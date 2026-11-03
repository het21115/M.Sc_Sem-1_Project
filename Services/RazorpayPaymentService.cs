using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPOInvestmentManagement.Services
{
    public class RazorpayPaymentService : IRazorpayPaymentService
    {
        private readonly HttpClient _httpClient;
        private readonly string _keyId;
        private readonly string _keySecret;

        public RazorpayPaymentService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _keyId = configuration["Razorpay:KeyId"] ?? string.Empty;
            _keySecret = configuration["Razorpay:KeySecret"] ?? string.Empty;
        }

        public string KeyId => _keyId;

        public async Task<string> CreateOrderAsync(decimal amount, string receipt)
        {
            EnsureConfigured();
            using HttpRequestMessage request = new(HttpMethod.Post, "orders");
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                amount = checked((long)Math.Round(amount * 100m, 0, MidpointRounding.ToEven)),
                currency = "INR",
                receipt,
                payment_capture = 1
            }), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Razorpay could not create the payment order.");
            }

            using JsonDocument document = JsonDocument.Parse(responseBody);
            return document.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Razorpay returned an invalid payment order.");
        }

        public bool VerifyPaymentSignature(string orderId, string paymentId, string signature)
        {
            EnsureConfigured();
            string payload = $"{orderId}|{paymentId}";
            using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(_keySecret));
            byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            byte[] supplied = Convert.FromHexString(signature);
            return CryptographicOperations.FixedTimeEquals(digest, supplied);
        }

        private void EnsureConfigured()
        {
            if (string.IsNullOrWhiteSpace(_keyId) || string.IsNullOrWhiteSpace(_keySecret))
            {
                throw new InvalidOperationException("Razorpay is not configured. Set Razorpay:KeyId and Razorpay:KeySecret.");
            }
        }
    }
}
