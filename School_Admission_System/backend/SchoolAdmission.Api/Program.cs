using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using SchoolAdmission.Api.Contracts;
using SchoolAdmission.Api.Data;
using SchoolAdmission.Api.Services;
using SchoolAdmission.Api.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
        new BadRequestObjectResult(new ErrorResponse(string.Join(" ",
            context.ModelState.Values.SelectMany(value => value.Errors)
                .Select(error => string.IsNullOrEmpty(error.ErrorMessage)
                    ? "Invalid request body." : error.ErrorMessage))));
});

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=school_admission;Username=schoolapp;Password=change_me_for_local_dev;Maximum Pool Size=50";
builder.Services.AddDbContext<AdmissionDbContext>(options => options.UseNpgsql(connectionString));

var holdDurationSetting = builder.Configuration["HOLD_DURATION_SECONDS"] ?? "600";
if (!int.TryParse(holdDurationSetting, out var holdDurationSeconds) || holdDurationSeconds <= 0)
    throw new InvalidOperationException("HOLD_DURATION_SECONDS must be a positive integer.");
builder.Services.AddSingleton(new HoldOptions(holdDurationSeconds));

builder.Services.AddScoped<IAdmissionService, AdmissionService>();
builder.Services.AddHostedService<ExpiredHoldWorker>();

var origin = builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:3000";
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    policy.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Compose waits for PostgreSQL readiness. Apply schema changes before serving requests;
// a migration failure stops startup rather than running against an incomplete schema.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AdmissionDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors("Frontend");
app.MapControllers();

// Readiness checks the connection and both application tables, not business features.
app.MapGet("/health", async (AdmissionDbContext db, CancellationToken ct) =>
{
    try
    {
        await db.Classes.AnyAsync(ct);
        await db.Holds.AnyAsync(ct);
        return Results.Ok(new { status = "Healthy", ready = true });
    }
    catch (Exception exception) when (!ct.IsCancellationRequested)
    {
        app.Logger.LogWarning(exception, "Database readiness check failed.");
        return Results.Json(new { status = "Unhealthy", ready = false }, statusCode: 503);
    }
});

app.Run();

// For future integration tests with WebApplicationFactory if desired.
public partial class Program { }
