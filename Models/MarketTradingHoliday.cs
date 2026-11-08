using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("MarketTradingHolidays")]
    public class MarketTradingHoliday
    {
        [Key]
        public int HolidayId { get; set; }

        public DateTime HolidayDate { get; set; }

        [Required]
        [StringLength(150)]
        public string Name { get; set; } = string.Empty;
    }
}
