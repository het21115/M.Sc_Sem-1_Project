using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("StockMarketData")]
    public class StockMarketData
    {
        [Key]
        public int MarketDataId { get; set; }

        public int IPOId { get; set; }
        public int CompanyId { get; set; }

        [Required]
        [StringLength(30)]
        public string StockSymbol { get; set; } = string.Empty;

        [Required]
        [StringLength(10)]
        public string Exchange { get; set; } = "NSE";

        [Column(TypeName = "decimal(18,2)")]
        public decimal? IssuePrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? ListingPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? CurrentPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? PreviousClose { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? DayHigh { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? DayLow { get; set; }

        public long? Volume { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? PriceChange { get; set; }

        [Column(TypeName = "decimal(10,4)")]
        public decimal? PriceChangePercentage { get; set; }

        public DateTime LastUpdated { get; set; }
        public bool IsAvailable { get; set; }
    }
}
