using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("InvestmentTransactions")]
    public class InvestmentTransaction
    {
        [Key]
        public int Transaction_id { get; set; }

        public int Investment_id { get; set; }

        [StringLength(10)]
        public string? Transaction_type { get; set; }

        public int Quantity { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Price { get; set; }

        [Column(TypeName = "decimal(29,2)")]
        public decimal? Total_amount { get; set; }

        public DateTime Transaction_date { get; set; }
    }
}