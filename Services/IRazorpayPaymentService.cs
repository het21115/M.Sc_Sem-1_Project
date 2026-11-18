namespace IPOInvestmentManagement.Services
{
    public interface IRazorpayPaymentService
    {
        string KeyId { get; }
        bool IsTestMode { get; }
        Task<string> CreateOrderAsync(decimal amount, string receipt);
        Task<RazorpayPaymentDetails> GetPaymentAsync(string paymentId);
        Task<IReadOnlyList<RazorpayPaymentDetails>> GetOrderPaymentsAsync(string orderId);
        Task<RazorpayRefundDetails> CreateRefundAsync(string paymentId, decimal amount, string receiptOrNotes);
        bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
        bool VerifyWebhookSignature(string payload, string signature);
    }

    public sealed record RazorpayPaymentDetails(
        string PaymentId,
        string OrderId,
        decimal Amount,
        string Status);

    public sealed record RazorpayRefundDetails(
        string RefundId,
        string PaymentId,
        decimal Amount,
        string Status);
}
