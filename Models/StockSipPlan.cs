using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("StockSipPlans")]
    public class StockSipPlan
    {
        [Key]
        public int SipId { get; set; }

        public int UserId { get; set; }
        public User? User { get; set; }

        public int IPOId { get; set; }
        public IPO? IPO { get; set; }

        [Required]
        [StringLength(30)]
        public string StockSymbol { get; set; } = string.Empty;

        [Required]
        [StringLength(10)]
        public string Exchange { get; set; } = "NSE"; // NSE or BSE

        [Required]
        [StringLength(20)]
        public string Frequency { get; set; } = "Monthly"; // Daily, Weekly, Monthly

        [Column(TypeName = "decimal(18,2)")]
        public decimal InstallmentAmount { get; set; } // Fixed rupee budget per installment

        public int InstallmentDay { get; set; } = 5; // Day of month (1-28) or Day of week (1-5)

        public DateTime StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        public DateTime NextExecutionDate { get; set; }

        public DateTime? LastExecutedDate { get; set; }

        public int InstallmentsCompleted { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalInvested { get; set; } = 0m;

        public int TotalSharesAccumulated { get; set; } = 0;

        [Required]
        [StringLength(20)]
        public string Status { get; set; } = "Active"; // Active, Paused, Cancelled, Completed

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }

        [NotMapped]
        public string? CompanyName { get; set; }

        [NotMapped]
        public decimal? CurrentMarketPrice { get; set; }
    }
}
