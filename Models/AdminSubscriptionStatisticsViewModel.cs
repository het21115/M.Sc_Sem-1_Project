namespace IPOInvestmentManagement.Models
{
    public sealed class AdminSubscriptionStatisticsViewModel
    {
        public IPO Ipo { get; set; } = new();
        public long OfferedShares { get; set; }
        public long AppliedShares { get; set; }
        public int ApplicationCount { get; set; }
        public decimal SubscriptionMultiple { get; set; }
    }
}
