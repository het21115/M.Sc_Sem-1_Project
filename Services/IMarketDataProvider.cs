using IPOInvestmentManagement.Models;

namespace IPOInvestmentManagement.Services
{
    public sealed record MarketQuote(
        string Symbol,
        string Exchange,
        decimal CurrentPrice,
        decimal PreviousClose,
        decimal DayHigh,
        decimal DayLow,
        long Volume,
        DateTime LastUpdated,
        decimal PriceChange = 0m,
        decimal PriceChangePercentage = 0m,
        decimal BidPrice = 0m,
        decimal AskPrice = 0m,
        bool IsSimulated = false,
        string DataSource = "Live Exchange",
        bool IsStale = false,
        string? StatusMessage = null);

    public interface IMarketDataProvider
    {
        Task<MarketQuote?> GetQuoteAsync(string symbol, string exchange, CancellationToken cancellationToken = default);
        Task<MarketQuote?> GetIpoQuoteAsync(int ipoId, string exchange, decimal defaultPrice, string symbol = "", CancellationToken cancellationToken = default);
        Task<IReadOnlyDictionary<string, MarketQuote>> GetAllQuotesAsync(CancellationToken cancellationToken = default);
        void UpdateTickPrices(IEnumerable<(int IpoId, string Symbol, decimal BasePrice)> ipos);
        bool IsSimulationMode { get; set; }
        string ProviderName { get; }
        bool IsConfigured { get; }
        string? StatusMessage { get; }
        Task<IEnumerable<StockPriceHistory>> GetHistoricalPricesAsync(int ipoId, string symbol, string exchange, string timeframe, CancellationToken cancellationToken = default);
    }
}
