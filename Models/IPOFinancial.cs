using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOFinancials")]
    public class IPOFinancial
    {
        [Key]
        public int Financial_id { get; set; }

        public int IPO_id { get; set; }

        public int Financial_year { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Revenue { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Profit { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Net_worth { get; set; }
    }
}