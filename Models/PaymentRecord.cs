using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("PaymentRecords")]
    public class PaymentRecord
    {
        [Key]
        public int PaymentRecordId { get; set; }

        public int UserId { get; set; }
        public int IpoId { get; set; }
        public int AppliedLots { get; set; }
        public int AppliedShares { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal BidPrice { get; set; }

        [StringLength(40)]
        public string ApplicationNumber { get; set; } = string.Empty;

        [StringLength(40)]
        public string RazorpayOrderId { get; set; } = string.Empty;

        [StringLength(40)]
        public string? RazorpayPaymentId { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [StringLength(20)]
        public string Status { get; set; } = "Created";
        [StringLength(20)]
        public string RefundStatus { get; set; } = "Not Applicable";
        [Column(TypeName = "decimal(18,2)")]
        public decimal UnblockedAmount { get; set; }
        public DateTime? RefundProcessedAt { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
