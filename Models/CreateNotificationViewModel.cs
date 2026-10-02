using System.ComponentModel.DataAnnotations;

namespace IPOInvestmentManagement.Models
{
    public class CreateNotificationViewModel
    {
        [Required, StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Message { get; set; } = string.Empty;

        [Required]
        public string RecipientType { get; set; } = "Investors";

        public int? UserId { get; set; }
        public int? CompanyId { get; set; }
        public List<User> Investors { get; set; } = new();
        public List<User> Companies { get; set; } = new();
        public List<Company> CompanyProfiles { get; set; } = new();
    }
}
