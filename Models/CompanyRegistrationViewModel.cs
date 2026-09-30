using System.ComponentModel.DataAnnotations;

namespace IPOInvestmentManagement.Models
{
    public class CompanyRegistrationViewModel
    {
        [Required]
        [StringLength(200)]
        [Display(Name = "Company Name")]
        public string CompanyName { get; set; } = string.Empty;

        [StringLength(50)]
        public string? CIN { get; set; }

        [Required]
        [StringLength(100)]
        public string Industry { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Url]
        [StringLength(255)]
        public string? Website { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(150)]
        [Display(Name = "Company Email")]
        public string CompanyEmail { get; set; } = string.Empty;

        [Required]
        [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Please enter a valid 10-digit mobile number.")]
        [Display(Name = "Company Phone")]
        public string CompanyPhone { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Address { get; set; }

        [Required]
        [StringLength(255)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
