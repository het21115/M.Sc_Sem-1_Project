using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOApplications")]
    public class IPOApplication
    {
        [Key]
        public int Application_id { get; set; }

        public int User_id { get; set; }

        public int IPO_id { get; set; }

        public int Applied_lots { get; set; }

        public int Applied_shares { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Application_amount { get; set; }

        public DateTime Application_date { get; set; }

        [StringLength(30)]
        public string? Status { get; set; }
    }
}