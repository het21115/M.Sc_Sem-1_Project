using System.Collections.Concurrent;
using System.Text.Json;
using IPOInvestmentManagement.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public sealed class RealTimeStockMarketDataProvider : IMarketDataProvider
    {
        private readonly ILogger<RealTimeStockMarketDataProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ConcurrentDictionary<string, CachedQuoteEntry> _quoteCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, (decimal NseBase, decimal BseBase)> _ipoBasePrices = new();
        private bool _isSimulationMode;
        private readonly string _configuredProvider;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly TimeSpan _cacheDuration;

        private sealed record CachedQuoteEntry(MarketQuote Quote, DateTime CachedAt);

        public RealTimeStockMarketDataProvider(
            ILogger<RealTimeStockMarketDataProvider> logger,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClient = httpClientFactory.CreateClient("MarketDataClient");
            _httpClient.Timeout = TimeSpan.FromSeconds(5);

            // 1. Resolve Provider and credentials securely from Env vars, User Secrets, or Configuration
            _configuredProvider = _configuration["MarketData:Provider"] ?? "TwelveData";

            string? envKey = Environment.GetEnvironmentVariable("MARKET_DATA_API_KEY") 
                          ?? Environment.GetEnvironmentVariable("MarketData__ApiKey");
            _apiKey = envKey ?? _configuration["MarketData:ApiKey"] ?? _configuration["MarketTrading:ApiKey"] ?? string.Empty;

            _baseUrl = _configuration["MarketData:BaseUrl"] ?? "https://api.twelvedata.com";
            
            int cacheSec = int.TryParse(_configuration["MarketData:CacheDurationSeconds"], out int cs) ? cs : 15;
            _cacheDuration = TimeSpan.FromSeconds(Math.Max(5, cacheSec));

            // 2. Resolve simulation mode
            string tradingProvider = _configuration["MarketTrading:Provider"] ?? "Live";
            bool forceSimulation = string.Equals(tradingProvider, "Simulation", StringComparison.OrdinalIgnoreCase);
            bool enableSimulationFallback = _configuration.GetValue<bool>("MarketData:EnableSimulationFallback", false);

            if (forceSimulation)
            {
                _isSimulationMode = true;
            }
            else if (string.IsNullOrWhiteSpace(_apiKey))
            {
                // In Live mode, if credentials are missing, do not fake live data!
                // We keep Live mode (with unconfigured warning) unless fallback is explicitly requested.
                _isSimulationMode = enableSimulationFallback;
            }
            else
            {
                _isSimulationMode = false;
            }

            _logger.LogInformation(
                "Market Data Provider initialized: Provider={Provider}, Configured={IsConfigured}, SimulationMode={IsSimulationMode}",
                _configuredProvider, IsConfigured, _isSimulationMode);
        }

        public bool IsSimulationMode
        {
            get => _isSimulationMode;
            set => _isSimulationMode = value;
        }

        public string ProviderName => _isSimulationMode 
            ? "Simulation Engine (Development Mode)" 
            : (IsConfigured ? $"{_configuredProvider} Live Exchange" : "Exchange Feed (Unconfigured)");

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string? StatusMessage
        {
            get
            {
                if (_isSimulationMode)
                {
                    return "Simulation Mode Active: Realistic continuous tick generation within Indian circuit limits (±20%). For live exchange feeds, configure provider API credentials.";
                }
                if (!IsConfigured)
                {
                    return "Market Data Provider API key is not configured. Real-time exchange feed is offline. Set 'MarketData:ApiKey' in appsettings.json, User Secrets, or 'MARKET_DATA_API_KEY' environment variable.";
                }
                return null;
            }
        }

        public async Task<MarketQuote?> GetQuoteAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
        {
            return await GetIpoQuoteAsync(0, exchange, 100m, symbol, cancellationToken);
        }

        public async Task<MarketQuote?> GetIpoQuoteAsync(
            int ipoId,
            string exchange,
            decimal defaultPrice,
            string symbol = "",
            CancellationToken cancellationToken = default)
        {
            string exch = string.Equals(exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";
            string cleanSym = CleanStockSymbol(symbol, string.Empty);
            string key = BuildKey(ipoId, cleanSym, exch);
            DateTime now = GetIndiaStandardTime();

            // 1. Check in-memory cache
            if (_quoteCache.TryGetValue(key, out CachedQuoteEntry? cached))
            {
                if ((now - cached.CachedAt) < _cacheDuration)
                {
                    return cached.Quote;
                }
            }

            // 2. If simulation mode is explicitly active, run simulated walk
            if (_isSimulationMode)
            {
                var simQuote = GenerateSimulatedTick(ipoId, cleanSym, exch, defaultPrice, cached?.Quote, now);
                _quoteCache[key] = new CachedQuoteEntry(simQuote, now);
                return simQuote;
            }

            // 3. Genuine Real-Time Provider Integration
            if (IsConfigured)
            {
                try
                {
                    var liveQuote = await FetchLiveQuoteFromProviderAsync(cleanSym, exch, defaultPrice, cancellationToken);
                    if (liveQuote != null)
                    {
                        _quoteCache[key] = new CachedQuoteEntry(liveQuote, now);
                        return liveQuote;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch live quote for {Exchange}:{Symbol} from {Provider}.", exch, cleanSym, _configuredProvider);
                }
            }

            // 4. Provider unconfigured or live fetch failed:
            // NEVER output random fake numbers as genuine market data.
            // Return cached or static verified quote marked as STALE with clear notice.
            decimal fallbackPrice = cached?.Quote.CurrentPrice ?? (defaultPrice > 0 ? defaultPrice : 100m);
            decimal fallbackPrev = cached?.Quote.PreviousClose ?? fallbackPrice;
            decimal change = fallbackPrice - fallbackPrev;
            decimal changePct = fallbackPrev > 0 ? Math.Round(change / fallbackPrev * 100m, 2) : 0m;

            var staleQuote = new MarketQuote(
                Symbol: cleanSym,
                Exchange: exch,
                CurrentPrice: fallbackPrice,
                PreviousClose: fallbackPrev,
                DayHigh: Math.Max(fallbackPrice, fallbackPrev),
                DayLow: Math.Min(fallbackPrice, fallbackPrev),
                Volume: cached?.Quote.Volume ?? 0,
                LastUpdated: cached?.Quote.LastUpdated ?? now,
                PriceChange: change,
                PriceChangePercentage: changePct,
                BidPrice: Math.Max(0.05m, fallbackPrice - 0.05m),
                AskPrice: fallbackPrice + 0.05m,
                IsSimulated: false,
                DataSource: IsConfigured ? $"{_configuredProvider} (Delayed/Stale)" : "Exchange Database (Static/Offline)",
                IsStale: true,
                StatusMessage: IsConfigured 
                    ? "Live exchange feed temporarily delayed. Displaying latest recorded quote." 
                    : "Provider API credentials not configured. Displaying recorded base price without simulation."
            );

            _quoteCache[key] = new CachedQuoteEntry(staleQuote, now);
            return staleQuote;
        }

        public Task<IReadOnlyDictionary<string, MarketQuote>> GetAllQuotesAsync(CancellationToken cancellationToken = default)
        {
            var dict = _quoteCache.ToDictionary(k => k.Key, v => v.Value.Quote);
            return Task.FromResult<IReadOnlyDictionary<string, MarketQuote>>(dict);
        }

        public void UpdateTickPrices(IEnumerable<(int IpoId, string Symbol, decimal BasePrice)> ipos)
        {
            DateTime now = GetIndiaStandardTime();
            foreach (var (ipoId, symbol, basePrice) in ipos)
            {
                decimal validBase = basePrice > 0 ? basePrice : 150m;
                var basePrices = _ipoBasePrices.GetOrAdd(ipoId, _ =>
                {
                    decimal nse = validBase;
                    decimal bse = Math.Round(validBase * 0.9995m, 2);
                    return (nse, bse);
                });

                if (_isSimulationMode)
                {
                    string clean = CleanStockSymbol(symbol, string.Empty);
                    string keyNse = BuildKey(ipoId, clean, "NSE");
                    string keyBse = BuildKey(ipoId, clean, "BSE");

                    _quoteCache.TryGetValue(keyNse, out var cachedNse);
                    var tickNse = GenerateSimulatedTick(ipoId, clean, "NSE", basePrices.NseBase, cachedNse?.Quote, now);
                    _quoteCache[keyNse] = new CachedQuoteEntry(tickNse, now);

                    _quoteCache.TryGetValue(keyBse, out var cachedBse);
                    var tickBse = GenerateSimulatedTick(ipoId, clean, "BSE", basePrices.BseBase, cachedBse?.Quote, now);
                    _quoteCache[keyBse] = new CachedQuoteEntry(tickBse, now);
                }
            }
        }

        public async Task<IEnumerable<StockPriceHistory>> GetHistoricalPricesAsync(
            int ipoId,
            string symbol,
            string exchange,
            string timeframe,
            CancellationToken cancellationToken = default)
        {
            if (_isSimulationMode || !IsConfigured)
            {
                return Enumerable.Empty<StockPriceHistory>();
            }

            string cleanSym = CleanStockSymbol(symbol, string.Empty);
            string exch = string.Equals(exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";

            if (string.Equals(_configuredProvider, "TwelveData", StringComparison.OrdinalIgnoreCase))
            {
                return await FetchTwelveDataHistoryAsync(ipoId, cleanSym, exch, timeframe, cancellationToken);
            }

            return Enumerable.Empty<StockPriceHistory>();
        }

        // =========================================================================
        // GENUINE LIVE DATA INTEGRATIONS: TWELVE DATA & ALPHA VANTAGE
        // =========================================================================

        private async Task<MarketQuote?> FetchLiveQuoteFromProviderAsync(
            string symbol,
            string exchange,
            decimal defaultPrice,
            CancellationToken cancellationToken)
        {
            if (string.Equals(_configuredProvider, "AlphaVantage", StringComparison.OrdinalIgnoreCase))
            {
                return await FetchAlphaVantageQuoteAsync(symbol, exchange, defaultPrice, cancellationToken);
            }

            // Default: Twelve Data (Direct Indian NSE/BSE licensed market data)
            return await FetchTwelveDataQuoteAsync(symbol, exchange, defaultPrice, cancellationToken);
        }

        private async Task<MarketQuote?> FetchTwelveDataQuoteAsync(
            string symbol,
            string exchange,
            decimal defaultPrice,
            CancellationToken cancellationToken)
        {
            // Twelve Data endpoint format:
            // https://api.twelvedata.com/quote?symbol=TATAMOTORS&exchange=NSE&apikey=API_KEY
            string url = $"{_baseUrl.TrimEnd('/')}/quote?symbol={Uri.EscapeDataString(symbol)}&exchange={Uri.EscapeDataString(exchange)}&apikey={Uri.EscapeDataString(_apiKey)}";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Twelve Data quote request returned status {StatusCode} for {Exchange}:{Symbol}.", response.StatusCode, exchange, symbol);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusEl) && string.Equals(statusEl.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                string message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "Provider error" : "Error";
                _logger.LogWarning("Twelve Data returned error for {Symbol}:{Exchange}: {Message}", symbol, exchange, message);
                return null;
            }

            decimal price = defaultPrice;
            if (root.TryGetProperty("close", out var closeEl) && decimal.TryParse(closeEl.GetString(), out decimal p)) price = p;
            else if (root.TryGetProperty("price", out var priceEl) && decimal.TryParse(priceEl.GetString(), out decimal pr)) price = pr;

            decimal prevClose = price;
            if (root.TryGetProperty("previous_close", out var prevEl) && decimal.TryParse(prevEl.GetString(), out decimal pc)) prevClose = pc;

            decimal high = price;
            if (root.TryGetProperty("high", out var highEl) && decimal.TryParse(highEl.GetString(), out decimal h)) high = h;

            decimal low = price;
            if (root.TryGetProperty("low", out var lowEl) && decimal.TryParse(lowEl.GetString(), out decimal l)) low = l;

            long volume = 0;
            if (root.TryGetProperty("volume", out var volEl) && long.TryParse(volEl.GetString(), out long v)) volume = v;

            decimal change = price - prevClose;
            if (root.TryGetProperty("change", out var chgEl) && decimal.TryParse(chgEl.GetString(), out decimal c)) change = c;

            decimal changePct = prevClose > 0 ? Math.Round(change / prevClose * 100m, 2) : 0m;
            if (root.TryGetProperty("percent_change", out var pctEl) && decimal.TryParse(pctEl.GetString(), out decimal cp)) changePct = cp;

            DateTime lastUpdated = GetIndiaStandardTime();
            if (root.TryGetProperty("datetime", out var dtEl) && DateTime.TryParse(dtEl.GetString(), out DateTime dt))
            {
                lastUpdated = dt;
            }

            return new MarketQuote(
                Symbol: symbol,
                Exchange: exchange,
                CurrentPrice: price,
                PreviousClose: prevClose,
                DayHigh: Math.Max(high, price),
                DayLow: Math.Min(low, price),
                Volume: volume,
                LastUpdated: lastUpdated,
                PriceChange: change,
                PriceChangePercentage: changePct,
                BidPrice: Math.Max(0.05m, price - 0.05m),
                AskPrice: price + 0.05m,
                IsSimulated: false,
                DataSource: $"Twelve Data ({exchange} Live)",
                IsStale: false,
                StatusMessage: null
            );
        }

        private async Task<MarketQuote?> FetchAlphaVantageQuoteAsync(
            string symbol,
            string exchange,
            decimal defaultPrice,
            CancellationToken cancellationToken)
        {
            // Alpha Vantage format: https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol=RELIANCE.BSE&apikey=KEY
            string avSymbol = $"{symbol}.{exchange}";
            string url = $"https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol={Uri.EscapeDataString(avSymbol)}&apikey={Uri.EscapeDataString(_apiKey)}";

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Global Quote", out var gq) || gq.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            decimal price = defaultPrice;
            if (gq.TryGetProperty("05. price", out var pEl) && decimal.TryParse(pEl.GetString(), out decimal p)) price = p;

            decimal prevClose = price;
            if (gq.TryGetProperty("08. previous close", out var pcEl) && decimal.TryParse(pcEl.GetString(), out decimal pc)) prevClose = pc;

            decimal high = price;
            if (gq.TryGetProperty("03. high", out var hEl) && decimal.TryParse(hEl.GetString(), out decimal h)) high = h;

            decimal low = price;
            if (gq.TryGetProperty("04. low", out var lEl) && decimal.TryParse(lEl.GetString(), out decimal l)) low = l;

            long volume = 0;
            if (gq.TryGetProperty("06. volume", out var vEl) && long.TryParse(vEl.GetString(), out long v)) volume = v;

            decimal change = price - prevClose;
            if (gq.TryGetProperty("09. change", out var cEl) && decimal.TryParse(cEl.GetString(), out decimal c)) change = c;

            decimal changePct = prevClose > 0 ? Math.Round(change / prevClose * 100m, 2) : 0m;

            return new MarketQuote(
                Symbol: symbol,
                Exchange: exchange,
                CurrentPrice: price,
                PreviousClose: prevClose,
                DayHigh: Math.Max(high, price),
                DayLow: Math.Min(low, price),
                Volume: volume,
                LastUpdated: GetIndiaStandardTime(),
                PriceChange: change,
                PriceChangePercentage: changePct,
                BidPrice: Math.Max(0.05m, price - 0.05m),
                AskPrice: price + 0.05m,
                IsSimulated: false,
                DataSource: $"Alpha Vantage ({exchange})",
                IsStale: false,
                StatusMessage: null
            );
        }

        private async Task<List<StockPriceHistory>> FetchTwelveDataHistoryAsync(
            int ipoId,
            string symbol,
            string exchange,
            string timeframe,
            CancellationToken cancellationToken)
        {
            var results = new List<StockPriceHistory>();
            (string interval, int outputsize) = timeframe switch
            {
                "1D" => ("5min", 78),
                "1W" => ("1day", 7),
                "1M" => ("1day", 25),
                "3M" => ("1day", 70),
                "6M" => ("1day", 140),
                "1Y" => ("1week", 52),
                "5Y" => ("1month", 60),
                _ => ("1day", 30)
            };

            string url = $"{_baseUrl.TrimEnd('/')}/time_series?symbol={Uri.EscapeDataString(symbol)}&exchange={Uri.EscapeDataString(exchange)}&interval={interval}&outputsize={outputsize}&apikey={Uri.EscapeDataString(_apiKey)}";

            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) return results;

                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (var item in valuesEl.EnumerateArray())
                {
                    if (!item.TryGetProperty("datetime", out var dtEl) || !DateTime.TryParse(dtEl.GetString(), out DateTime dt))
                    {
                        continue;
                    }

                    decimal.TryParse(item.GetProperty("open").GetString(), out decimal o);
                    decimal.TryParse(item.GetProperty("high").GetString(), out decimal h);
                    decimal.TryParse(item.GetProperty("low").GetString(), out decimal l);
                    decimal.TryParse(item.GetProperty("close").GetString(), out decimal c);
                    long.TryParse(item.GetProperty("volume").GetString(), out long vol);

                    results.Add(new StockPriceHistory
                    {
                        IPOId = ipoId,
                        StockSymbol = symbol,
                        Exchange = exchange,
                        Price = c,
                        OpenPrice = o,
                        HighPrice = h,
                        LowPrice = l,
                        Volume = vol,
                        Timestamp = dt,
                        IsSimulated = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Twelve Data historical candles for {Symbol}:{Exchange}", symbol, exchange);
            }

            return results.OrderBy(r => r.Timestamp).ToList();
        }

        // =========================================================================
        // DEVELOPMENT SIMULATION TICK GENERATOR (CLEARLY LABELED AS SIMULATION)
        // =========================================================================

        private static MarketQuote GenerateSimulatedTick(
            int ipoId,
            string symbol,
            string exchange,
            decimal basePrice,
            MarketQuote? existing,
            DateTime now)
        {
            decimal prevClose = basePrice > 0 ? basePrice : 150m;
            decimal current = existing?.CurrentPrice ?? prevClose;

            decimal lowerCircuit = Math.Round(prevClose * 0.80m, 2);
            decimal upperCircuit = Math.Round(prevClose * 1.20m, 2);

            // Realistic mean-reverting Indian circuit bounded tick walk
            decimal pctDeviation = prevClose > 0 ? (prevClose - current) / prevClose : 0m;
            decimal drift = pctDeviation * 0.005m;
            decimal volatility = (decimal)((Random.Shared.NextDouble() - 0.495) * 0.0024);
            decimal deltaRupees = Math.Round(current * (drift + volatility), 2);

            if (deltaRupees == 0m)
            {
                deltaRupees = Random.Shared.Next(0, 2) == 0 ? 0.05m : -0.05m;
            }

            // Snap strictly to 5 paise tick size
            deltaRupees = Math.Round(deltaRupees * 20m) / 20m;
            decimal maxStep = Math.Max(0.50m, Math.Round(prevClose * 0.0025m, 2));
            deltaRupees = Math.Clamp(deltaRupees, -maxStep, maxStep);

            decimal newPrice = Math.Clamp(current + deltaRupees, lowerCircuit, upperCircuit);
            decimal dayHigh = Math.Max(existing?.DayHigh ?? newPrice, newPrice);
            decimal dayLow = Math.Min(existing?.DayLow ?? newPrice, newPrice);
            long addedVolume = Random.Shared.Next(15, 300);
            long newVolume = (existing?.Volume ?? 35000) + addedVolume;

            decimal priceChange = newPrice - prevClose;
            decimal changePercent = prevClose > 0 ? Math.Round(priceChange / prevClose * 100m, 2) : 0m;

            return new MarketQuote(
                Symbol: symbol,
                Exchange: exchange,
                CurrentPrice: newPrice,
                PreviousClose: prevClose,
                DayHigh: dayHigh,
                DayLow: dayLow,
                Volume: newVolume,
                LastUpdated: now,
                PriceChange: priceChange,
                PriceChangePercentage: changePercent,
                BidPrice: Math.Max(0.05m, newPrice - 0.05m),
                AskPrice: newPrice + 0.05m,
                IsSimulated: true,
                DataSource: "Simulation Engine (Development Mode)",
                IsStale: false,
                StatusMessage: "Development Simulation Mode Active: Realistic continuous prices within Indian circuit limits (±20%)."
            );
        }

        public static string CleanStockSymbol(string? rawSymbol, string ipoName)
        {
            if (!string.IsNullOrWhiteSpace(rawSymbol))
            {
                string s = rawSymbol.Trim().ToUpperInvariant();
                int colonIdx = s.IndexOf(':');
                if (colonIdx >= 0) s = s[..colonIdx];
                int dotIdx = s.IndexOf('.');
                if (dotIdx >= 0) s = s[..dotIdx];
                return s;
            }

            if (string.IsNullOrWhiteSpace(ipoName)) return "STOCK";

            string lower = ipoName.ToLowerInvariant();
            if (lower.Contains("tata motor")) return "TATAMOTORS";
            if (lower.Contains("tata tech")) return "TATATECH";
            if (lower.Contains("zomato")) return "ZOMATO";
            if (lower.Contains("swiggy")) return "SWIGGY";
            if (lower.Contains("paytm") || lower.Contains("one97")) return "PAYTM";
            if (lower.Contains("lic") || lower.Contains("life insurance")) return "LICI";
            if (lower.Contains("nykaa") || lower.Contains("fsn")) return "NYKAA";
            if (lower.Contains("hyundai")) return "HYUNDAI";
            if (lower.Contains("ola electric")) return "OLAELEC";
            if (lower.Contains("bajaj housing")) return "BAJAJHFL";
            if (lower.Contains("jio financial")) return "JIOFIN";
            if (lower.Contains("reliance")) return "RELIANCE";
            if (lower.Contains("infosys")) return "INFY";
            if (lower.Contains("tcs") || lower.Contains("tata consultancy")) return "TCS";
            if (lower.Contains("hdfc bank")) return "HDFCBANK";
            if (lower.Contains("icici bank")) return "ICICIBANK";

            var parts = ipoName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0].Length <= 9 ? parts[0].ToUpperInvariant() : parts[0][..9].ToUpperInvariant();
            }

            string acronym = string.Concat(parts.Select(p => p[0])).ToUpperInvariant();
            return acronym.Length >= 3 ? acronym : parts[0].ToUpperInvariant();
        }

        private static string BuildKey(int ipoId, string symbol, string exchange)
        {
            return $"IPO_{ipoId}_{symbol}_{exchange.ToUpperInvariant()}";
        }

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
