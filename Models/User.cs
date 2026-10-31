using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("Users")]
    public class User
    {
        [Key]
        public int User_id { get; set; }

        [Required]
        [StringLength(100)]
        public string? First_name { get; set; }

        [Required]
        [StringLength(100)]
        public string? Last_name { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(150)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        public string Password_hash { get; set; } = string.Empty;

        [NotMapped]
        [Required]
        [DataType(DataType.Password)]
        [Compare(nameof(Password_hash), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Required]
        [RegularExpression(@"^[6-9]\d{9}$",
        ErrorMessage = "Please enter a valid 10-digit mobile number.")]
        public string Phone { get; set; } = string.Empty;

        [Required]
        [StringLength(30)]
        public string? DematAccountNumber { get; set; }

        [Required]
        [StringLength(100)]
        public string? UpiId { get; set; }

        [StringLength(100)]
        public string? Role { get; set; }

        public DateTime Created_at { get; set; }

        public bool Is_active { get; set; } = true;
    }
}