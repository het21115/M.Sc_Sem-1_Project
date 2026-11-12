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
            decimal currentPrice = isBse ? (data.BsePrice ?? data.CurrentPrice ?? 100m) : (data.NsePrice ?? data.CurrentPrice ?? 100m);
            decimal previousClose = isBse ? (data.BsePreviousClose ?? data.PreviousClose ?? currentPrice) : (data.NsePreviousClose ?? data.PreviousClose ?? currentPrice);
            decimal dayHigh = Math.Max(currentPrice, isBse ? (data.BseDayHigh ?? data.DayHigh ?? currentPrice) : (data.NseDayHigh ?? data.DayHigh ?? currentPrice));
            decimal dayLow = Math.Min(currentPrice, isBse ? (data.BseDayLow ?? data.DayLow ?? currentPrice) : (data.NseDayLow ?? data.DayLow ?? currentPrice));
            decimal priceChange = currentPrice - previousClose;
            decimal priceChangePct = previousClose > 0 ? (priceChange / previousClose * 100m) : 0m;
            decimal openPrice = Math.Round(previousClose * (1m + ((ipoId % 5 - 2) * 0.003m)), 2);

            var (points, candles) = GenerateChartData(timeframe, previousClose, currentPrice, dayHigh, dayLow, openPrice, ipoId);

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
                exchange = isBse ? "BSE" : "NSE",
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
                timeframe = timeframe.ToUpperInvariant(),
                points,
                candles
            });
        }

        private static (List<object> Points, List<object> Candles) GenerateChartData(
            string timeframe,
            decimal previousClose,
            decimal currentPrice,
            decimal dayHigh,
            decimal dayLow,
            decimal openPrice,
            int ipoId)
        {
            var points = new List<object>();
            var candles = new List<object>();
            string tf = string.IsNullOrWhiteSpace(timeframe) ? "1D" : timeframe.ToUpperInvariant();

            int count = tf switch
            {
                "1D" => 42,
                "1W" => 7,
                "1M" => 30,
                "3M" => 45,
                "6M" => 60,
                "1Y" => 52,
                "5Y" => 60,
                _ => 40
            };

            var rnd = new Random(ipoId * 101 + tf.GetHashCode());
            decimal startPrice = tf == "1D" ? openPrice : Math.Round(previousClose * (1m - (count * 0.0035m)), 2);
            if (startPrice <= 0m) startPrice = previousClose * 0.85m;

            decimal runningPrice = startPrice;

            for (int i = 0; i < count; i++)
            {
                double fraction = count <= 1 ? 1.0 : (double)i / (count - 1);
                decimal target = startPrice + (decimal)fraction * (currentPrice - startPrice);
                double wave = Math.Sin(fraction * Math.PI * 3.5) * (double)(dayHigh - dayLow) * 0.28;
                double noise = (rnd.NextDouble() - 0.48) * (double)(previousClose * 0.012m);

                decimal price = i == count - 1 ? currentPrice : Math.Round(target + (decimal)wave + (decimal)noise, 2);
                if (price < 0.05m) price = 0.05m;

                string label = tf switch
                {
                    "1D" => TimeSpan.FromMinutes(9 * 60 + 15 + i * (375.0 / count)).ToString(@"hh\:mm"),
                    "1W" => DateTime.Now.AddDays(-6 + i).ToString("ddd"),
                    "1M" => DateTime.Now.AddDays(-29 + i).ToString("dd MMM"),
                    "3M" => DateTime.Now.AddDays(-89 + i * 2.0).ToString("dd MMM"),
                    "6M" => DateTime.Now.AddDays(-179 + i * 3.0).ToString("MMM yy"),
                    "1Y" => DateTime.Now.AddDays(-364 + i * 7.0).ToString("MMM yy"),
                    "5Y" => DateTime.Now.AddMonths(-59 + i).ToString("MMM yyyy"),
                    _ => DateTime.Now.AddMonths(-count + i).ToString("MMM yyyy")
                };

                long volume = rnd.Next(1500, 9500);
                points.Add(new { label, price, volume });

                decimal cOpen = runningPrice;
                decimal cClose = price;
                decimal delta = Math.Abs(cClose - cOpen);
                decimal cHigh = Math.Max(cOpen, cClose) + Math.Round((decimal)(rnd.NextDouble() * 0.2) + delta * 0.1m, 2);
                decimal cLow = Math.Min(cOpen, cClose) - Math.Round((decimal)(rnd.NextDouble() * 0.2) + delta * 0.1m, 2);

                candles.Add(new
                {
                    label,
                    open = Math.Round(cOpen, 2),
                    high = Math.Round(cHigh, 2),
                    low = Math.Round(Math.Max(0.05m, cLow), 2),
                    close = Math.Round(cClose, 2),
                    volume
                });

                runningPrice = price;
            }

            return (points, candles);
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
            int? userId = HttpContext.Session.GetInt32("User_id");
            string? role = HttpContext.Session.GetString("UserRole");

            if (!userId.HasValue || !string.Equals(role, "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Unauthorized(new { success = false, message = "Please sign in to an investor account." });
            }

            if (request.Quantity <= 0)
            {
                return BadRequest(new { success = false, message = "Quantity must be greater than zero." });
            }

            string exchange = string.Equals(request.Exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";
            bool isBuy = string.Equals(request.TradeType, "BUY", StringComparison.OrdinalIgnoreCase);

            var marketData = await _stockMarketService.GetMarketDataAsync(request.IpoId);
            if (marketData == null || !marketData.IsMarketOpen || !marketData.CanTrade)
            {
                return BadRequest(new { success = false, message = "Market is currently closed. Trading is allowed strictly during official market hours (Monday – Friday, 09:30 AM to 03:30 PM IST)." });
            }

            decimal tradePrice = exchange == "BSE" ? (marketData.BsePrice ?? marketData.CurrentPrice ?? 0m) : (marketData.NsePrice ?? marketData.CurrentPrice ?? 0m);
            if (tradePrice <= 0m)
            {
                return BadRequest(new { success = false, message = "Invalid market price." });
            }

            decimal totalAmount = request.Quantity * tradePrice;
            User? investor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == userId.Value && u.Is_active);
            if (investor == null)
            {
                return NotFound(new { success = false, message = "Investor record not found." });
            }

            await using var transaction = await _context.Database.BeginTransactionAsync();

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
            string? role = HttpContext.Session.GetString("UserRole");
            if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
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
        public async Task<IActionResult> ResetCashBalance()
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Unauthorized(new { success = false, message = "Unauthorized" });
            }

            var user = await _context.Users.FindAsync(userId.Value);
            if (user == null)
            {
                return NotFound();
            }

            user.SimulatedCashBalance = 100000m;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, newBalance = user.SimulatedCashBalance });
        }

        [HttpPost("toggle-simulation")]
        public IActionResult ToggleSimulation()
        {
            _marketDataProvider.IsSimulationMode = !_marketDataProvider.IsSimulationMode;
            return Ok(new { isSimulationActive = _marketDataProvider.IsSimulationMode });
        }
    }
}
