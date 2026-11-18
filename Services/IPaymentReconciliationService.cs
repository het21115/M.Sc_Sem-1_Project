using IPOInvestmentManagement.Models;

namespace IPOInvestmentManagement.Services
{
    public interface IPaymentReconciliationService
    {
        Task<PaymentReconciliationReport> ReconcileAllPaymentsAsync(CancellationToken cancellationToken = default);
        Task<bool> ReconcilePaymentRecordAsync(int paymentRecordId, CancellationToken cancellationToken = default);
        Task<int> ProcessPendingWebhookRetriesAsync(CancellationToken cancellationToken = default);
        Task<int> ReconcilePendingUnblocksAsync(CancellationToken cancellationToken = default);
        Task<PaymentWebhookLog> LogAndProcessWebhookAsync(string eventId, string eventType, string? orderId, string? paymentId, string payload);
    }
}
