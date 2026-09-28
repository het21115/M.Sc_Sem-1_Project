using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOAnalysis")]
    public class IPOAnalysis
    {
        [Key]
        public int Analysis_id { get; set; }

        public int IPO_id { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Revenue { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Profit { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? ROE { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? Revenue_growth { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal? Profit_growth { get; set; }

        public string? Analysis_summary { get; set; }

        public DateTime Updated_at { get; set; }
    }
}