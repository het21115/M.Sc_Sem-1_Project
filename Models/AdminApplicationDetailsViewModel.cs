namespace IPOInvestmentManagement.Models
{
    public class AdminApplicationDetailsViewModel
    {
        public IPOApplication Application { get; set; } = new();
        public User? Investor { get; set; }
        public IPO Ipo { get; set; } = new();
        public IPOAllotment? Allotment { get; set; }
        public PaymentRecord? Payment { get; set; }
    }
}
