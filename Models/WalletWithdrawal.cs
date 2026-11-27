using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("WalletWithdrawals")]
    public class WalletWithdrawal
    {
        [Key]
        public int WithdrawalId { get; set; }

        public int UserId { get; set; }

        [Required]
        [StringLength(40)]
        public string ReferenceNumber { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal BalanceBefore { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal BalanceAfter { get; set; }

        [Required]
        [StringLength(30)]
        public string PayoutMethod { get; set; } = "UPI";

        [Required]
        [StringLength(120)]
        public string DestinationAccount { get; set; } = string.Empty;

        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Completed";

        [StringLength(300)]
        public string? Remarks { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public sealed class WalletWithdrawalRequest
    {
        public decimal Amount { get; set; }
        public string PayoutMethod { get; set; } = "UPI";
        public string? UpiId { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? IfscCode { get; set; }
        public string? Remarks { get; set; }
        public string? ReturnTo { get; set; }
    }

    public sealed class WalletWithdrawModalViewModel
    {
        public decimal SimulatedCashBalance { get; set; }
        public string InvestorUpiId { get; set; } = string.Empty;
        public string ReturnTo { get; set; } = "Portfolio";
    }

    public sealed class WalletWithdrawalResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public decimal NewCashBalance { get; set; }
        public WalletWithdrawal? Withdrawal { get; set; }
    }
}
