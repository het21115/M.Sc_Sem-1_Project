using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOGMPHistory")]
    public class IPOGMPHistory
    {
        [Key]
        public int Gmp_id { get; set; }

        public int IPO_id { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal GMPPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? EstimatedListingPrice { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? ExpectedListingGainPercentage { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? KostakRate { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? SubjectToSauda { get; set; }

        [StringLength(50)]
        public string? FireRating { get; set; }

        [StringLength(200)]
        public string? Source { get; set; }

        public DateTime RecordedDate { get; set; }

        public DateTime CreatedAt { get; set; }

        // Navigation property
        [ForeignKey("IPO_id")]
        public virtual IPO? IPO { get; set; }
    }
}
