using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public sealed class AiTokenBudgetReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<AiTokenBudgetReconciliationWorker> logger) : BackgroundService
{
    private readonly AiOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAiTokenBudgetManager>()
                    .ReconcileStaleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AI token budget reconciliation failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.WorkerPollSeconds), timeProvider,
                stoppingToken);
        }
    }
}
