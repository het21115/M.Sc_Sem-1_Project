namespace IPOInvestmentManagement.Models
{
    public class IpoPaymentViewModel
    {
        public string ApplicationNumber { get; set; } = string.Empty;
        public string InvestorName { get; set; } = string.Empty;
        public string IpoName { get; set; } = string.Empty;
        public decimal BidPrice { get; set; }
        public int AppliedLots { get; set; }
        public int AppliedShares { get; set; }
        public decimal ApplicationAmount { get; set; }
        public string DematAccountNumber { get; set; } = string.Empty;
        public string UpiId { get; set; } = string.Empty;
    }
}
