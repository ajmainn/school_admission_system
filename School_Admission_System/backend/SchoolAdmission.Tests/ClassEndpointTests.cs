using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Domain;
using SchoolAdmission.Api.Workers;
using Testcontainers.PostgreSql;
using Xunit;

namespace SchoolAdmission.Tests;

// A disposable real PostgreSQL database; these tests never use the Compose database.
public sealed class ClassApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    // Leave PostgreSQL connection headroom for EF's separate migration checks.
    private string ConnectionString => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { MaxPoolSize = 50 }.ConnectionString;

    public WebApplicationFactory<Program> CreateFactory(string? duration = null, bool runWorker = true)
        => new ClassApiFactory(ConnectionString, duration, runWorker);

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        // Seeded deadline tests control expiration explicitly. Worker tests opt into the real worker.
        Factory = new ClassApiFactory(ConnectionString, runWorker: false);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null) await Factory.DisposeAsync();
        await postgres.DisposeAsync();
    }

    private sealed class ClassApiFactory(string connectionString, string? duration = null, bool runWorker = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            if (duration is not null) builder.UseSetting("HOLD_DURATION_SECONDS", duration);
            if (!runWorker)
                builder.ConfigureTestServices(services =>
                {
                    var worker = services.Single(s => s.ImplementationType == typeof(ExpiredHoldWorker));
                    services.Remove(worker);
                });
        }
    }
}

public sealed class ClassEndpointTests(ClassApiFixture fixture)
    : IClassFixture<ClassApiFixture>, IAsyncLifetime
{
    // xUnit serializes tests in this class. Each case starts with empty tables.
    public async Task InitializeAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
        await db.Holds.ExecuteDeleteAsync();
        await db.Classes.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreatePersistsTrimmedNameAndReturnsExactContracts()
    {
        using var response = await fixture.Client.PostAsJsonAsync("/classes",
            new { name = "  Class 6 - Morning Shift  ", totalSeats = 5 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedClassResponse>();
        Assert.NotNull(created);
        Assert.Matches("^c_[0-9a-f]{32}$", created.Id);
        Assert.Equal("Class 6 - Morning Shift", created.Name);
        Assert.Equal(5, created.TotalSeats);
        await AssertFields(response, "id", "name", "totalSeats");
        Assert.Equal($"/classes/{created.Id}", response.Headers.Location!.AbsolutePath);

        using var details = await fixture.Client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var seats = await details.Content.ReadFromJsonAsync<ClassSeatsResponse>();
        Assert.Equal(new ClassSeatsResponse(created.Id, created.Name, 5, 0, 0, 5), seats);
        await AssertFields(details, "id", "name", "totalSeats", "heldSeats", "confirmedSeats", "availableSeats");

        using var list = await fixture.Client.GetAsync("/classes");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(seats, Assert.Single((await list.Content.ReadFromJsonAsync<ClassSeatsResponse[]>())!));

        // Read through a fresh context to prove the POST persisted its values.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>()
            .Classes.SingleAsync();
        Assert.Equal(created.Name, stored.Name);
        Assert.Equal(5, stored.TotalSeats);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("   ", 5)]
    [InlineData("\t\r\n", 5)]
    [InlineData("Class 6", 0)]
    [InlineData("Class 6", -1)]
    public async Task InvalidCreationReturns400WithoutSaving(string? name, int totalSeats)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/classes", new { name, totalSeats });
        await AssertBadRequestWithoutSaving(response);
    }

    [Fact]
    public async Task NameLengthMatchesDatabaseLimitAfterTrimming()
    {
        using var invalid = await fixture.Client.PostAsJsonAsync("/classes",
            new { name = new string('x', 201), totalSeats = 1 });
        await AssertBadRequestWithoutSaving(invalid);
        using var valid = await fixture.Client.PostAsJsonAsync("/classes",
            new { name = " " + new string('x', 200) + " ", totalSeats = 1 });
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Class 6\",\"totalSeats\":1.5}")]
    [InlineData("{\"name\":\"Class 6\",\"totalSeats\":2147483648}")]
    [InlineData("{bad json")]
    public async Task InvalidBodyReturns400WithoutSaving(string body)
    {
        using var response = await fixture.Client.PostAsync("/classes",
            new StringContent(body, Encoding.UTF8, "application/json"));
        await AssertBadRequestWithoutSaving(response);
    }

    [Fact]
    public async Task EmptyListAndMissingOrMalformedIdsHaveExpectedResponses()
    {
        Assert.Empty((await fixture.Client.GetFromJsonAsync<ClassSeatsResponse[]>("/classes"))!);
        using var missing = await fixture.Client.GetAsync($"/classes/c_{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await missing.Content.ReadFromJsonAsync<ErrorResponse>())?.Error));
        using var malformed = await fixture.Client.GetAsync("/classes/invalid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(3, 0)]
    [InlineData(1, 0)]
    public async Task CountsUseDatabaseStatusesAndDeadlinesAndAvailabilityNeverGoesNegative(
        int capacity, int available)
    {
        var id = $"c_{Guid.NewGuid():N}";
        var otherId = $"c_{Guid.NewGuid():N}";
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
            db.Classes.Add(new AdmissionClass { Id = id, Name = "Class 6 - Day Shift", TotalSeats = capacity });
            db.Classes.Add(new AdmissionClass { Id = otherId, Name = "Class 6 - Morning Shift", TotalSeats = 10 });
            var now = DateTimeOffset.UtcNow;
            var cases = new[]
            {
                (HoldStatus.HELD, now.AddDays(1)),
                (HoldStatus.HELD, now.AddDays(1)),
                (HoldStatus.HELD, now.AddDays(-1)), // overdue but not yet processed by a worker
                (HoldStatus.CONFIRMED, now.AddDays(-1)),
                (HoldStatus.EXPIRED, now.AddDays(1)),
                (HoldStatus.CANCELLED, now.AddDays(1))
            };
            for (var i = 0; i < cases.Length; i++)
                db.Holds.Add(new AdmissionHold
                {
                    Id = $"h_{Guid.NewGuid():N}", ClassId = id,
                    StudentId = (10000000000000000L + i).ToString(),
                    Status = cases[i].Item1, ExpiresAtUtc = cases[i].Item2
                });
            await db.SaveChangesAsync();
        }

        var expected = new ClassSeatsResponse(id, "Class 6 - Day Shift", capacity, 2, 1, available);
        Assert.Equal(expected, await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{id}"));
        var listed = await fixture.Client.GetFromJsonAsync<ClassSeatsResponse[]>("/classes");
        Assert.Equal(2, listed!.Length);
        Assert.Equal(expected, Assert.Single(listed, x => x.Id == id));
        var other = new ClassSeatsResponse(otherId, "Class 6 - Morning Shift", 10, 0, 0, 10);
        Assert.Equal(other, Assert.Single(listed, x => x.Id == otherId));
        Assert.Equal(other, await fixture.Client.GetFromJsonAsync<ClassSeatsResponse>($"/classes/{otherId}"));
    }

    private async Task AssertBadRequestWithoutSaving(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await response.Content.ReadFromJsonAsync<ErrorResponse>())?.Error));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Classes.AnyAsync());
    }

    private static async Task AssertFields(HttpResponseMessage response, params string[] names)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(names.OrderBy(x => x), json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }
}
