using System.ComponentModel.DataAnnotations;

namespace IPOInvestmentManagement.Models
{
    public class AdminProfileViewModel
    {
        public int UserId { get; set; }

        [Required, StringLength(100)]
        public string FirstName { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string LastName { get; set; } = string.Empty;

        [Required, EmailAddress, StringLength(150)]
        public string Email { get; set; } = string.Empty;

        [Required, RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Please enter a valid 10-digit mobile number.")]
        public string Phone { get; set; } = string.Empty;

        public string Role { get; set; } = "Admin";
    }
}
