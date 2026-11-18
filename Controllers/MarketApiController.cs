using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using IPOInvestmentManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Controllers
{
    [ApiController]
    [Route("api/market")]
    public class MarketApiController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IStockMarketService _stockMarketService;
        private readonly IMarketDataProvider _marketDataProvider;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<MarketApiController> _logger;

        public MarketApiController(
            ApplicationDbContext context,
            IStockMarketService stockMarketService,
            IMarketDataProvider marketDataProvider,
            IHubContext<MarketHub> hubContext,
            ILogger<MarketApiController> logger)
        {
            _context = context;
            _stockMarketService = stockMarketService;
            _marketDataProvider = marketDataProvider;
            _hubContext = hubContext;
            _logger = logger;
        }

        [HttpGet("live")]
        public async Task<IActionResult> GetLiveMarket()
        {
            var dashboard = await _stockMarketService.GetDashboardAsync();
            return Ok(new
            {
                isMarketOpen = dashboard.IsMarketOpen,
                marketStatus = dashboard.MarketStatus,
                isSimulationActive = dashboard.IsSimulationActive,
                dataSource = dashboard.DataSource,
                isStale = dashboard.IsStale,
                warningMessage = dashboard.WarningMessage,
                serverTime = DateTime.Now.ToString("dd MMM yyyy, hh:mm:ss tt"),
                message = dashboard.IsMarketOpen 
                    ? "Live trading session active (NSE & BSE)." 
                    : "Market is closed. Live share prices and trading are accessible exclusively during official market hours (Monday – Friday, 09:30 AM to 03:30 PM IST).",
                stocks = dashboard.IsMarketOpen 
                    ? dashboard.Listings.Select(row => new
                    {
                        ipoId = row.IpoId,
                        ipoName = row.IpoName,
                        companyName = row.CompanyName,
                        stockSymbol = row.StockSymbol,
                        dataSource = row.DataSource,
                        isStale = row.IsStale,
                        staleWarning = row.StaleWarning,

                        // NSE
                        nsePrice = row.NsePrice ?? row.CurrentPrice ?? 0m,
                        nseChange = row.NsePriceChange ?? row.PriceChange ?? 0m,
                        nseChangePercent = row.NsePriceChangePercentage ?? row.PriceChangePercentage ?? 0m,
                        nseHigh = row.NseDayHigh ?? row.DayHigh ?? 0m,
                        nseLow = row.NseDayLow ?? row.DayLow ?? 0m,
                        nseVolume = row.NseVolume ?? row.Volume ?? 0,

                        // BSE
                        bsePrice = row.BsePrice ?? row.CurrentPrice ?? 0m,
                        bseChange = row.BsePriceChange ?? row.PriceChange ?? 0m,
                        bseChangePercent = row.BsePriceChangePercentage ?? row.PriceChangePercentage ?? 0m,
                        bseHigh = row.BseDayHigh ?? row.DayHigh ?? 0m,
                        bseLow = row.BseDayLow ?? row.DayLow ?? 0m,
                        bseVolume = row.BseVolume ?? row.Volume ?? 0,

                        bestBid = row.BestBidPrice ?? 0m,
                        bestAsk = row.BestAskPrice ?? 0m,
                        lastUpdated = row.LastUpdated?.ToString("hh:mm:ss tt") ?? DateTime.Now.ToString("hh:mm:ss tt")
                    })
                    : Enumerable.Empty<object>()
            });
        }

        [HttpGet("quote/{ipoId}")]
        public async Task<IActionResult> GetQuote(int ipoId)
        {
            var data = await _stockMarketService.GetMarketDataAsync(ipoId);
            if (data == null)
            {
                return NotFound(new { message = "Stock not found" });
            }

            return Ok(data);
        }

        [HttpGet("chart/{ipoId}")]
        public async Task<IActionResult> GetStockChart(int ipoId, [FromQuery] string timeframe = "1D", [FromQuery] string exchange = "NSE")
        {
            var data = await _stockMarketService.GetMarketDataAsync(ipoId);
            if (data == null)
            {
                return NotFound(new { message = "Stock not found" });
            }

            bool isBse = string.Equals(exchange, "BSE", StringComparison.OrdinalIgnoreCase);
            string exch = isBse ? "BSE" : "NSE";
            decimal currentPrice = isBse ? (data.BsePrice ?? data.CurrentPrice ?? 100m) : (data.NsePrice ?? data.CurrentPrice ?? 100m);
            decimal previousClose = isBse ? (data.BsePreviousClose ?? data.PreviousClose ?? currentPrice) : (data.NsePreviousClose ?? data.PreviousClose ?? currentPrice);
            decimal dayHigh = Math.Max(currentPrice, isBse ? (data.BseDayHigh ?? data.DayHigh ?? currentPrice) : (data.NseDayHigh ?? data.DayHigh ?? currentPrice));
            decimal dayLow = Math.Min(currentPrice, isBse ? (data.BseDayLow ?? data.DayLow ?? currentPrice) : (data.NseDayLow ?? data.DayLow ?? currentPrice));
            decimal priceChange = currentPrice - previousClose;
            decimal priceChangePct = previousClose > 0 ? Math.Round(priceChange / previousClose * 100m, 2) : 0m;
            decimal openPrice = previousClose;

            var ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == ipoId);
            DateTime? listingDate = ipo?.Listing_date;

            string tf = string.IsNullOrWhiteSpace(timeframe) ? "1D" : timeframe.ToUpperInvariant();
            var (points, candles, dataSource, dataNotice, isStale) = await BuildChartHistoryAsync(
                ipoId,
                data.StockSymbol,
                exch,
                tf,
                previousClose,
                currentPrice,
                dayHigh,
                dayLow,
                openPrice,
                listingDate);

            decimal lowerCircuit = Math.Round(previousClose * 0.80m, 2);
            decimal upperCircuit = Math.Round(previousClose * 1.20m, 2);
            decimal high52w = Math.Round(Math.Max(dayHigh, previousClose) * 1.35m, 2);
            decimal low52w = Math.Round(Math.Min(dayLow, previousClose) * 0.70m, 2);

            return Ok(new
            {
                ipoId = data.IpoId,
                ipoName = data.IpoName,
                companyName = data.CompanyName,
                stockSymbol = data.StockSymbol,
                exchange = exch,
                currentPrice,
                previousClose,
                priceChange,
                priceChangePercentage = priceChangePct,
                dayHigh,
                dayLow,
                openPrice,
                lowerCircuit,
                upperCircuit,
                high52w,
                low52w,
                volume = isBse ? (data.BseVolume ?? 32000) : (data.NseVolume ?? 45000),
                bestBid = data.BestBidPrice ?? Math.Max(0.05m, currentPrice - 0.05m),
                bestAsk = data.BestAskPrice ?? (currentPrice + 0.05m),
                isMarketOpen = data.IsMarketOpen,
                isSimulation = _marketDataProvider.IsSimulationMode,
                isStale,
                dataSource,
                dataNotice,
                warningMessage = isStale ? dataNotice : null,
                listingDate = listingDate?.ToString("dd MMM yyyy"),
                timeframe = tf,
                points,
                candles
            });
        }

        private async Task<(List<object> Points, List<object> Candles, string DataSource, string DataNotice, bool IsStale)> BuildChartHistoryAsync(
            int ipoId,
            string symbol,
            string exchange,
            string timeframe,
            decimal previousClose,
            decimal currentPrice,
            decimal dayHigh,
            decimal dayLow,
            decimal openPrice,
            DateTime? listingDate)
        {
            var points = new List<object>();
            var candles = new List<object>();
            DateTime now = GetIndiaStandardTime();
            bool isSim = _marketDataProvider.IsSimulationMode || !_marketDataProvider.IsConfigured;

            // 1. DEVELOPMENT SIMULATION MODE (Clearly labeled)
            if (isSim)
            {
                string simDataSource = "Simulation Engine (Development Mode)";
                string simDataNotice = "⚠️ Simulation Mode: Realistic continuous developmental price simulation within Indian circuit limits (±20%).";

                if (timeframe == "1D")
                {
                    DateTime sessionStart = now.Date.AddHours(9).AddMinutes(30);
                    DateTime sessionCurrent = now > sessionStart ? now : sessionStart.AddHours(6);
                    int totalMinutes = (int)Math.Max(15, (sessionCurrent - sessionStart).TotalMinutes);
                    int stepMinutes = 5;
                    int intervals = Math.Clamp(totalMinutes / stepMinutes, 10, 60);

                    var rnd = new Random(ipoId * 79 + exchange.GetHashCode());
                    decimal walkPrice = openPrice > 0 ? openPrice : previousClose;
                    decimal lowerCircuit = Math.Round(previousClose * 0.80m, 2);
                    decimal upperCircuit = Math.Round(previousClose * 1.20m, 2);

                    var backfill = new List<StockPriceHistory>();

                    for (int i = 0; i < intervals; i++)
                    {
                        DateTime ptTime = sessionStart.AddMinutes(i * stepMinutes);
                        string label = ptTime.ToString("hh:mm tt");

                        if (i == intervals - 1)
                        {
                            walkPrice = currentPrice;
                        }
                        else
                        {
                            double progress = (double)i / (intervals - 1);
                            decimal target = openPrice + (decimal)progress * (currentPrice - openPrice);
                            double noise = (rnd.NextDouble() - 0.495) * 0.003;
                            walkPrice = Math.Round(target + (target * (decimal)noise), 2);
                            walkPrice = Math.Round(walkPrice * 20m) / 20m;
                            walkPrice = Math.Clamp(walkPrice, lowerCircuit, upperCircuit);
                        }

                        long volume = rnd.Next(1500, 9500);
                        points.Add(new { label, price = walkPrice, volume });

                        decimal cOpen = walkPrice;
                        decimal cDelta = Math.Max(0.05m, Math.Round((decimal)(rnd.NextDouble() * 0.35), 2));
                        decimal cHigh = Math.Min(upperCircuit, walkPrice + cDelta);
                        decimal cLow = Math.Max(lowerCircuit, walkPrice - cDelta);

                        candles.Add(new { label, open = cOpen, high = cHigh, low = cLow, close = walkPrice, volume });

                        backfill.Add(new StockPriceHistory
                        {
                            IPOId = ipoId,
                            StockSymbol = symbol,
                            Exchange = exchange,
                            Price = walkPrice,
                            OpenPrice = cOpen,
                            HighPrice = cHigh,
                            LowPrice = cLow,
                            Volume = volume,
                            Timestamp = ptTime,
                            IsSimulated = true
                        });
                    }

                    try
                    {
                        bool alreadyHasToday = await _context.StockPriceHistories
                            .AnyAsync(h => h.IPOId == ipoId && h.Exchange == exchange && h.Timestamp.Date == now.Date);
                        if (!alreadyHasToday)
                        {
                            _context.StockPriceHistories.AddRange(backfill);
                            await _context.SaveChangesAsync();
                        }
                    }
                    catch { }

                    return (points, candles, simDataSource, simDataNotice, false);
                }

                // Multi-day simulation
                int simDays = timeframe switch
                {
                    "1W" => 5,
                    "1M" => 22,
                    "3M" => 65,
                    "6M" => 130,
                    "1Y" => 250,
                    "5Y" => 1250,
                    _ => 250
                };

                DateTime startDate = now.AddDays(-Math.Max(7, (int)(simDays * 1.45)));
                int count = Math.Clamp(simDays, 5, 80);
                var rndMulti = new Random(ipoId * 137 + timeframe.GetHashCode());
                decimal basePrice = previousClose > 0 ? previousClose : 100m;
                decimal runningP = Math.Round(basePrice * (1m - (count * 0.002m)), 2);
                if (runningP <= 0m) runningP = basePrice * 0.85m;

                for (int i = 0; i < count; i++)
                {
                    double fraction = count <= 1 ? 1.0 : (double)i / (count - 1);
                    decimal target = runningP + (decimal)fraction * (currentPrice - runningP);
                    double dailyVol = (rndMulti.NextDouble() - 0.495) * 0.012;
                    decimal dayPrice = i == count - 1 ? currentPrice : Math.Round(target + (target * (decimal)dailyVol), 2);
                    dayPrice = Math.Round(dayPrice * 20m) / 20m;
                    if (dayPrice < 0.05m) dayPrice = 0.05m;

                    DateTime ptDate = startDate.AddDays(fraction * (now - startDate).TotalDays);
                    string label = FormatTimeframeLabel(ptDate, timeframe);
                    long vol = rndMulti.Next(25000, 150000);
                    points.Add(new { label, price = dayPrice, volume = vol });

                    decimal cOpen = runningP;
                    decimal cClose = dayPrice;
                    decimal spread = Math.Max(0.10m, Math.Abs(cClose - cOpen) * 0.5m);
                    decimal cHigh = Math.Max(cOpen, cClose) + Math.Round((decimal)(rndMulti.NextDouble() * 0.4) + spread, 2);
                    decimal cLow = Math.Max(0.05m, Math.Min(cOpen, cClose) - Math.Round((decimal)(rndMulti.NextDouble() * 0.4) + spread, 2));

                    candles.Add(new
                    {
                        label,
                        open = Math.Round(cOpen, 2),
                        high = Math.Round(cHigh, 2),
                        low = Math.Round(cLow, 2),
                        close = Math.Round(cClose, 2),
                        volume = vol
                    });

                    runningP = dayPrice;
                }

                return (points, candles, simDataSource, simDataNotice, false);
            }

            // 2. GENUINE LIVE DATA INTEGRATION (Never display simulated data as real)
            DateTime filterStartDate = timeframe switch
            {
                "1D" => now.Date.AddHours(9).AddMinutes(15),
                "1W" => now.AddDays(-7),
                "1M" => now.AddMonths(-1),
                "3M" => now.AddMonths(-3),
                "6M" => now.AddMonths(-6),
                "1Y" => now.AddYears(-1),
                "5Y" => now.AddYears(-5),
                "ALL" => listingDate.HasValue && listingDate.Value.Date <= now.Date ? listingDate.Value.Date : now.AddYears(-5),
                _ => now.Date.AddHours(9).AddMinutes(15)
            };

            // Query genuine records from SQL Server
            var dbRealHistory = await _context.StockPriceHistories.AsNoTracking()
                .Where(h => h.IPOId == ipoId && h.Exchange == exchange && h.Timestamp >= filterStartDate && !h.IsSimulated)
                .OrderBy(h => h.Timestamp)
                .ToListAsync();

            if (dbRealHistory.Count >= 5)
            {
                if (timeframe == "1D")
                {
                    foreach (var h in dbRealHistory)
                    {
                        string label = h.Timestamp.ToString("hh:mm tt");
                        points.Add(new { label, price = h.Price, volume = h.Volume });
                        candles.Add(new { label, open = h.OpenPrice, high = h.HighPrice, low = h.LowPrice, close = h.Price, volume = h.Volume });
                    }
                    points.Add(new { label = now.ToString("hh:mm tt"), price = currentPrice, volume = 0 });
                }
                else
                {
                    var grouped = dbRealHistory.GroupBy(h => h.Timestamp.Date).OrderBy(g => g.Key);
                    foreach (var g in grouped)
                    {
                        var first = g.First();
                        var last = g.Last();
                        decimal gOpen = first.OpenPrice > 0 ? first.OpenPrice : first.Price;
                        decimal gClose = last.Price;
                        decimal gHigh = g.Max(x => x.HighPrice > 0 ? x.HighPrice : x.Price);
                        decimal gLow = g.Min(x => x.LowPrice > 0 ? x.LowPrice : x.Price);
                        long gVol = g.Sum(x => x.Volume);
                        string label = FormatTimeframeLabel(g.Key, timeframe);
                        points.Add(new { label, price = gClose, volume = gVol });
                        candles.Add(new { label, open = gOpen, high = gHigh, low = gLow, close = gClose, volume = gVol });
                    }
                }

                string genuineSource = _marketDataProvider.IsConfigured 
                    ? $"{_marketDataProvider.ProviderName} (NSE/BSE)" 
                    : "Exchange Database (Verified Records)";
                string genuineNotice = "Genuine historical price points recorded in SQL Server.";
                return (points, candles, genuineSource, genuineNotice, false);
            }

            // If SQL Server has insufficient records, try to fetch genuine history from provider API
            if (_marketDataProvider.IsConfigured)
            {
                var providerHistory = (await _marketDataProvider.GetHistoricalPricesAsync(
                    ipoId, symbol, exchange, timeframe, HttpContext.RequestAborted)).ToList();

                if (providerHistory.Count > 0)
                {
                    try
                    {
                        _context.StockPriceHistories.AddRange(providerHistory);
                        await _context.SaveChangesAsync();
                    }
                    catch { }

                    foreach (var h in providerHistory)
                    {
                        string label = timeframe == "1D" ? h.Timestamp.ToString("hh:mm tt") : FormatTimeframeLabel(h.Timestamp.Date, timeframe);
                        points.Add(new { label, price = h.Price, volume = h.Volume });
                        candles.Add(new { label, open = h.OpenPrice, high = h.HighPrice, low = h.LowPrice, close = h.Price, volume = h.Volume });
                    }

                    string liveSource = $"{_marketDataProvider.ProviderName} (NSE/BSE Historical)";
                    string liveNotice = $"Genuine historical market data fetched from {_marketDataProvider.ProviderName}.";
                    return (points, candles, liveSource, liveNotice, false);
                }
            }

            // 3. Provider credentials missing or no historical feed available:
            // Do NOT fabricate random numbers! Display verified baseline price points with clear stale/unconfigured warning.
            string offlineSource = "Exchange Database (Static/Offline)";
            string offlineNotice = _marketDataProvider.StatusMessage ?? "Market data provider API credentials not configured. Showing recorded baseline exchange price.";
            
            decimal basePt = previousClose > 0 ? previousClose : (openPrice > 0 ? openPrice : currentPrice);
            string startLbl = listingDate.HasValue ? listingDate.Value.ToString("dd MMM yyyy") : filterStartDate.ToString("dd MMM yyyy");
            string nowLbl = now.ToString("dd MMM yyyy");

            points.Add(new { label = startLbl, price = basePt, volume = 0 });
            points.Add(new { label = nowLbl, price = currentPrice, volume = 0 });

            candles.Add(new { label = startLbl, open = basePt, high = basePt, low = basePt, close = basePt, volume = 0 });
            candles.Add(new { label = nowLbl, open = basePt, high = dayHigh, low = dayLow, close = currentPrice, volume = 0 });

            return (points, candles, offlineSource, offlineNotice, true);
        }

        private static string FormatTimeframeLabel(DateTime dt, string timeframe) => timeframe switch
        {
            "1W" => dt.ToString("ddd, dd MMM"),
            "1M" or "3M" or "6M" => dt.ToString("dd MMM"),
            "1Y" => dt.ToString("MMM yy"),
            "5Y" or "ALL" => dt.ToString("MMM yyyy"),
            _ => dt.ToString("dd MMM")
        };

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

        public class FastTradeApiRequest
        {
            public int IpoId { get; set; }
            public string Exchange { get; set; } = "NSE";
            public string TradeType { get; set; } = "BUY";
            public int Quantity { get; set; }
            public int? InvestmentId { get; set; }
        }

        [HttpPost("trade")]
        public async Task<IActionResult> ExecuteTrade([FromBody] FastTradeApiRequest request)
        {
            int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
            string? role = HttpContext.Session.GetString("UserRole");

            if (!userId.HasValue || !string.Equals(role, "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Unauthorized(new { success = false, message = "Trading security policy: Secondary market trading is strictly restricted to authenticated investors." });
            }

            if (request.Quantity <= 0 || request.Quantity > 500_000)
            {
                return BadRequest(new { success = false, message = "Invalid trade quantity. Quantity must be between 1 and 500,000 shares per order." });
            }

            if (!string.Equals(request.TradeType, "BUY", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.TradeType, "SELL", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { success = false, message = "Invalid trade type. Only BUY and SELL orders are supported." });
            }

            string exchange = string.Equals(request.Exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";
            bool isBuy = string.Equals(request.TradeType, "BUY", StringComparison.OrdinalIgnoreCase);

            // Server-side trading security: Security must exist and be actively listed
            var ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == request.IpoId);
            if (ipo is null)
            {
                return NotFound(new { success = false, message = "Security or IPO not found." });
            }

            bool isListedSecurity = string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase) ||
                (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= DateTime.UtcNow.Date);

            if (!isListedSecurity)
            {
                return BadRequest(new { success = false, message = $"Trading security policy: Secondary market trading is restricted to publicly listed stocks. '{ipo.IPO_name}' is currently in '{ipo.Status}' status." });
            }

            var marketData = await _stockMarketService.GetMarketDataAsync(request.IpoId);
            if (marketData == null || !marketData.IsMarketOpen || !marketData.CanTrade)
            {
                return BadRequest(new { success = false, message = "Market is currently closed. Secondary market trading is permitted strictly during official exchange hours (Monday – Friday, 09:30 AM to 03:30 PM IST) or when market simulation is activated by an Administrator." });
            }

            decimal tradePrice = exchange == "BSE" ? (marketData.BsePrice ?? marketData.CurrentPrice ?? 0m) : (marketData.NsePrice ?? marketData.CurrentPrice ?? 0m);
            if (tradePrice <= 0m)
            {
                return BadRequest(new { success = false, message = "Authoritative market price is unavailable. Trade rejected for safety." });
            }

            // Circuit limit safety check
            decimal prevClose = exchange == "BSE" ? (marketData.BsePreviousClose ?? marketData.PreviousClose ?? tradePrice) : (marketData.NsePreviousClose ?? marketData.PreviousClose ?? tradePrice);
            if (prevClose > 0m)
            {
                decimal lowerCircuit = Math.Round(prevClose * 0.80m, 2);
                decimal upperCircuit = Math.Round(prevClose * 1.20m, 2);
                if (tradePrice < lowerCircuit || tradePrice > upperCircuit)
                {
                    return BadRequest(new { success = false, message = $"Trading price ₹{tradePrice:N2} breached daily circuit filter limits (₹{lowerCircuit:N2} - ₹{upperCircuit:N2})." });
                }
            }

            decimal totalAmount = request.Quantity * tradePrice;
            User? investor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == userId.Value && u.Is_active);
            if (investor == null)
            {
                return NotFound(new { success = false, message = "Active investor account record not found." });
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                int remainingQty = 0;
                string userName = $"{investor.First_name} {investor.Last_name}".Trim();
                if (string.IsNullOrWhiteSpace(userName)) userName = "Investor #" + investor.User_id;

                if (isBuy)
                {
                    if (investor.SimulatedCashBalance < totalAmount)
                    {
                        return BadRequest(new { success = false, message = $"Insufficient simulated balance. Needed ₹{totalAmount:N2}, available ₹{investor.SimulatedCashBalance:N2}." });
                    }

                    Investment? investment = await _context.Investments.FirstOrDefaultAsync(i =>
                        i.User_id == userId.Value && i.IPO_id == request.IpoId && i.Status != "Closed");

                    if (investment == null)
                    {
                        investment = new Investment
                        {
                            User_id = userId.Value,
                            IPO_id = request.IpoId,
                            Quantity = request.Quantity,
                            Buy_price = tradePrice,
                            Buy_date = DateTime.Now,
                            Current_price = tradePrice,
                            Status = "Holding"
                        };
                        _context.Investments.Add(investment);
                        await _context.SaveChangesAsync();
                        remainingQty = request.Quantity;
                    }
                    else
                    {
                        decimal existingCost = (investment.Buy_price ?? 0m) * investment.Quantity;
                        investment.Buy_price = (existingCost + totalAmount) / (investment.Quantity + request.Quantity);
                        investment.Quantity += request.Quantity;
                        investment.Current_price = tradePrice;
                        investment.Status = "Holding";
                        remainingQty = investment.Quantity;
                    }

                    investor.SimulatedCashBalance -= totalAmount;

                    _context.InvestmentTransactions.Add(new InvestmentTransaction
                    {
                        Investment_id = investment.Investment_id,
                        Transaction_type = "BUY",
                        Quantity = request.Quantity,
                        Price = tradePrice,
                        Total_amount = totalAmount,
                        Transaction_date = DateTime.Now
                    });

                    _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                    {
                        UserId = userId.Value,
                        AdminUserId = null,
                        TransactionType = "TRADE_BUY",
                        Amount = -totalAmount,
                        BalanceBefore = investor.SimulatedCashBalance + totalAmount,
                        BalanceAfter = investor.SimulatedCashBalance,
                        Reason = $"Market purchase: {request.Quantity:N0} shares of {marketData.IpoName} ({exchange}) @ ₹{tradePrice:N2}",
                        CreatedAt = DateTime.Now
                    });

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Broadcast real-time trade execution to all connected users
                    await _hubContext.Clients.All.SendAsync("ReceiveTradeNotification", new
                    {
                        stockName = marketData.IpoName,
                        exchange = exchange,
                        tradeType = "BUY",
                        quantity = request.Quantity,
                        price = tradePrice,
                        totalAmount = totalAmount,
                        user = userName,
                        timestamp = DateTime.Now.ToString("hh:mm:ss tt")
                    });

                    return Ok(new TradeExecutionResultDto
                    {
                        Success = true,
                        Message = $"Successfully bought {request.Quantity:N0} shares of {marketData.IpoName} on {exchange} at ₹{tradePrice:N2}.",
                        TransactionType = "BUY",
                        StockName = marketData.IpoName,
                        Exchange = exchange,
                        Quantity = request.Quantity,
                        Price = tradePrice,
                        TotalAmount = totalAmount,
                        NewCashBalance = investor.SimulatedCashBalance,
                        RemainingHoldingQuantity = remainingQty
                    });
                }
                else
                {
                    // SELL logic
                    Investment? investment = null;
                    if (request.InvestmentId.HasValue && request.InvestmentId.Value > 0)
                    {
                        investment = await _context.Investments.FirstOrDefaultAsync(i =>
                            i.Investment_id == request.InvestmentId.Value && i.User_id == userId.Value);
                    }
                    else
                    {
                        investment = await _context.Investments.FirstOrDefaultAsync(i =>
                            i.User_id == userId.Value && i.IPO_id == request.IpoId && i.Status != "Closed");
                    }

                    if (investment == null)
                    {
                        return BadRequest(new { success = false, message = "No active holdings found to sell." });
                    }

                    int soldQuantity = await _context.InvestmentTransactions
                        .Where(t => t.Investment_id == investment.Investment_id && t.Transaction_type == "SELL")
                        .SumAsync(t => (int?)t.Quantity) ?? 0;

                    int availableQuantity = Math.Max(0, investment.Quantity - soldQuantity);
                    if (request.Quantity > availableQuantity)
                    {
                        return BadRequest(new { success = false, message = $"You only have {availableQuantity:N0} shares available to sell." });
                    }

                    investor.SimulatedCashBalance += totalAmount;

                    _context.InvestmentTransactions.Add(new InvestmentTransaction
                    {
                        Investment_id = investment.Investment_id,
                        Transaction_type = "SELL",
                        Quantity = request.Quantity,
                        Price = tradePrice,
                        Total_amount = totalAmount,
                        Transaction_date = DateTime.Now
                    });

                    _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                    {
                        UserId = userId.Value,
                        AdminUserId = null,
                        TransactionType = "TRADE_SELL",
                        Amount = totalAmount,
                        BalanceBefore = investor.SimulatedCashBalance - totalAmount,
                        BalanceAfter = investor.SimulatedCashBalance,
                        Reason = $"Market sale: {request.Quantity:N0} shares of {marketData.IpoName} ({exchange}) @ ₹{tradePrice:N2}",
                        CreatedAt = DateTime.Now
                    });

                    remainingQty = availableQuantity - request.Quantity;
                    if (remainingQty == 0)
                    {
                        investment.Status = "Closed";
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Broadcast real-time trade execution to all connected users
                    await _hubContext.Clients.All.SendAsync("ReceiveTradeNotification", new
                    {
                        stockName = marketData.IpoName,
                        exchange = exchange,
                        tradeType = "SELL",
                        quantity = request.Quantity,
                        price = tradePrice,
                        totalAmount = totalAmount,
                        user = userName,
                        timestamp = DateTime.Now.ToString("hh:mm:ss tt")
                    });

                    return Ok(new TradeExecutionResultDto
                    {
                        Success = true,
                        Message = $"Successfully sold {request.Quantity:N0} shares of {marketData.IpoName} on {exchange} at ₹{tradePrice:N2}.",
                        TransactionType = "SELL",
                        StockName = marketData.IpoName,
                        Exchange = exchange,
                        Quantity = request.Quantity,
                        Price = tradePrice,
                        TotalAmount = totalAmount,
                        NewCashBalance = investor.SimulatedCashBalance,
                        RemainingHoldingQuantity = remainingQty
                    });
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Trade execution failed");
                return StatusCode(500, new { success = false, message = "An error occurred while executing the trade." });
            }
        }

        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] AdminBroadcastRequestDto request)
        {
            int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
            string? role = HttpContext.Session.GetString("UserRole");

            if (!userId.HasValue)
            {
                return Unauthorized(new { success = false, message = "Authentication required. Please sign in as an administrator." });
            }

            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = "Access denied: Only administrators are authorized to send broadcasts." });
            }

            if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { success = false, message = "Title and message are required." });
            }

            DateTime now = DateTime.UtcNow;
            var activeInvestors = await _context.Users
                .AsNoTracking()
                .Where(u => u.Role == "Investor" && u.Is_active)
                .Select(u => u.User_id)
                .ToListAsync();

            var notifications = activeInvestors.Select(id => new SystemNotification
            {
                User_id = id,
                Title = request.Title.Trim(),
                Message = request.Message.Trim(),
                Created_at = now,
                Is_read = false
            });

            _context.SystemNotifications.AddRange(notifications);
            await _context.SaveChangesAsync();

            // Real-time broadcast to all open investor screens via SignalR
            await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
            {
                title = request.Title.Trim(),
                message = request.Message.Trim(),
                category = request.Category,
                timestamp = DateTime.Now.ToString("hh:mm:ss tt"),
                date = DateTime.Now.ToString("dd MMM yyyy")
            });

            return Ok(new { success = true, recipientCount = activeInvestors.Count });
        }

        [HttpPost("reset-balance")]
        public IActionResult ResetCashBalance()
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                message = "Unrestricted wallet resets have been disabled. Virtual balances can only be adjusted by a platform Administrator with an auditable reason."
            });
        }

        [HttpPost("toggle-simulation")]
        public IActionResult ToggleSimulation()
        {
            int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
            string? role = HttpContext.Session.GetString("UserRole");

            if (!userId.HasValue)
            {
                return Unauthorized(new { success = false, message = "Authentication required. Please sign in as an administrator." });
            }

            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = "Access denied: Only administrators are authorized to configure market simulation." });
            }

            _marketDataProvider.IsSimulationMode = !_marketDataProvider.IsSimulationMode;
            _logger.LogInformation("Market simulation mode toggled to {Mode} by Admin User {UserId}.", _marketDataProvider.IsSimulationMode, userId.Value);
            return Ok(new { success = true, isSimulationActive = _marketDataProvider.IsSimulationMode });
        }

        [HttpGet("gmp/{ipoId}")]
        public async Task<IActionResult> GetGmpData(int ipoId)
        {
            var ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == ipoId);
            if (ipo is null)
            {
                return NotFound(new { success = false, message = "IPO not found." });
            }

            var history = await _context.IPOGMPHistories
                .AsNoTracking()
                .Where(g => g.IPO_id == ipoId)
                .OrderBy(g => g.RecordedDate)
                .ThenBy(g => g.Gmp_id)
                .ToListAsync();

            var latest = history.LastOrDefault();
            var previous = history.Count > 1 ? history[history.Count - 2] : latest;

            decimal basePrice = ipo.Price_max ?? ipo.Price_min ?? 0m;
            decimal currentGmp = latest?.GMPPrice ?? 0m;
            decimal prevGmp = previous?.GMPPrice ?? currentGmp;
            decimal estListingPrice = latest?.EstimatedListingPrice ?? (basePrice + currentGmp);
            decimal estGainPct = latest?.ExpectedListingGainPercentage ?? (basePrice > 0 ? Math.Round((currentGmp / basePrice) * 100m, 2) : 0m);

            return Ok(new
            {
                success = true,
                ipoId = ipo.IPO_id,
                ipoName = ipo.IPO_name,
                basePrice = basePrice,
                currentGmp = currentGmp,
                previousGmp = prevGmp,
                gmpChange = currentGmp - prevGmp,
                gmpChangePercentage = prevGmp > 0 ? Math.Round((currentGmp - prevGmp) / prevGmp * 100m, 2) : 0m,
                estimatedListingPrice = estListingPrice,
                estimatedGainPercentage = estGainPct,
                fireRating = latest?.FireRating ?? (estGainPct >= 20 ? "Bullish" : (estGainPct >= 10 ? "Moderate" : "Neutral")),
                isUnofficial = true,
                disclaimer = "Grey Market Premium (GMP) is purely unofficial, unregulated, and indicative peer-to-peer data. It is not recognized or monitored by SEBI, NSE, or BSE.",
                history = history.Select(g => new
                {
                    id = g.Gmp_id,
                    date = g.RecordedDate.ToString("dd MMM"),
                    fullDate = g.RecordedDate.ToString("dd MMM yyyy"),
                    gmp = g.GMPPrice,
                    estimatedPrice = g.EstimatedListingPrice ?? (basePrice + g.GMPPrice),
                    gainPercentage = g.ExpectedListingGainPercentage ?? (basePrice > 0 ? Math.Round((g.GMPPrice / basePrice) * 100m, 2) : 0m),
                    kostak = g.KostakRate,
                    subjectToSauda = g.SubjectToSauda,
                    fireRating = g.FireRating ?? "Neutral",
                    source = g.Source ?? "Unofficial Grey Market Desk"
                })
            });
        }
    }
}
