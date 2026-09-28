using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOCategories")]
    public class IPOCategory
    {
        [Key]
        public int Category_id { get; set; }

        [Required]
        [StringLength(100)]
        public string Category_name { get; set; } = string.Empty;

        [StringLength(255)]
        public string? Description { get; set; }
    }
}