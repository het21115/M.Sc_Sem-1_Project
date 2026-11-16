using System.Collections.Concurrent;
using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public sealed class StockPriceBroadcastingService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMarketDataProvider _marketDataProvider;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<StockPriceBroadcastingService> _logger;
        private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(3.5);
        private readonly ConcurrentDictionary<string, DateTime> _lastHistorySaved = new();

        public StockPriceBroadcastingService(
            IServiceScopeFactory scopeFactory,
            IMarketDataProvider marketDataProvider,
            IHubContext<MarketHub> hubContext,
            ILogger<StockPriceBroadcastingService> logger)
        {
            _scopeFactory = scopeFactory;
            _marketDataProvider = marketDataProvider;
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Stock Price Broadcasting Service started.");

            // Allow application startup to complete
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessMarketTickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during market price tick and broadcasting.");
                }

                await Task.Delay(_tickInterval, stoppingToken);
            }

            _logger.LogInformation("Stock Price Broadcasting Service stopped.");
        }

        private async Task ProcessMarketTickAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var calendar = scope.ServiceProvider.GetRequiredService<IMarketTradingCalendar>();

            DateTime now = GetIndiaStandardTime();
            bool isMarketOpen = await calendar.IsMarketOpenAsync(now, cancellationToken);

            if (!isMarketOpen)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveMarketUpdate", new
                {
                    type = "MARKET_STATUS",
                    marketStatus = "Closed",
                    isMarketOpen = false,
                    serverTime = now.ToString("dd MMM yyyy, hh:mm:ss tt"),
                    dataSource = _marketDataProvider.IsSimulationMode ? "Simulation Engine (Development Mode)" : (_marketDataProvider.IsConfigured ? $"{_marketDataProvider.ProviderName} (NSE/BSE)" : "Exchange Database (Static/Offline)"),
                    isSimulation = _marketDataProvider.IsSimulationMode,
                    isStale = !_marketDataProvider.IsSimulationMode && !_marketDataProvider.IsConfigured,
                    warningMessage = _marketDataProvider.StatusMessage,
                    stocks = new List<object>()
                }, cancellationToken);
                return;
            }

            // Find all listed or active IPOs
            var ipos = await context.IPOs.AsNoTracking()
                .Where(ipo => ipo.Status == "Listed" ||
                    ((ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open" || ipo.Status == "Closed") &&
                     ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= now.Date) ||
                    context.IPOListings.Any(l => l.IPO_id == ipo.IPO_id))
                .ToListAsync(cancellationToken);

            // If no listed IPOs match by date, fallback to any listed/approved IPOs so testing is always functional
            if (ipos.Count == 0)
            {
                ipos = await context.IPOs.AsNoTracking()
                    .Where(ipo => ipo.Status == "Listed" || ipo.Status == "Published" || ipo.Status == "Approved" || ipo.Status == "Open" || ipo.Status == "Closed")
                    .Take(10)
                    .ToListAsync(cancellationToken);
            }

            if (ipos.Count == 0)
            {
                return;
            }

            int[] ipoIds = ipos.Select(ipo => ipo.IPO_id).ToArray();
            var companies = await context.Companies.AsNoTracking()
                .Where(company => ipos.Select(i => i.Company_id).Contains(company.Company_id))
                .ToDictionaryAsync(c => c.Company_id, c => c.Company_name, cancellationToken);

            var listings = await context.IPOListings.AsNoTracking()
                .Where(l => ipoIds.Contains(l.IPO_id))
                .ToDictionaryAsync(l => l.IPO_id, cancellationToken);

            // Collect tick items
            var tickItems = new List<(int IpoId, string Symbol, decimal BasePrice)>();
            foreach (var ipo in ipos)
            {
                listings.TryGetValue(ipo.IPO_id, out IPOListing? listing);
                string symbol = listing?.StockSymbol ?? GenerateStockSymbol(ipo.IPO_name);
                decimal basePrice = listing?.NSE_closing_price
                    ?? listing?.NSE_listing_price
                    ?? listing?.BSE_listing_price
                    ?? listing?.Issue_price
                    ?? ipo.Price_max
                    ?? ipo.Price_min
                    ?? 100m;

                tickItems.Add((ipo.IPO_id, symbol, basePrice));
            }

            // Update in-memory quotes in market data provider
            _marketDataProvider.UpdateTickPrices(tickItems);

            // Read existing StockMarketData rows from database to update
            var existingRows = await context.StockMarketData
                .Where(row => ipoIds.Contains(row.IPOId))
                .ToListAsync(cancellationToken);

            var stockTicks = new List<MarketStockTickDto>();

            foreach (var ipo in ipos)
            {
                listings.TryGetValue(ipo.IPO_id, out IPOListing? listing);
                string symbol = listing?.StockSymbol ?? GenerateStockSymbol(ipo.IPO_name);
                string companyName = companies.GetValueOrDefault(ipo.Company_id, "Company");

                decimal defaultPrice = listing?.NSE_closing_price
                    ?? listing?.NSE_listing_price
                    ?? listing?.BSE_listing_price
                    ?? listing?.Issue_price
                    ?? ipo.Price_max
                    ?? ipo.Price_min
                    ?? 100m;

                MarketQuote? nseQuote = await _marketDataProvider.GetIpoQuoteAsync(ipo.IPO_id, "NSE", defaultPrice, symbol, cancellationToken);
                MarketQuote? bseQuote = await _marketDataProvider.GetIpoQuoteAsync(ipo.IPO_id, "BSE", defaultPrice, symbol, cancellationToken);

                if (nseQuote == null || bseQuote == null)
                {
                    continue;
                }

                // Upsert NSE database record
                SyncStockMarketDataRow(context, existingRows, ipo, "NSE", symbol, listing?.Issue_price ?? defaultPrice, nseQuote);

                // Upsert BSE database record
                SyncStockMarketDataRow(context, existingRows, ipo, "BSE", symbol, listing?.Issue_price ?? defaultPrice, bseQuote);

                // Persist historical price points for charting and analysis
                RecordPriceHistory(context, ipo.IPO_id, symbol, "NSE", nseQuote, now);
                RecordPriceHistory(context, ipo.IPO_id, symbol, "BSE", bseQuote, now);

                // Add to broadcast payload
                stockTicks.Add(new MarketStockTickDto
                {
                    IpoId = ipo.IPO_id,
                    IpoName = ipo.IPO_name,
                    CompanyName = companyName,
                    StockSymbol = symbol,

                    NsePrice = nseQuote.CurrentPrice,
                    NseChange = nseQuote.PriceChange,
                    NseChangePercent = nseQuote.PriceChangePercentage,
                    NseHigh = nseQuote.DayHigh,
                    NseLow = nseQuote.DayLow,
                    NseVolume = nseQuote.Volume,

                    BsePrice = bseQuote.CurrentPrice,
                    BseChange = bseQuote.PriceChange,
                    BseChangePercent = bseQuote.PriceChangePercentage,
                    BseHigh = bseQuote.DayHigh,
                    BseLow = bseQuote.DayLow,
                    BseVolume = bseQuote.Volume,

                    LastUpdated = now.ToString("hh:mm:ss tt"),
                    IsStale = nseQuote.IsStale || bseQuote.IsStale,
                    DataSource = nseQuote.DataSource
                });
            }

            // Sync holding current prices in Investments table
            var userInvestments = await context.Investments
                .Where(inv => ipoIds.Contains(inv.IPO_id) && inv.Status != "Closed")
                .ToListAsync(cancellationToken);

            foreach (var inv in userInvestments)
            {
                var tick = stockTicks.FirstOrDefault(t => t.IpoId == inv.IPO_id);
                if (tick != null)
                {
                    inv.Current_price = tick.NsePrice;
                }
            }

            await context.SaveChangesAsync(cancellationToken);

            // Broadcast real-time update via SignalR to all connected browsers
            var broadcastPayload = new LiveMarketBroadcastDto
            {
                Type = "MARKET_TICK",
                MarketStatus = isMarketOpen ? "Open" : "Closed",
                IsMarketOpen = isMarketOpen,
                ServerTime = now.ToString("dd MMM yyyy, hh:mm:ss tt"),
                DataSource = _marketDataProvider.IsSimulationMode ? "Simulation Engine (Development Mode)" : (_marketDataProvider.IsConfigured ? $"{_marketDataProvider.ProviderName} (NSE/BSE)" : "Exchange Database (Static/Offline)"),
                IsSimulation = _marketDataProvider.IsSimulationMode,
                IsStale = !_marketDataProvider.IsSimulationMode && (!_marketDataProvider.IsConfigured || stockTicks.Any(t => t.IsStale)),
                WarningMessage = _marketDataProvider.StatusMessage,
                Stocks = stockTicks
            };

            await _hubContext.Clients.All.SendAsync("ReceiveMarketUpdate", broadcastPayload, cancellationToken);
        }

        private static void SyncStockMarketDataRow(
            ApplicationDbContext context,
            List<StockMarketData> existingRows,
            IPO ipo,
            string exchange,
            string symbol,
            decimal issuePrice,
            MarketQuote quote)
        {
            var row = existingRows.FirstOrDefault(r => r.IPOId == ipo.IPO_id && string.Equals(r.Exchange, exchange, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                row = new StockMarketData
                {
                    IPOId = ipo.IPO_id,
                    CompanyId = ipo.Company_id,
                    StockSymbol = symbol,
                    Exchange = exchange,
                    IssuePrice = issuePrice,
                    ListingPrice = quote.PreviousClose,
                    CurrentPrice = quote.CurrentPrice,
                    PreviousClose = quote.PreviousClose,
                    DayHigh = quote.DayHigh,
                    DayLow = quote.DayLow,
                    Volume = quote.Volume,
                    PriceChange = quote.PriceChange,
                    PriceChangePercentage = quote.PriceChangePercentage,
                    LastUpdated = quote.LastUpdated,
                    IsAvailable = true,
                    IsSimulated = quote.IsSimulated,
                    DataSource = quote.DataSource
                };
                context.StockMarketData.Add(row);
                existingRows.Add(row);
            }
            else
            {
                row.StockSymbol = symbol;
                row.CurrentPrice = quote.CurrentPrice;
                row.PreviousClose = quote.PreviousClose;
                row.DayHigh = quote.DayHigh;
                row.DayLow = quote.DayLow;
                row.Volume = quote.Volume;
                row.PriceChange = quote.PriceChange;
                row.PriceChangePercentage = quote.PriceChangePercentage;
                row.LastUpdated = quote.LastUpdated;
                row.IsAvailable = true;
                row.IsSimulated = quote.IsSimulated;
                row.DataSource = quote.DataSource;
            }
        }

        private void RecordPriceHistory(ApplicationDbContext context, int ipoId, string symbol, string exchange, MarketQuote quote, DateTime now)
        {
            string key = $"{ipoId}_{exchange.ToUpperInvariant()}";
            if (_lastHistorySaved.TryGetValue(key, out DateTime lastTime) && (now - lastTime).TotalSeconds < 15)
            {
                return;
            }

            _lastHistorySaved[key] = now;
            context.StockPriceHistories.Add(new StockPriceHistory
            {
                IPOId = ipoId,
                StockSymbol = symbol,
                Exchange = exchange,
                Price = quote.CurrentPrice,
                OpenPrice = quote.PreviousClose,
                HighPrice = quote.DayHigh,
                LowPrice = quote.DayLow,
                Volume = quote.Volume,
                Timestamp = now,
                IsSimulated = quote.IsSimulated
            });
        }

        private static string GenerateStockSymbol(string ipoName)
        {
            if (string.IsNullOrWhiteSpace(ipoName))
            {
                return "STOCK";
            }

            var parts = ipoName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return parts[0].Length <= 8 ? parts[0].ToUpperInvariant() : parts[0][..8].ToUpperInvariant();
            }

            var clean = string.Concat(parts.Select(p => p[0])).ToUpperInvariant();
            return clean.Length >= 3 ? clean : (parts[0].Length >= 4 ? parts[0][..4].ToUpperInvariant() : clean);
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
