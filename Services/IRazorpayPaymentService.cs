namespace IPOInvestmentManagement.Services
{
    public interface IRazorpayPaymentService
    {
        string KeyId { get; }
        Task<string> CreateOrderAsync(decimal amount, string receipt);
        bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
    }
}
