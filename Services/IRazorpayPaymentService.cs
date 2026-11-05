namespace IPOInvestmentManagement.Services
{
    public interface IRazorpayPaymentService
    {
        string KeyId { get; }
        Task<string> CreateOrderAsync(decimal amount, string receipt);
        Task<RazorpayPaymentDetails> GetPaymentAsync(string paymentId);
        bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
        bool VerifyWebhookSignature(string payload, string signature);
    }

    public sealed record RazorpayPaymentDetails(
        string PaymentId,
        string OrderId,
        decimal Amount,
        string Status);
}
