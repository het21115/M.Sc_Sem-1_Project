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

        public List<IPOGMPHistory> GmpHistory { get; set; } = new();
        public IPOGMPHistory? CurrentGmp => GmpHistory.OrderByDescending(g => g.RecordedDate).ThenByDescending(g => g.Gmp_id).FirstOrDefault();
        public IPOGMPHistory? PreviousGmp => GmpHistory.OrderByDescending(g => g.RecordedDate).ThenByDescending(g => g.Gmp_id).Skip(1).FirstOrDefault();
        public decimal EstimatedListingPrice => CurrentGmp?.EstimatedListingPrice ?? ((Ipo.Price_max ?? Ipo.Price_min ?? 0m) + (CurrentGmp?.GMPPrice ?? 0m));
        public decimal EstimatedGainPercentage
        {
            get
            {
                if (CurrentGmp?.ExpectedListingGainPercentage.HasValue == true)
                {
                    return CurrentGmp.ExpectedListingGainPercentage.Value;
                }
                decimal basePrice = Ipo.Price_max ?? Ipo.Price_min ?? 0m;
                return basePrice > 0m && CurrentGmp != null ? Math.Round((CurrentGmp.GMPPrice / basePrice) * 100m, 2) : 0m;
            }
        }
        public decimal GmpChange => (CurrentGmp?.GMPPrice ?? 0m) - (PreviousGmp?.GMPPrice ?? CurrentGmp?.GMPPrice ?? 0m);
        public decimal GmpChangePercentage => PreviousGmp != null && PreviousGmp.GMPPrice > 0 
            ? Math.Round(((CurrentGmp?.GMPPrice ?? 0m) - PreviousGmp.GMPPrice) / PreviousGmp.GMPPrice * 100m, 2) 
            : 0m;
    }
}
