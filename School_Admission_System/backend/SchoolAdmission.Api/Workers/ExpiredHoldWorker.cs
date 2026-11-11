using SchoolAdmission.Api.Services;

namespace SchoolAdmission.Api.Workers;

public sealed class ExpiredHoldWorker(IServiceScopeFactory scopes, ILogger<ExpiredHoldWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IAdmissionService>().ExpireHoldsAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // A transient database failure must not permanently stop expiration.
                    logger.LogError(exception, "Hold expiration failed; retrying on the next interval.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
