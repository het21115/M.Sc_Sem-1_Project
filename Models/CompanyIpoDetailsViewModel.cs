namespace IPOInvestmentManagement.Models
{
    public class CompanyIpoDetailsViewModel
    {
        public IPO Ipo { get; set; } = new();
        public int ApplicationCount { get; set; }
        public IPOFinancial? Financial { get; set; }
        public IPOAnalysis? Analysis { get; set; }
        public IPOListing? Listing { get; set; }
    }
}
