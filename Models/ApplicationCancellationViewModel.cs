using System.ComponentModel.DataAnnotations;

namespace IPOInvestmentManagement.Models
{
    public class ApplicationCancellationViewModel
    {
        public int ApplicationId { get; set; }

        [Required, StringLength(500, MinimumLength = 5)]
        public string Reason { get; set; } = string.Empty;
    }
}
