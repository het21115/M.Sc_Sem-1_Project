using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("Investments")]
    public class Investment
    {
        [Key]
        public int Investment_id { get; set; }

        public int User_id { get; set; }

        public int IPO_id { get; set; }

        public int Quantity { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Buy_price { get; set; }

        [Column(TypeName = "decimal(29,2)")]
        public decimal? Investment_amount { get; set; }

        public DateTime? Buy_date { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Current_price { get; set; }

        [StringLength(30)]
        public string? Status { get; set; }
    }
}