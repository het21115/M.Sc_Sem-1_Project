using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOAllotments")]
    public class IPOAllotment
    {
        [Key]
        public int Allotment_id { get; set; }

        public int Application_id { get; set; }
        public int User_id { get; set; }
        public int IPO_id { get; set; }
        public int Allotted_lots { get; set; }
        public int Allotted_shares { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Allotment_price { get; set; }

        [Required]
        [StringLength(30)]
        public string Status { get; set; } = "Not Allotted";

        public DateTime Allotment_date { get; set; }
    }
}
