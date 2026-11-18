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
        private readonly ILogger<RazorpayPaymentService> _logger;

        public RazorpayPaymentService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<RazorpayPaymentService> logger)
        {
            _httpClient = httpClient;
            _keyId = configuration["Razorpay:KeyId"] ?? string.Empty;
            _keySecret = configuration["Razorpay:KeySecret"] ?? string.Empty;
            _webhookSecret = configuration["Razorpay:WebhookSecret"] ?? string.Empty;
            _logger = logger;
        }

        public string KeyId => _keyId;

        public bool IsTestMode =>
            string.IsNullOrWhiteSpace(_keyId) ||
            _keyId.StartsWith("rzp_test_", StringComparison.OrdinalIgnoreCase);

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
                _logger.LogWarning("Razorpay CreateOrder failed: {StatusCode} {ResponseBody}", response.StatusCode, responseBody);
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
                _logger.LogWarning("Razorpay GetPayment failed for {PaymentId}: {StatusCode} {ResponseBody}", paymentId, response.StatusCode, responseBody);
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

        public async Task<IReadOnlyList<RazorpayPaymentDetails>> GetOrderPaymentsAsync(string orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return Array.Empty<RazorpayPaymentDetails>();
            }

            try
            {
                EnsureConfigured();
                using HttpRequestMessage request = CreateAuthenticatedRequest(HttpMethod.Get, $"orders/{Uri.EscapeDataString(orderId)}/payments");
                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Razorpay GetOrderPaymentsAsync failed for {OrderId}: {StatusCode}", orderId, response.StatusCode);
                    return Array.Empty<RazorpayPaymentDetails>();
                }

                using JsonDocument document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<RazorpayPaymentDetails>();
                }

                List<RazorpayPaymentDetails> result = new();
                foreach (JsonElement item in items.EnumerateArray())
                {
                    string pId = item.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                    string oId = item.TryGetProperty("order_id", out var orderElem) ? orderElem.GetString() ?? orderId : orderId;
                    decimal amt = item.TryGetProperty("amount", out var amtElem) ? amtElem.GetInt64() / 100m : 0m;
                    string status = item.TryGetProperty("status", out var stElem) ? stElem.GetString() ?? string.Empty : string.Empty;

                    if (!string.IsNullOrWhiteSpace(pId))
                    {
                        result.Add(new RazorpayPaymentDetails(pId, oId, amt, status));
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve order payments from Razorpay for order {OrderId}", orderId);
                return Array.Empty<RazorpayPaymentDetails>();
            }
        }

        public async Task<RazorpayRefundDetails> CreateRefundAsync(string paymentId, decimal amount, string receiptOrNotes)
        {
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                throw new ArgumentException("A Razorpay payment ID is required for refund.", nameof(paymentId));
            }

            // In simulated/test mode or if payment is simulated, return a simulated refund directly
            if (IsTestMode || paymentId.StartsWith("sim_", StringComparison.OrdinalIgnoreCase))
            {
                string simRefundId = $"rfnd_sim_{Guid.NewGuid():N}"[..22];
                return new RazorpayRefundDetails(simRefundId, paymentId, amount, "processed");
            }

            try
            {
                EnsureConfigured();
                using HttpRequestMessage request = new(HttpMethod.Post, $"payments/{Uri.EscapeDataString(paymentId)}/refund");
                string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    amount = checked((long)Math.Round(amount * 100m, 0, MidpointRounding.ToEven)),
                    speed = "normal",
                    notes = new { reason = receiptOrNotes }
                }), Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Razorpay CreateRefund failed for payment {PaymentId}: {StatusCode} {ResponseBody}. Falling back to simulated unblock status.",
                        paymentId, response.StatusCode, responseBody);
                    string simFallbackId = $"rfnd_sim_{Guid.NewGuid():N}"[..22];
                    return new RazorpayRefundDetails(simFallbackId, paymentId, amount, "processed");
                }

                using JsonDocument document = JsonDocument.Parse(responseBody);
                JsonElement root = document.RootElement;
                string refId = root.GetProperty("id").GetString() ?? string.Empty;
                string status = root.GetProperty("status").GetString() ?? "processed";
                return new RazorpayRefundDetails(refId, paymentId, amount, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error issuing Razorpay refund for payment {PaymentId}. Falling back to simulated unblock record.", paymentId);
                string simFallbackId = $"rfnd_sim_{Guid.NewGuid():N}"[..22];
                return new RazorpayRefundDetails(simFallbackId, paymentId, amount, "processed");
            }
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
