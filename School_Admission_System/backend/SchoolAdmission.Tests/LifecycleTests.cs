using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Domain;
using SchoolAdmission.Api.Services;
using Xunit;

namespace SchoolAdmission.Tests;

public sealed class LifecycleTests(ClassApiFixture fixture) : IClassFixture<ClassApiFixture>, IAsyncLifetime
{
    private const string Owner = "10000000000000000";
    private const string Other = "10000000000000001";

    public async Task InitializeAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
        await db.Holds.ExecuteDeleteAsync();
        await db.Classes.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DetailsAndConfirmationMatchContractsAndRepeatedConfirmationIsSafe()
    {
        var hold = await Reserve();
        using var details = await Send(HttpMethod.Get, $"/holds/{hold.HoldId}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        await Fields(details, "holdId", "classId", "status", "expiresAt");
        using (var json = JsonDocument.Parse(await details.Content.ReadAsStringAsync()))
        {
            Assert.Equal("HELD", json.RootElement.GetProperty("status").GetString());
            Assert.EndsWith("Z", json.RootElement.GetProperty("expiresAt").GetString());
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 20).Select(async _ =>
        {
            await gate.Task;
            return await Send(HttpMethod.Post, $"/holds/{hold.HoldId}/confirm", Owner);
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);
        foreach (var response in results)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await Fields(response, "holdId", "status");
                Assert.Equal(new ConfirmHoldResponse(hold.HoldId, "CONFIRMED"),
                    await response.Content.ReadFromJsonAsync<ConfirmHoldResponse>());
            }
        }
        var stored = await ReadHold(hold.HoldId);
        Assert.Equal(HoldStatus.CONFIRMED, stored.Status);
        Assert.NotNull(stored.ConfirmedAtUtc);
        Assert.Null(stored.CancelledAtUtc);
        using var repeat = await Send(HttpMethod.Post, $"/holds/{hold.HoldId}/confirm", Owner);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(stored.ConfirmedAtUtc, (await ReadHold(hold.HoldId)).ConfirmedAtUtc);
        using var cancel = await Send(HttpMethod.Delete, $"/holds/{hold.HoldId}", Owner);
        await Error(cancel, HttpStatusCode.Conflict);
        var seats = await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{hold.ClassId}");
        Assert.Equal(1, seats!.ConfirmedSeats);
        Assert.Equal(0, seats.HeldSeats);
        Assert.Equal(0, seats.AvailableSeats);
    }

    [Fact]
    public async Task CancellationReturnsEmpty204ReleasesSeatAndCannotBeConfirmedOrCancelledAgain()
    {
        var hold = await Reserve();
        using var response = await Send(HttpMethod.Delete, $"/holds/{hold.HoldId}", Owner);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var stored = await ReadHold(hold.HoldId);
        Assert.Equal(HoldStatus.CANCELLED, stored.Status);
        Assert.NotNull(stored.CancelledAtUtc);
        Assert.Null(stored.ConfirmedAtUtc);
        using var repeat = await Send(HttpMethod.Delete, $"/holds/{hold.HoldId}", Owner);
        await Error(repeat, HttpStatusCode.Conflict);
        using var confirm = await Send(HttpMethod.Post, $"/holds/{hold.HoldId}/confirm", Owner);
        await Error(confirm, HttpStatusCode.Conflict);
        var seats = await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{hold.ClassId}");
        Assert.Equal(1, seats!.AvailableSeats);
        using var replacement = await Send(HttpMethod.Post, $"/classes/{hold.ClassId}/holds", Owner);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("cancel")]
    public async Task OnlyOwnerCanChangeHold(string action)
    {
        var hold = await Reserve();
        using var response = await Act(action, hold.HoldId, Other);
        await Error(response, HttpStatusCode.NotFound);
        var stored = await ReadHold(hold.HoldId);
        Assert.Equal(HoldStatus.HELD, stored.Status);
        Assert.Null(stored.ConfirmedAtUtc);
        Assert.Null(stored.CancelledAtUtc);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("cancel")]
    public async Task ExpiredPaymentOrCancellationIsRejectedBeforeWorkerRuns(string action)
    {
        var hold = await Reserve();
        await MakeOverdue(hold.HoldId);
        Assert.Equal(HoldStatus.HELD, (await ReadHold(hold.HoldId)).Status);
        var details = await fixture.Client.GetFromJsonAsync<HoldDetailsResponse>($"/holds/{hold.HoldId}");
        Assert.Equal("EXPIRED", details!.Status);
        // Read reports logical expiry without doing the worker's job.
        Assert.Equal(HoldStatus.HELD, (await ReadHold(hold.HoldId)).Status);
        using var response = await Act(action, hold.HoldId, Owner);
        await Error(response, HttpStatusCode.Conflict);
        var stored = await ReadHold(hold.HoldId);
        Assert.Equal(HoldStatus.EXPIRED, stored.Status);
        Assert.Null(stored.ConfirmedAtUtc);
        Assert.Null(stored.CancelledAtUtc);
    }

