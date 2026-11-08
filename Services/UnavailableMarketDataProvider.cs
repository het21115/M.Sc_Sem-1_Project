using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public sealed class UnavailableMarketDataProvider(ILogger<UnavailableMarketDataProvider> logger) : IMarketDataProvider
    {
        public Task<MarketQuote?> GetQuoteAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
        {
            logger.LogWarning("No market-data provider is configured for {Exchange}:{Symbol}.", exchange, symbol);
            return Task.FromResult<MarketQuote?>(null);
        }
    }
}
