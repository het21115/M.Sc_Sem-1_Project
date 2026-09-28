using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOListing")]
    public class IPOListing
    {
        [Key]
        public int Listing_id { get; set; }

        public int IPO_id { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Issue_price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? NSE_listing_price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? BSE_listing_price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? NSE_closing_price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? BSE_closing_price { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? Listing_gain_percentage { get; set; }

        public DateTime? Listing_date { get; set; }
    }
}