namespace IPOInvestmentManagement.Models
{
    public class AdminApplicationDetailsViewModel
    {
        public IPOApplication Application { get; set; } = new();
        public User? Investor { get; set; }
        public IPO? Ipo { get; set; }
    }
}
