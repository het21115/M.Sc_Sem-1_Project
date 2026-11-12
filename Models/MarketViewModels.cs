namespace IPOInvestmentManagement.Models
{
    public sealed class MarketDataViewModel
    {
        public int IpoId { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string IpoName { get; set; } = string.Empty;
        public string StockSymbol { get; set; } = string.Empty;
        public string Exchange { get; set; } = "NSE";
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

        // Dedicated NSE Fields
        public decimal? NsePrice { get; set; }
        public decimal? NseListingPrice { get; set; }
        public decimal? NsePreviousClose { get; set; }
        public decimal? NsePriceChange { get; set; }
        public decimal? NsePriceChangePercentage { get; set; }
        public decimal? NseDayHigh { get; set; }
        public decimal? NseDayLow { get; set; }
        public long? NseVolume { get; set; }

        // Dedicated BSE Fields
        public decimal? BsePrice { get; set; }
        public decimal? BseListingPrice { get; set; }
        public decimal? BsePreviousClose { get; set; }
        public decimal? BsePriceChange { get; set; }
        public decimal? BsePriceChangePercentage { get; set; }
        public decimal? BseDayHigh { get; set; }
        public decimal? BseDayLow { get; set; }
        public long? BseVolume { get; set; }

        // Bid / Ask Simulation
        public decimal? BestBidPrice { get; set; }
        public decimal? BestAskPrice { get; set; }
    }

    public sealed class MarketDashboardViewModel
    {
        public bool IsMarketOpen { get; set; }
        public string MarketStatus { get; set; } = "Closed";
        public DateTime? LastUpdated { get; set; }
        public List<MarketDataViewModel> Listings { get; set; } = new();
        public List<MarketDataViewModel> TopGainers { get; set; } = new();
        public List<MarketDataViewModel> TopLosers { get; set; } = new();
        public bool IsSimulationActive { get; set; } = true;
    }

    public sealed class MarketTradeViewModel
    {
        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string StockSymbol { get; set; } = string.Empty;
        public string Exchange { get; set; } = "NSE";
        public decimal CurrentPrice { get; set; }
        public decimal? NsePrice { get; set; }
        public decimal? BsePrice { get; set; }
        public int Quantity { get; set; }
        public decimal EstimatedTotal { get; set; }
        public int AvailableQuantity { get; set; }
        public decimal CashBalance { get; set; }
        public bool IsSell { get; set; }
        public int? InvestmentId { get; set; }
    }

    public sealed class LiveMarketBroadcastDto
    {
        public string Type { get; set; } = "PRICE_UPDATE";
        public string MarketStatus { get; set; } = "Open";
        public bool IsMarketOpen { get; set; } = true;
        public string ServerTime { get; set; } = string.Empty;
        public List<MarketStockTickDto> Stocks { get; set; } = new();
    }

    public sealed class MarketStockTickDto
    {
        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string StockSymbol { get; set; } = string.Empty;

        // NSE
        public decimal NsePrice { get; set; }
        public decimal NseChange { get; set; }
        public decimal NseChangePercent { get; set; }
        public decimal NseHigh { get; set; }
        public decimal NseLow { get; set; }
        public long NseVolume { get; set; }

        // BSE
        public decimal BsePrice { get; set; }
        public decimal BseChange { get; set; }
        public decimal BseChangePercent { get; set; }
        public decimal BseHigh { get; set; }
        public decimal BseLow { get; set; }
        public long BseVolume { get; set; }

        public string LastUpdated { get; set; } = string.Empty;
    }

    public sealed class TradeExecutionResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public string StockName { get; set; } = string.Empty;
        public string Exchange { get; set; } = "NSE";
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal NewCashBalance { get; set; }
        public int RemainingHoldingQuantity { get; set; }
    }

    public sealed class AdminBroadcastRequestDto
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Category { get; set; } = "Announcement"; // Announcement, Alert, MarketUpdate
    }
}
