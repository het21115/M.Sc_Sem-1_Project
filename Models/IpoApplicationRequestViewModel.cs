using System.ComponentModel.DataAnnotations;

namespace IPOInvestmentManagement.Models
{
    public class IpoApplicationRequestViewModel
    {
        public int IpoId { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Number of lots must be at least one.")]
        public int AppliedLots { get; set; }

        [Range(typeof(decimal), "0.01", "999999999999", ErrorMessage = "Enter a valid bid price.")]
        public decimal BidPrice { get; set; }

        [Required]
        public bool AcceptTerms { get; set; }

        public string InvestorName { get; set; } = string.Empty;
        public string IpoName { get; set; } = string.Empty;
        public int LotSize { get; set; }
        public int TotalShares { get; set; }
        public decimal TotalAmount { get; set; }
        public string DematAccountNumber { get; set; } = string.Empty;
        public string UpiId { get; set; } = string.Empty;
    }
}
