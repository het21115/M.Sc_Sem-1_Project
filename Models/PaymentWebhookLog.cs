using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("PaymentWebhookLogs")]
    public class PaymentWebhookLog
    {
        [Key]
        public int WebhookLogId { get; set; }

        [Required, StringLength(100)]
        public string EventId { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string EventType { get; set; } = string.Empty;

        [StringLength(50)]
        public string? RazorpayOrderId { get; set; }

        [StringLength(50)]
        public string? RazorpayPaymentId { get; set; }

        public string Payload { get; set; } = string.Empty;

        [Required, StringLength(30)]
        public string Status { get; set; } = "Received"; // Received, Processed, Duplicate, Failed, SignatureFailed

        public int RetryCount { get; set; }

        [StringLength(1000)]
        public string? ErrorMessage { get; set; }

        public DateTime ReceivedAt { get; set; }

        public DateTime? ProcessedAt { get; set; }
    }
}
