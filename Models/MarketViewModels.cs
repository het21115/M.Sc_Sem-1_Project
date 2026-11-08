namespace IPOInvestmentManagement.Models
{
    public sealed class MarketDataViewModel
    {
        public int IpoId { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string IpoName { get; set; } = string.Empty;
        public string StockSymbol { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public decimal? IssuePrice { get; set; }
        public decimal? ListingPrice { get; set; }
        public decimal? CurrentPrice { get; set; }
        public decimal? PreviousClose { get; set; }
        public decimal? PriceChange { get; set; }
        public decimal? PriceChangePercentage { get; set; }
        public decimal? DayHigh { get; set; }
        public decimal? DayLow { get; set; }
        public long? Volume { get; set; }
        public DateTime? LastUpdated { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsMarketOpen { get; set; }
        public bool CanTrade { get; set; }
        public bool IsListingToday { get; set; }
    }

    public sealed class MarketDashboardViewModel
    {
        public bool IsMarketOpen { get; set; }
        public string MarketStatus { get; set; } = "Closed";
        public DateTime? LastUpdated { get; set; }
        public List<MarketDataViewModel> Listings { get; set; } = new();
        public List<MarketDataViewModel> TopGainers { get; set; } = new();
        public List<MarketDataViewModel> TopLosers { get; set; } = new();
    }

    public sealed class MarketTradeViewModel
    {
        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string StockSymbol { get; set; } = string.Empty;
        public decimal CurrentPrice { get; set; }
        public int Quantity { get; set; }
        public decimal EstimatedTotal { get; set; }
        public int AvailableQuantity { get; set; }
        public decimal CashBalance { get; set; }
        public bool IsSell { get; set; }
    }
}
