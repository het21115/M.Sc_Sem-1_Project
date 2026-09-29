using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("Companies")]
    public class Company
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Company_id { get; set; }

        public int User_id { get; set; }

        [Required]
        [StringLength(200)]
        public string Company_name { get; set; } = string.Empty;

        [StringLength(50)]
        public string? CIN { get; set; }

        [StringLength(100)]
        public string? Industry { get; set; }

        public string? Description { get; set; }

        [StringLength(255)]
        public string? Website { get; set; }

        [StringLength(150)]
        public string? Email { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; }

        [StringLength(500)]
        public string? Address { get; set; }

        [StringLength(30)]
        public string? Approval_status { get; set; } = "Pending";

        public DateTime Created_at { get; set; }
    }
}