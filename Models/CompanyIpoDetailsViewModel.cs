namespace IPOInvestmentManagement.Models
{
    public class CompanyIpoDetailsViewModel
    {
        public IPO Ipo { get; set; } = new();
        public Company? Company { get; set; }
        public int ApplicationCount { get; set; }
        public IPOFinancial? Financial { get; set; }
        public IPOAnalysis? Analysis { get; set; }
        public IPOListing? Listing { get; set; }
        public decimal? ComparisonRoe { get; set; }
        public decimal? ComparisonRevenueGrowth { get; set; }
        public decimal? ComparisonProfitGrowth { get; set; }
        public decimal? ComparisonListingPerformance { get; set; }
    }
}
