using System.ComponentModel.DataAnnotations;

namespace IPOInvestmentManagement.Models
{
    public class IpoSubmissionViewModel
    {
        [Required]
        [StringLength(200)]
        [Display(Name = "IPO Name")]
        public string IpoName { get; set; } = string.Empty;

        [Required]
        [Range(1, long.MaxValue)]
        [Display(Name = "Total Shares")]
        public long? TotalShares { get; set; }

        [Required]
        [DataType(DataType.Date)]
        [Display(Name = "Opening Date")]
        public DateTime? OpeningDate { get; set; }

        [Required]
        [DataType(DataType.Date)]
        [Display(Name = "Closing Date")]
        public DateTime? ClosingDate { get; set; }

        [Required]
        [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        [Display(Name = "Minimum Price")]
        public decimal? PriceMin { get; set; }

        [Required]
        [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        [Display(Name = "Maximum Price")]
        public decimal? PriceMax { get; set; }

        [Required]
        [Range(1, int.MaxValue)]
        [Display(Name = "Lot Size")]
        public int? LotSize { get; set; }

        [Required]
        [Range(typeof(decimal), "0.01", "79228162514264337593543950335")]
        [Display(Name = "Issue Size")]
        public decimal? IssueSize { get; set; }

        public string IssueSizeUnit { get; set; } = "Crores";

        [Required]
        [DataType(DataType.Date)]
        [Display(Name = "Allotment Date")]
        public DateTime? AllotmentDate { get; set; }

        [Required]
        [DataType(DataType.Date)]
        [Display(Name = "Funds Unblock / Debit Date")]
        public DateTime? FundsUnblockDate { get; set; }

        [DataType(DataType.Date)]
        [Display(Name = "Tentative Listing Date")]
        public DateTime? ListingDate { get; set; }

        [StringLength(2000)]
        public string? Description { get; set; }

        [Required]
        [Range(2000, 2100)]
        [Display(Name = "Financial Year")]
        public int? FinancialYear { get; set; }

        [Required]
        [Range(typeof(decimal), "0", "79228162514264337593543950335")]
        public decimal? Revenue { get; set; }

        [Required]
        [Range(typeof(decimal), "0", "79228162514264337593543950335")]
        public decimal? Profit { get; set; }

        [Required]
        [Range(typeof(decimal), "0", "79228162514264337593543950335")]
        [Display(Name = "Net Worth")]
        public decimal? NetWorth { get; set; }

        public string RevenueUnit { get; set; } = "Crores";
        public string ProfitUnit { get; set; } = "Crores";
        public string NetWorthUnit { get; set; } = "Crores";
    }
}
