using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public sealed class RealTimeStockMarketDataProvider : IMarketDataProvider
    {
        private readonly ILogger<RealTimeStockMarketDataProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, MarketQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, (decimal NseBase, decimal BseBase)> _ipoBasePrices = new();
        private bool _isSimulationMode = true;

        public RealTimeStockMarketDataProvider(
            ILogger<RealTimeStockMarketDataProvider> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("MarketDataClient");
            _httpClient.Timeout = TimeSpan.FromSeconds(3);
        }

        public bool IsSimulationMode
        {
            get => _isSimulationMode;
            set => _isSimulationMode = value;
        }

        public Task<MarketQuote?> GetQuoteAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
        {
            string key = BuildSymbolKey(symbol, exchange);
            if (_quotes.TryGetValue(key, out MarketQuote? quote))
            {
                return Task.FromResult<MarketQuote?>(quote);
            }

            // Return mock quote based on symbol hash if not found
            decimal defaultPrice = Math.Max(50m, (decimal)(Math.Abs(symbol.GetHashCode() % 1500) + 100));
            var newQuote = CreateQuote(symbol, exchange, defaultPrice, defaultPrice);
            _quotes[key] = newQuote;
            return Task.FromResult<MarketQuote?>(newQuote);
        }

        public Task<MarketQuote?> GetIpoQuoteAsync(int ipoId, string exchange, decimal defaultPrice, string symbol = "", CancellationToken cancellationToken = default)
        {
            string key = BuildIpoKey(ipoId, exchange);
            if (_quotes.TryGetValue(key, out MarketQuote? existing))
            {
                return Task.FromResult<MarketQuote?>(existing);
            }

            decimal price = defaultPrice > 0 ? defaultPrice : 100.00m;
            decimal baseNse = price;
            decimal baseBse = Math.Round(price * (1m + ((decimal)(Random.Shared.NextDouble() * 0.002 - 0.001))), 2);
            _ipoBasePrices.TryAdd(ipoId, (baseNse, baseBse));

            decimal quotePrice = string.Equals(exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? baseBse : baseNse;
            var newQuote = CreateQuote(symbol, exchange, quotePrice, quotePrice);
            _quotes[key] = newQuote;
            return Task.FromResult<MarketQuote?>(newQuote);
        }

        public Task<IReadOnlyDictionary<string, MarketQuote>> GetAllQuotesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, MarketQuote> dict = _quotes;
            return Task.FromResult(dict);
        }

        public void UpdateTickPrices(IEnumerable<(int IpoId, string Symbol, decimal BasePrice)> ipos)
        {
            DateTime now = GetIndiaStandardTime();
            foreach (var (ipoId, symbol, basePrice) in ipos)
            {
                decimal validBasePrice = basePrice > 0 ? basePrice : 150.00m;
                var basePrices = _ipoBasePrices.GetOrAdd(ipoId, _ =>
                {
                    decimal nse = validBasePrice;
                    decimal bse = Math.Round(validBasePrice * (1m + ((decimal)(Random.Shared.NextDouble() * 0.002 - 0.001))), 2);
                    return (nse, bse);
                });

                // Update NSE
                UpdateExchangeQuote(ipoId, symbol, "NSE", basePrices.NseBase, now);

                // Update BSE
                UpdateExchangeQuote(ipoId, symbol, "BSE", basePrices.BseBase, now);
            }
        }

        private void UpdateExchangeQuote(int ipoId, string symbol, string exchange, decimal previousClose, DateTime now)
        {
            string key = BuildIpoKey(ipoId, exchange);
            if (!_quotes.TryGetValue(key, out MarketQuote? existing))
            {
                existing = CreateQuote(symbol, exchange, previousClose, previousClose);
            }

            // Realistic Indian Market Tick:
            // Minimum tick size is 0.05 INR
            // Random walk with mean-reversion pull towards previous close
            double drift = (double)(previousClose - existing.CurrentPrice) * 0.02;
            double volatility = (Random.Shared.NextDouble() - 0.495) * 0.006; // +/- 0.3% per tick
            decimal percentageDelta = (decimal)(drift + volatility);
            decimal deltaRupees = Math.Round(existing.CurrentPrice * percentageDelta, 2);

            if (deltaRupees == 0m)
            {
                deltaRupees = (Random.Shared.Next(0, 2) == 0 ? 0.05m : -0.05m);
            }

            // Snap to 5 paise tick size
            deltaRupees = Math.Round(deltaRupees * 20m) / 20m;
            decimal newPrice = Math.Max(1.00m, existing.CurrentPrice + deltaRupees);

            // Circuit filter check: maximum 20% limit from previous close
            decimal lowerCircuit = Math.Round(previousClose * 0.80m, 2);
            decimal upperCircuit = Math.Round(previousClose * 1.20m, 2);
            newPrice = Math.Clamp(newPrice, lowerCircuit, upperCircuit);

            decimal newHigh = Math.Max(existing.DayHigh, newPrice);
            decimal newLow = Math.Min(existing.DayLow, newPrice);
            long addedVolume = (long)Random.Shared.Next(10, 350);
            long newVolume = existing.Volume + addedVolume;
            decimal priceChange = newPrice - previousClose;
            decimal changePercent = previousClose > 0 ? Math.Round(priceChange / previousClose * 100m, 2) : 0m;
            decimal bid = Math.Max(0.05m, newPrice - 0.05m);
            decimal ask = newPrice + 0.05m;

            var updated = new MarketQuote(
                Symbol: string.IsNullOrWhiteSpace(symbol) ? existing.Symbol : symbol,
                Exchange: exchange,
                CurrentPrice: newPrice,
                PreviousClose: previousClose,
                DayHigh: newHigh,
                DayLow: newLow,
                Volume: newVolume,
                LastUpdated: now,
                PriceChange: priceChange,
                PriceChangePercentage: changePercent,
                BidPrice: bid,
                AskPrice: ask
            );

            _quotes[key] = updated;
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                _quotes[BuildSymbolKey(symbol, exchange)] = updated;
            }
        }

        private static MarketQuote CreateQuote(string symbol, string exchange, decimal currentPrice, decimal previousClose)
        {
            DateTime now = GetIndiaStandardTime();
            decimal high = Math.Max(currentPrice, previousClose);
            decimal low = Math.Min(currentPrice, previousClose);
            long volume = Random.Shared.Next(25_000, 250_000);
            decimal change = currentPrice - previousClose;
            decimal changePercent = previousClose > 0 ? Math.Round(change / previousClose * 100m, 2) : 0m;

            return new MarketQuote(
                Symbol: symbol,
                Exchange: exchange,
                CurrentPrice: currentPrice,
                PreviousClose: previousClose,
                DayHigh: high,
                DayLow: low,
                Volume: volume,
                LastUpdated: now,
                PriceChange: change,
                PriceChangePercentage: changePercent,
                BidPrice: Math.Max(0.05m, currentPrice - 0.05m),
                AskPrice: currentPrice + 0.05m
            );
        }

        private static string BuildIpoKey(int ipoId, string exchange) => $"IPO_{ipoId}_{exchange.ToUpperInvariant()}";
        private static string BuildSymbolKey(string symbol, string exchange) => $"SYM_{symbol.ToUpperInvariant()}_{exchange.ToUpperInvariant()}";

        private static DateTime GetIndiaStandardTime()
        {
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
            }
            catch
            {
                try
                {
                    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
                }
                catch
                {
                    return DateTime.UtcNow.AddHours(5).AddMinutes(30);
                }
            }
        }
    }
}
