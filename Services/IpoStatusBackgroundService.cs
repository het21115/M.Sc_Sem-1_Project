using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public class IpoStatusBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<IpoStatusBackgroundService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

        public IpoStatusBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<IpoStatusBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("IPO Status Background Synchronization Service started. Check interval: {Interval}s.", Interval.TotalSeconds);

            // Initial synchronization run shortly after startup
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var statusService = scope.ServiceProvider.GetRequiredService<IIpoStatusService>();
                        var result = await statusService.SynchronizeIpoStatusesAsync(stoppingToken);

                        if (result.TotalUpdated > 0)
                        {
                            _logger.LogInformation(
                                "IPO Status Background Service synchronized {UpdatedCount}/{EvaluatedCount} IPOs at {Timestamp:O}.",
                                result.TotalUpdated, result.TotalEvaluated, result.TimestampIst);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in IPO Status Background Synchronization Service.");
                }

                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger.LogInformation("IPO Status Background Synchronization Service stopped.");
        }
    }
}
