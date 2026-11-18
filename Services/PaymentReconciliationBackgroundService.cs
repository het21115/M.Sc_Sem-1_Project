using IPOInvestmentManagement.Services;

namespace IPOInvestmentManagement.Services
{
    public class PaymentReconciliationBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PaymentReconciliationBackgroundService> _logger;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

        public PaymentReconciliationBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<PaymentReconciliationBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Payment Reconciliation Background Service is starting.");

            // Initial short delay to allow app to finish startup
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (IServiceScope scope = _scopeFactory.CreateScope())
                    {
                        var reconciliationService = scope.ServiceProvider.GetRequiredService<IPaymentReconciliationService>();
                        var report = await reconciliationService.ReconcileAllPaymentsAsync(stoppingToken);

                        if (report.ReconciledCaptured > 0 || report.ReconciledFailed > 0 || report.ApplicationsAutoCreated > 0 || report.ReconciledUnblocks > 0)
                        {
                            _logger.LogInformation(
                                "Payment Reconciliation completed: {Captured} captured, {Failed} failed, {Apps} apps auto-created, {Unblocks} unblocks reconciled.",
                                report.ReconciledCaptured,
                                report.ReconciledFailed,
                                report.ApplicationsAutoCreated,
                                report.ReconciledUnblocks);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in Payment Reconciliation Background Service");
                }

                try
                {
                    await Task.Delay(CheckInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger.LogInformation("Payment Reconciliation Background Service is stopping.");
        }
    }
}
