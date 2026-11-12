using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public sealed class UnavailableMarketDataProvider(ILogger<UnavailableMarketDataProvider> logger) : IMarketDataProvider
    {
        public bool IsSimulationMode { get; set; } = false;

        public Task<MarketQuote?> GetQuoteAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
        {
            logger.LogWarning("No market-data provider is configured for {Exchange}:{Symbol}.", exchange, symbol);
            return Task.FromResult<MarketQuote?>(null);
        }

        public Task<MarketQuote?> GetIpoQuoteAsync(int ipoId, string exchange, decimal defaultPrice, string symbol = "", CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MarketQuote?>(null);
        }

        public Task<IReadOnlyDictionary<string, MarketQuote>> GetAllQuotesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, MarketQuote> empty = new Dictionary<string, MarketQuote>();
            return Task.FromResult(empty);
        }

        public void UpdateTickPrices(IEnumerable<(int IpoId, string Symbol, decimal BasePrice)> ipos)
        {
        }
    }
}
