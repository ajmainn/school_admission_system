using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Domain;
using Xunit;

namespace SchoolAdmission.Tests;

public sealed class AdmissionTests(ClassApiFixture fixture) : IClassFixture<ClassApiFixture>, IAsyncLifetime
{
    private const string Student = "10000000000000000";

    public async Task InitializeAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
        await db.Holds.ExecuteDeleteAsync();
        await db.Classes.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(50)]
    [InlineData(1000)]
    public async Task ConcurrentHoldsNeverExceedCapacity(int attempts)
    {
        var classId = await CreateClass(5);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, attempts).Select(async i =>
        {
            await start.Task;
            return await Hold(classId, (10000000000000000L + i).ToString());
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(5, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
            Assert.Equal(attempts - 5, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            var seats = await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{classId}");
            Assert.NotNull(seats);
            Assert.Equal(5, seats.HeldSeats);
            Assert.Equal(0, seats.ConfirmedSeats);
            Assert.Equal(0, seats.AvailableSeats);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var holds = await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.ToListAsync();
            Assert.Equal(5, holds.Count);
            Assert.All(holds, hold => Assert.Equal(HoldStatus.HELD, hold.Status));
            Assert.Equal(5, holds.Select(h => h.StudentId).Distinct().Count());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task SameStudentCannotCreateDuplicateActiveHold()
    {
        var classId = await CreateClass(50);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 50).Select(async _ =>
        {
            await start.Task;
            return await Hold(classId, Student);
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            Assert.Equal(49, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            Assert.Single(await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.ToListAsync());
            var otherClass = await CreateClass(1);
            using var other = await Hold(otherClass, Student);
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task CreatedHoldHasExactContractUtcDeadlineAndDefaultDuration()
    {
        var classId = await CreateClass(1);
        using var response = await Hold(classId, Student);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "classId", "expiresAt", "holdId", "status", "studentId" },
            json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.EndsWith("Z", json.RootElement.GetProperty("expiresAt").GetString());
        var hold = await response.Content.ReadFromJsonAsync<CreatedHoldResponse>();
        Assert.NotNull(hold);
        Assert.Matches("^h_[0-9a-f]{32}$", hold.HoldId);
        Assert.Equal(classId, hold.ClassId);
        Assert.Equal(Student, hold.StudentId);
        Assert.Equal("HELD", hold.Status);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.SingleAsync();
        Assert.Equal(hold.HoldId, stored.Id);
        Assert.Equal(Student, stored.StudentId);
        Assert.Equal(TimeSpan.FromSeconds(600), stored.ExpiresAtUtc - stored.CreatedAtUtc);
        Assert.Equal(stored.ExpiresAtUtc.UtcDateTime, hold.ExpiresAt, TimeSpan.FromMicroseconds(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567890123456")]
    [InlineData("123456789012345678")]
    [InlineData("1234567890123456a")]
    [InlineData("１２３４５６７８９０１２３４５６７")]
    [InlineData("١٢٣٤٥٦٧٨٩٠١٢٣٤٥٦٧")]
    [InlineData("10000000000000000,10000000000000001")]
    public async Task InvalidStudentIdReturns400WithoutSaving(string? studentId)
    {
        var classId = await CreateClass(1);
        using var response = await Hold(classId, studentId);
        await AssertError(response, HttpStatusCode.BadRequest);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.AnyAsync());
    }

    [Fact]
    public async Task InvalidAndUnknownClassIdsAreRejected()
    {
        using var invalid = await Hold("invalid", Student);
        await AssertError(invalid, HttpStatusCode.BadRequest);
        using var missing = await Hold($"c_{Guid.NewGuid():N}", Student);
        await AssertError(missing, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(HoldStatus.HELD, false, false)]
    [InlineData(HoldStatus.CONFIRMED, true, false)]
    [InlineData(HoldStatus.HELD, true, true)]
    [InlineData(HoldStatus.EXPIRED, true, true)]
    [InlineData(HoldStatus.CANCELLED, true, true)]
    public async Task ExistingApplicationStatusControlsCapacityAndReapplication(
        HoldStatus status, bool pastDeadline, bool allowed)
    {
        var classId = await CreateClass(1);
        var oldId = $"h_{Guid.NewGuid():N}";
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
            db.Holds.Add(new AdmissionHold
            {
                Id = oldId, ClassId = classId, StudentId = Student, Status = status,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(pastDeadline ? -1 : 1)
            });
            await db.SaveChangesAsync();
        }
        using var sameStudent = await Hold(classId, Student);
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.Conflict, sameStudent.StatusCode);
        using var otherStudent = await Hold(classId, "10000000000000001");
        await AssertError(otherStudent, HttpStatusCode.Conflict);

        await using var check = fixture.Factory.Services.CreateAsyncScope();
        var records = await check.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.ToListAsync();
        Assert.Equal(allowed ? 2 : 1, records.Count);
        Assert.Equal(status == HoldStatus.HELD && pastDeadline ? HoldStatus.EXPIRED : status,
            records.Single(h => h.Id == oldId).Status);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("60")]
    public async Task ConfiguredDurationIsPersisted(string duration)
    {
        var classId = await CreateClass(1);
        await using var factory = fixture.CreateFactory(duration);
        using var client = factory.CreateClient();
        using var response = await Hold(classId, Student, client);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds.SingleAsync();
        Assert.Equal(TimeSpan.FromSeconds(int.Parse(duration)), stored.ExpiresAtUtc - stored.CreatedAtUtc);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task InvalidDurationPreventsStartup(string duration)
    {
        await using var factory = fixture.CreateFactory(duration);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("HOLD_DURATION_SECONDS", exception.Message);
    }

    [Fact]
    public async Task ExpiredHoldReleasesSeatWithoutApiCalls()
    {
        var classId = await CreateClass(1);
        await using var factory = fixture.CreateFactory("1", runWorker: true);
        using var client = factory.CreateClient();
        using var response = await Hold(classId, Student, client);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatedHoldResponse>())!;

        // No API calls after reservation until the actual background worker persists EXPIRED.
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        HoldStatus status;
        do
        {
            await using var scope = factory.Services.CreateAsyncScope();
            status = await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Holds
                .Where(h => h.Id == created.HoldId).Select(h => h.Status).SingleAsync();
            if (status == HoldStatus.EXPIRED) break;
            await Task.Delay(100);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(HoldStatus.EXPIRED, status);

        var seats = await client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{classId}");
        Assert.Equal(0, seats!.HeldSeats);
        Assert.Equal(1, seats.AvailableSeats);
        using var replacement = await Hold(classId, Student, client);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
    }

    private async Task<string> CreateClass(int seats)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/classes",
            new { name = "Class 6 - Morning Shift", totalSeats = seats });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedClassResponse>())!.Id;
    }

    private async Task<HttpResponseMessage> Hold(string classId, string? studentId, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/classes/{classId}/holds");
        if (studentId is not null) request.Headers.TryAddWithoutValidation("X-Student-Id", studentId);
        return await (client ?? fixture.Client).SendAsync(request);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Error));
    }
}
