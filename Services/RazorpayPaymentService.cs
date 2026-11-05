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
        private readonly string _webhookSecret;

        public RazorpayPaymentService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _keyId = configuration["Razorpay:KeyId"] ?? string.Empty;
            _keySecret = configuration["Razorpay:KeySecret"] ?? string.Empty;
            _webhookSecret = configuration["Razorpay:WebhookSecret"] ?? string.Empty;
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

        public async Task<RazorpayPaymentDetails> GetPaymentAsync(string paymentId)
        {
            EnsureConfigured();
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                throw new ArgumentException("A Razorpay payment ID is required.", nameof(paymentId));
            }

            using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, $"payments/{Uri.EscapeDataString(paymentId)}");
            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("Razorpay could not retrieve the payment details.");
            }

            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement payment = document.RootElement;
            return new RazorpayPaymentDetails(
                payment.GetProperty("id").GetString() ?? string.Empty,
                payment.GetProperty("order_id").GetString() ?? string.Empty,
                payment.GetProperty("amount").GetInt64() / 100m,
                payment.GetProperty("status").GetString() ?? string.Empty);
        }

        public bool VerifyPaymentSignature(string orderId, string paymentId, string signature)
        {
            EnsureConfigured();
            string payload = $"{orderId}|{paymentId}";
            using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(_keySecret));
            byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            byte[] supplied;
            try
            {
                supplied = Convert.FromHexString(signature);
            }
            catch (FormatException)
            {
                return false;
            }
            return CryptographicOperations.FixedTimeEquals(digest, supplied);
        }

        public bool VerifyWebhookSignature(string payload, string signature)
        {
            if (string.IsNullOrWhiteSpace(_webhookSecret) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(_webhookSecret));
            byte[] digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            byte[] supplied;
            try
            {
                supplied = Convert.FromHexString(signature);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(digest, supplied);
        }

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string uri)
        {
            HttpRequestMessage request = new(method, uri);
            string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return request;
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
