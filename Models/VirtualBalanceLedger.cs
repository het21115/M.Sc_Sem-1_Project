using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("VirtualBalanceLedgers")]
    public class VirtualBalanceLedger
    {
        [Key]
        public int LedgerId { get; set; }

        public int UserId { get; set; }

        public int? AdminUserId { get; set; }

        [Required]
        [StringLength(50)]
        public string TransactionType { get; set; } = "ADMIN_ADJUSTMENT"; // ADMIN_CREDIT, ADMIN_DEBIT, ADMIN_SET, TRADE_BUY, TRADE_SELL

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal BalanceBefore { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal BalanceAfter { get; set; }

        [Required]
        [StringLength(500)]
        public string Reason { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }
}