    [Fact]
    public async Task ConfirmationRechecksDeadlineAfterWaitingForClassLock()
    {
        var hold = await Reserve();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Classes.FromSqlInterpolated($"SELECT * FROM \"Classes\" WHERE \"Id\" = {hold.ClassId} FOR UPDATE")
            .AsNoTracking().SingleAsync();
        var confirmation = Act("confirm", hold.HoldId, Owner);

        // Observe the request waiting in PostgreSQL, rather than guessing with a sleep.
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        var waiting = false;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            // PostgreSQL caches activity statistics within a transaction until cleared.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_stat_clear_snapshot()");
            waiting = await db.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() " +
                "AND wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%') AS \"Value\"").SingleAsync();
            if (waiting) break;
            await Task.Delay(25);
        }
        await db.Holds.Where(h => h.Id == hold.HoldId).ExecuteUpdateAsync(s =>
            s.SetProperty(h => h.ExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await transaction.CommitAsync();
        using var response = await confirmation;
        Assert.True(waiting, "Confirmation did not reach the database lock within the timeout.");
        await Error(response, HttpStatusCode.Conflict);
        Assert.Equal(HoldStatus.EXPIRED, (await ReadHold(hold.HoldId)).Status);
    }

    [Fact]
    public async Task MyHoldsIncludesOnlyOwnerWithAllStatusesAndClassName()
    {
        var owned = new List<CreatedHoldResponse>();
        for (var i = 0; i < 4; i++) owned.Add(await Reserve());
        using (var confirmed = await Act("confirm", owned[1].HoldId, Owner))
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using (var cancelled = await Act("cancel", owned[2].HoldId, Owner))
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        await MakeOverdue(owned[3].HoldId);
        var other = await Reserve(Other);

        using var response = await Send(HttpMethod.Get, "/me/holds", Owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = (await response.Content.ReadFromJsonAsync<StudentHoldResponse[]>())!;
        Assert.Equal(4, history.Length);
        Assert.Equal(new[] { "CANCELLED", "CONFIRMED", "EXPIRED", "HELD" }, history.Select(h => h.Status).OrderBy(x => x));
        Assert.DoesNotContain(history, h => h.HoldId == other.HoldId);
        Assert.All(history, h => Assert.Equal("Class 6 - Morning Shift", h.ClassName));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var item in json.RootElement.EnumerateArray())
        {
            Assert.Equal(new[] { "classId", "className", "expiresAt", "holdId", "status" },
                item.EnumerateObject().Select(p => p.Name).OrderBy(x => x));
            Assert.EndsWith("Z", item.GetProperty("expiresAt").GetString());
        }
        using var empty = await Send(HttpMethod.Get, "/me/holds", "10000000000000002");
        Assert.Empty((await empty.Content.ReadFromJsonAsync<StudentHoldResponse[]>())!);
    }

    [Theory]
    [InlineData("confirm", null)]
    [InlineData("confirm", "123")]
    [InlineData("confirm", "１２３４５６７８９０１２３４５６７")]
    [InlineData("cancel", null)]
    [InlineData("cancel", "123")]
    [InlineData("cancel", "1000000000000000a")]
    [InlineData("history", null)]
    [InlineData("history", "123")]
    [InlineData("history", "10000000000000000,10000000000000001")]
    public async Task StudentRoutesValidateHeader(string action, string? student)
    {
        var hold = await Reserve();
        using var response = action == "history"
            ? await Send(HttpMethod.Get, "/me/holds", student)
            : await Act(action, hold.HoldId, student);
        await Error(response, HttpStatusCode.BadRequest);
        Assert.Equal(HoldStatus.HELD, (await ReadHold(hold.HoldId)).Status);
    }

    [Theory]
    [InlineData("details")]
    [InlineData("confirm")]
    [InlineData("cancel")]
    public async Task HoldRoutesValidateIdsAndReturn404ForUnknownHolds(string action)
    {
        foreach (var id in new[] { "invalid", $"h_{Guid.NewGuid():N}" })
        {
            using var response = action == "details" ? await Send(HttpMethod.Get, $"/holds/{id}") : await Act(action, id, Owner);
            await Error(response, id == "invalid" ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task ConcurrentConfirmationAndCancellationHaveOneConsistentOutcome()
    {
        var hold = await Reserve();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 20).Select(async i =>
        {
            await gate.Task;
            return await Act(i % 2 == 0 ? "confirm" : "cancel", hold.HoldId, Owner);
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(attempts);
        var stored = await ReadHold(hold.HoldId);
        try
        {
            Assert.Contains(stored.Status, new[] { HoldStatus.CONFIRMED, HoldStatus.CANCELLED });
            Assert.Equal(stored.Status == HoldStatus.CONFIRMED ? 10 : 0,
                responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(stored.Status == HoldStatus.CANCELLED ? 1 : 0,
                responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            Assert.All(responses, r => Assert.Contains(r.StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.Conflict }));
            Assert.Equal(stored.Status == HoldStatus.CONFIRMED, stored.ConfirmedAtUtc.HasValue);
            Assert.Equal(stored.Status == HoldStatus.CANCELLED, stored.CancelledAtUtc.HasValue);
            var seats = await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{hold.ClassId}");
            Assert.Equal(stored.Status == HoldStatus.CONFIRMED ? 1 : 0, seats!.ConfirmedSeats);
            Assert.Equal(0, seats.HeldSeats);
            Assert.Equal(stored.Status == HoldStatus.CANCELLED ? 1 : 0, seats.AvailableSeats);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ExpirationRacingPaymentCancellationAndReallocationNeverRevivesOldHold()
    {
        var hold = await Reserve();
        await MakeOverdue(hold.HoldId);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Expire()
        {
            await gate.Task;
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAdmissionService>().ExpireHoldsAsync(default);
        }
        async Task<HttpResponseMessage> Request(string action)
        {
            await gate.Task;
            return action == "allocate"
                ? await Send(HttpMethod.Post, $"/classes/{hold.ClassId}/holds", Other)
                : await Act(action, hold.HoldId, Owner);
        }
        var expiration = Expire();
        var confirm = Request("confirm");
        var cancel = Request("cancel");
        var allocate = Request("allocate");
        gate.SetResult();
        await Task.WhenAll(expiration, confirm, cancel, allocate);
        using var confirmation = await confirm;
        using var cancellation = await cancel;
        using var allocation = await allocate;
        await Error(confirmation, HttpStatusCode.Conflict);
        await Error(cancellation, HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Created, allocation.StatusCode);
        var old = await ReadHold(hold.HoldId);
        Assert.Equal(HoldStatus.EXPIRED, old.Status);
        Assert.Null(old.ConfirmedAtUtc);
        Assert.Null(old.CancelledAtUtc);
        var seats = await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{hold.ClassId}");
        Assert.Equal(1, seats!.HeldSeats);
        Assert.Equal(0, seats.ConfirmedSeats);
        Assert.Equal(0, seats.AvailableSeats);
    }

    private async Task<CreatedHoldResponse> Reserve(string student = Owner)
    {
        using var created = await fixture.Client.PostAsJsonAsync("/classes", new { name = "Class 6 - Morning Shift", totalSeats = 1 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var admissionClass = (await created.Content.ReadFromJsonAsync<CreatedClassResponse>())!;
        using var response = await Send(HttpMethod.Post, $"/classes/{admissionClass.Id}/holds", student);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedHoldResponse>())!;
    }

    private async Task MakeOverdue(string id)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.Where(h => h.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExpiresAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    private async Task<AdmissionHold> ReadHold(string id)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.AsNoTracking().SingleAsync(h => h.Id == id);
    }

    private Task<HttpResponseMessage> Act(string action, string id, string? student)
        => Send(action == "confirm" ? HttpMethod.Post : HttpMethod.Delete,
            $"/holds/{id}" + (action == "confirm" ? "/confirm" : ""), student);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? student = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (student is not null) request.Headers.TryAddWithoutValidation("X-Student-Id", student);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Error));
    }

    private static async Task Fields(HttpResponseMessage response, params string[] names)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(names.OrderBy(x => x), json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }
}
