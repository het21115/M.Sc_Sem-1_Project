using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOAllotmentHistories")]
    public class IPOAllotmentHistory
    {
        [Key]
        public int AllotmentHistoryId { get; set; }

        public int IPOId { get; set; }

        public int FinalizedByUserId { get; set; }

        public DateTime FinalizedAt { get; set; }

        public int TotalApplications { get; set; }

        public int AllottedApplications { get; set; }

        public int PartialAllotmentCount { get; set; }

        public int UnallottedCount { get; set; }

        public long TotalAppliedShares { get; set; }

        public long TotalAllottedShares { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalDebitedAmount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalUnblockedAmount { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal OversubscriptionMultiple { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal CutOffPrice { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }
    }
}
