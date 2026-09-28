using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOs")]
    public class IPO
    {
        [Key]
        public int IPO_id { get; set; }

        public int Company_id { get; set; }

        [Required]
        [StringLength(200)]
        public string IPO_name { get; set; } = string.Empty;

        public long? Total_shares { get; set; }

        [StringLength(50)]
        public string? IPO_type { get; set; }

        [Required]
        public DateTime Open_date { get; set; }

        [Required]
        public DateTime Close_date { get; set; }

        public DateTime? Allotment_date { get; set; }

        public DateTime? Funds_unblock_date { get; set; }

        public DateTime? Listing_date { get; set; }

        public decimal? Price_min { get; set; }

        public decimal? Price_max { get; set; }

        public int? Lot_size { get; set; }

        public decimal? Issue_size { get; set; }

        [StringLength(50)]
        public string? Apply_type { get; set; }

        [StringLength(50)]
        public string? Status { get; set; }

        public string? Description { get; set; }

        public DateTime Created_at { get; set; }
    }
}