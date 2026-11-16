using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("StockPriceHistory")]
    public sealed class StockPriceHistory
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public long HistoryId { get; set; }

        public int IPOId { get; set; }

        [Required]
        [MaxLength(30)]
        public string StockSymbol { get; set; } = string.Empty;

        [Required]
        [MaxLength(10)]
        public string Exchange { get; set; } = "NSE";

        [Column(TypeName = "decimal(18,2)")]
        public decimal Price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OpenPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal HighPrice { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal LowPrice { get; set; }

        public long Volume { get; set; }

        public DateTime Timestamp { get; set; }

        public bool IsSimulated { get; set; } = true;
    }
}
