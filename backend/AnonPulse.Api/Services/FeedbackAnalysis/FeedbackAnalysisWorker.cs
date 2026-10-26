using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.FeedbackAnalysis;

public sealed class FeedbackAnalysisWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<FeedbackAnalysisWorker> logger) : BackgroundService
{
    private readonly AiOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return Task.CompletedTask;
        return Task.WhenAll(Enumerable.Range(0, _options.MaxConcurrentJobs)
            .Select(_ => RunLoopAsync(stoppingToken)));
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                FeedbackAnalysisLease? lease = await scope.ServiceProvider
                    .GetRequiredService<IFeedbackAnalysisJobQueue>()
                    .ClaimNextAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.WorkerPollSeconds),
                        timeProvider, stoppingToken);
                    continue;
                }
                await scope.ServiceProvider.GetRequiredService<IFeedbackAnalysisJobProcessor>()
                    .ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Feedback analysis worker iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(_options.WorkerPollSeconds),
                    timeProvider, stoppingToken);
            }
        }
    }
}
