using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.Extensions.Options;

namespace AnonPulse.Api.Services.KnowledgeGrading;

public sealed class KnowledgeGradingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AiOptions> options,
    TimeProvider timeProvider,
    ILogger<KnowledgeGradingWorker> logger) : BackgroundService
{
    private readonly AiOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.KnowledgeGradingEnabled) return Task.CompletedTask;
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
                KnowledgeGradingLease? lease = await scope.ServiceProvider.GetRequiredService<IKnowledgeGradingJobQueue>()
                    .ClaimNextAsync(stoppingToken);
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.WorkerPollSeconds),
                        timeProvider, stoppingToken);
                    continue;
                }
                await scope.ServiceProvider.GetRequiredService<IKnowledgeGradingJobProcessor>()
                    .ProcessAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge grading worker iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(_options.WorkerPollSeconds),
                    timeProvider, stoppingToken);
            }
        }
    }
}
