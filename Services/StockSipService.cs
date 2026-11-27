using System.Collections.Concurrent;
using System.Data;
using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public class StockSipService : IStockSipService
    {
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _sipExecutionLocks = new();

        private readonly ApplicationDbContext _context;
        private readonly IStockMarketService _stockMarketService;
        private readonly IMarketTradingCalendar _calendar;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<StockSipService> _logger;

        public StockSipService(
            ApplicationDbContext context,
            IStockMarketService stockMarketService,
            IMarketTradingCalendar calendar,
            IHubContext<MarketHub> hubContext,
            ILogger<StockSipService> logger)
        {
            _context = context;
            _stockMarketService = stockMarketService;
            _calendar = calendar;
            _hubContext = hubContext;
            _logger = logger;
        }

        public async Task<List<StockSipPlan>> GetUserSipsAsync(int userId, CancellationToken cancellationToken = default)
        {
            var plans = await _context.StockSipPlans
                .Include(p => p.IPO)
                .Where(p => p.UserId == userId)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(cancellationToken);

            if (plans.Count == 0) return plans;

            var ipoIds = plans.Select(p => p.IPOId).Distinct().ToList();
            var companies = await _context.Companies.AsNoTracking()
                .Where(c => _context.IPOs.Where(i => ipoIds.Contains(i.IPO_id)).Select(i => i.Company_id).Contains(c.Company_id))
                .ToDictionaryAsync(c => c.Company_id, c => c.Company_name, cancellationToken);

            var latestQuotes = await _context.StockMarketData.AsNoTracking()
                .Where(m => ipoIds.Contains(m.IPOId))
                .ToListAsync(cancellationToken);

            foreach (var plan in plans)
            {
                if (plan.IPO != null && companies.TryGetValue(plan.IPO.Company_id, out var cName))
                {
                    plan.CompanyName = cName;
                }
                else
                {
                    plan.CompanyName = plan.StockSymbol;
                }

                var quote = latestQuotes.FirstOrDefault(q => q.IPOId == plan.IPOId && string.Equals(q.Exchange, plan.Exchange, StringComparison.OrdinalIgnoreCase))
                         ?? latestQuotes.FirstOrDefault(q => q.IPOId == plan.IPOId);

                plan.CurrentMarketPrice = quote?.CurrentPrice ?? plan.IPO?.Price_max ?? plan.IPO?.Price_min;
            }

            return plans;
        }

        public async Task<StockSipDashboardViewModel> GetSipDashboardAsync(int userId, CancellationToken cancellationToken = default)
        {
            var plans = await GetUserSipsAsync(userId, cancellationToken);
            var active = plans.Where(p => p.Status == "Active" || p.Status == "Paused").ToList();
            var past = plans.Where(p => p.Status == "Cancelled" || p.Status == "Completed").ToList();

            decimal monthlyCommitment = active.Where(p => p.Status == "Active")
                .Sum(p => p.Frequency switch
                {
                    "Daily" => p.InstallmentAmount * 22m, // approx 22 trading days per month
                    "Weekly" => p.InstallmentAmount * 4m,
                    _ => p.InstallmentAmount
                });

            return new StockSipDashboardViewModel
            {
                ActivePlans = active,
                PastPlans = past,
                TotalActiveSips = active.Count(p => p.Status == "Active"),
                TotalMonthlyCommitment = monthlyCommitment,
                TotalSipInvested = plans.Sum(p => p.TotalInvested),
                TotalInstallmentsExecuted = plans.Sum(p => p.InstallmentsCompleted),
                TotalSharesAccumulated = plans.Sum(p => p.TotalSharesAccumulated)
            };
        }

        public async Task<(bool Success, string Message, StockSipPlan? Plan)> CreateSipAsync(
            int userId,
            CreateStockSipRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.InstallmentAmount < 100m)
            {
                return (false, "Minimum SIP installment amount is ₹100.", null);
            }

            var ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == request.IpoId, cancellationToken);
            if (ipo is null)
            {
                return (false, "Stock or IPO security not found.", null);
            }

            var listing = await _context.IPOListings.AsNoTracking().FirstOrDefaultAsync(l => l.IPO_id == request.IpoId, cancellationToken);
            string symbol = listing?.StockSymbol ?? (ipo.IPO_name.Length <= 8 ? ipo.IPO_name.ToUpperInvariant() : ipo.IPO_name[..8].ToUpperInvariant());

            DateTime istNow = GetIndiaStandardTime();
            int day = Math.Clamp(request.InstallmentDay, 1, 28);
            string frequency = string.IsNullOrWhiteSpace(request.Frequency) ? "Monthly" : request.Frequency;
            decimal installmentAmount = Math.Round(request.InstallmentAmount, 2);

            DateTime? endDate = null;
            if (request.TotalMonths.HasValue && request.TotalMonths.Value > 0)
            {
                endDate = istNow.AddMonths(request.TotalMonths.Value);
            }

            string exchange = string.Equals(request.Exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";

            decimal price = await ResolveSipExecutionPriceAsync(request.IpoId, exchange, ipo, cancellationToken);
            if (price <= 0m)
            {
                return (false, $"Market price for {symbol} is currently unavailable. Cannot start SIP right now.", null);
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                : null;

            try
            {
                User? investor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == userId && u.Is_active, cancellationToken);
                if (investor is null || !string.Equals(investor.Role, "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return (false, "Active investor record not found.", null);
                }

                int shares = (int)Math.Floor(installmentAmount / price);
                if (shares < 1)
                {
                    if (installmentAmount >= price * 0.70m && investor.SimulatedCashBalance >= price)
                    {
                        shares = 1;
                    }
                    else
                    {
                        if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                        return (false, $"Current market price of {symbol} (₹{price:N2}) exceeds your SIP installment of ₹{installmentAmount:N2}. Increase your SIP installment to at least ₹{Math.Ceiling(price)}.", null);
                    }
                }

                decimal totalCost = Math.Round(shares * price, 2);
                if (investor.SimulatedCashBalance < totalCost)
                {
                    if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                    return (false, $"Insufficient virtual wallet balance to start this SIP. First installment requires ₹{totalCost:N2}, available ₹{investor.SimulatedCashBalance:N2}.", null);
                }

                decimal balanceBefore = investor.SimulatedCashBalance;
                investor.SimulatedCashBalance = balanceBefore - totalCost;
                if (investor.SimulatedCashBalance < 0m)
                {
                    throw new InvalidOperationException("Negative cash balance prevented.");
                }

                var investment = await _context.Investments.FirstOrDefaultAsync(
                    item => item.User_id == userId && item.IPO_id == request.IpoId && item.Status != "Closed", cancellationToken);

                if (investment is null)
                {
                    investment = new Investment
                    {
                        User_id = userId,
                        IPO_id = request.IpoId,
                        Quantity = shares,
                        Buy_price = price,
                        Current_price = price,
                        Buy_date = istNow,
                        Status = "Holding"
                    };
                    _context.Investments.Add(investment);
                    await _context.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    decimal existingCost = (investment.Buy_price ?? 0m) * investment.Quantity;
                    investment.Buy_price = Math.Round((existingCost + totalCost) / (investment.Quantity + shares), 2);
                    investment.Quantity += shares;
                    investment.Current_price = price;
                    investment.Status = "Holding";
                }

                _context.InvestmentTransactions.Add(new InvestmentTransaction
                {
                    Investment_id = investment.Investment_id,
                    Transaction_type = "SIP_BUY",
                    Quantity = shares,
                    Price = price,
                    Total_amount = totalCost,
                    Transaction_date = istNow
                });

                _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                {
                    UserId = userId,
                    AdminUserId = null,
                    TransactionType = "SIP_BUY",
                    Amount = -totalCost,
                    BalanceBefore = balanceBefore,
                    BalanceAfter = investor.SimulatedCashBalance,
                    Reason = $"Initial SIP installment on start: {shares} share(s) of {symbol} ({exchange}) @ ₹{price:N2}",
                    CreatedAt = istNow
                });

                DateTime nextDate = CalculateNextExecutionDate(istNow, frequency, day);
                string initialStatus = (endDate.HasValue && nextDate > endDate.Value) ? "Completed" : "Active";

                var plan = new StockSipPlan
                {
                    UserId = userId,
                    IPOId = request.IpoId,
                    StockSymbol = symbol,
                    Exchange = exchange,
                    Frequency = frequency,
                    InstallmentAmount = installmentAmount,
                    InstallmentDay = day,
                    StartDate = istNow,
                    EndDate = endDate,
                    NextExecutionDate = nextDate,
                    LastExecutedDate = istNow,
                    InstallmentsCompleted = 1,
                    TotalInvested = totalCost,
                    TotalSharesAccumulated = shares,
                    Status = initialStatus,
                    CreatedAt = istNow,
                    UpdatedAt = istNow
                };

                _context.StockSipPlans.Add(plan);

                _context.SystemNotifications.Add(new SystemNotification
                {
                    User_id = userId,
                    Title = $"Stock SIP Started: {symbol}",
                    Message = $"Your {plan.Frequency} SIP for {ipo.IPO_name} ({plan.Exchange}) has started. Initial installment debited ₹{totalCost:N2} for {shares} share(s) @ ₹{price:N2}. Remaining wallet balance: ₹{investor.SimulatedCashBalance:N2}. Next scheduled execution: {plan.NextExecutionDate:dd MMM yyyy}.",
                    Created_at = istNow,
                    Is_read = false
                });

                await _context.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Stock SIP started & first installment executed: User={UserId}, Stock={Symbol}, Shares={Shares}, Cost={Cost}, BalanceAfter={Balance}",
                    userId, symbol, shares, totalCost, investor.SimulatedCashBalance);

                return (true, $"Stock SIP for {symbol} started! Purchased {shares} share(s) @ ₹{price:N2} (Debited ₹{totalCost:N2}). Remaining wallet balance: ₹{investor.SimulatedCashBalance:N2}.", plan);
            }
            catch (Exception ex)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Failed to start SIP for User {UserId}, IPO {IpoId}. No funds deducted.", userId, request.IpoId);
                return (false, "Could not start Stock SIP right now. No funds were deducted.", null);
            }
        }

        private async Task<decimal> ResolveSipExecutionPriceAsync(int ipoId, string exchange, IPO? ipo, CancellationToken cancellationToken)
        {
            var marketData = await _stockMarketService.GetMarketDataAsync(ipoId, cancellationToken);
            decimal price = string.Equals(exchange, "BSE", StringComparison.OrdinalIgnoreCase)
                ? (marketData?.BsePrice ?? marketData?.CurrentPrice ?? 0m)
                : (marketData?.NsePrice ?? marketData?.CurrentPrice ?? 0m);

            if (price <= 0m)
            {
                var listing = await _context.IPOListings.AsNoTracking().FirstOrDefaultAsync(l => l.IPO_id == ipoId, cancellationToken);
                price = listing?.NSE_listing_price ?? listing?.BSE_listing_price ?? listing?.Issue_price ?? ipo?.Price_max ?? ipo?.Price_min ?? 0m;
            }

            return price;
        }

        public async Task<(bool Success, string Message)> PauseSipAsync(int userId, int sipId, CancellationToken cancellationToken = default)
        {
            var plan = await _context.StockSipPlans.FirstOrDefaultAsync(p => p.SipId == sipId && p.UserId == userId, cancellationToken);
            if (plan is null)
            {
                return (false, "SIP plan not found.");
            }

            if (string.Equals(plan.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Cancelled SIP plans cannot be paused.");
            }

            if (string.Equals(plan.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Completed SIP plans cannot be paused.");
            }

            if (!string.Equals(plan.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Cannot pause SIP plan in '{plan.Status}' status.");
            }

            DateTime istNow = GetIndiaStandardTime();
            plan.Status = "Paused";
            plan.UpdatedAt = istNow;
            await _context.SaveChangesAsync(cancellationToken);

            return (true, $"SIP for {plan.StockSymbol} has been paused.");
        }

        public async Task<(bool Success, string Message)> ResumeSipAsync(int userId, int sipId, CancellationToken cancellationToken = default)
        {
            var plan = await _context.StockSipPlans.FirstOrDefaultAsync(p => p.SipId == sipId && p.UserId == userId, cancellationToken);
            if (plan is null)
            {
                return (false, "SIP plan not found.");
            }

            if (string.Equals(plan.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Cancelled SIP plans cannot be resumed.");
            }

            if (string.Equals(plan.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Completed SIP plans cannot be resumed.");
            }

            if (!string.Equals(plan.Status, "Paused", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Cannot resume SIP plan in '{plan.Status}' status.");
            }

            DateTime istNow = GetIndiaStandardTime();
            plan.Status = "Active";
            plan.NextExecutionDate = CalculateFirstExecutionDate(istNow, plan.Frequency, plan.InstallmentDay);
            plan.UpdatedAt = istNow;

            await _context.SaveChangesAsync(cancellationToken);

            return (true, $"SIP for {plan.StockSymbol} resumed! Next installment scheduled on {plan.NextExecutionDate:dd MMM yyyy}.");
        }

        public async Task<(bool Success, string Message)> CancelSipAsync(int userId, int sipId, CancellationToken cancellationToken = default)
        {
            var plan = await _context.StockSipPlans.FirstOrDefaultAsync(p => p.SipId == sipId && p.UserId == userId, cancellationToken);
            if (plan is null)
            {
                return (false, "SIP plan not found.");
            }

            if (string.Equals(plan.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "SIP plan is already cancelled.");
            }

            DateTime istNow = GetIndiaStandardTime();
            plan.Status = "Cancelled";
            plan.UpdatedAt = istNow;
            await _context.SaveChangesAsync(cancellationToken);

            return (true, $"SIP for {plan.StockSymbol} has been cancelled.");
        }

        public async Task<(bool Success, string Message, int? SharesBought, decimal? ExecutedPrice)> ExecuteInstallmentAsync(
            int sipId,
            int? requestingUserId = null,
            bool isManualTrigger = true,
            CancellationToken cancellationToken = default)
        {
            var sipLock = _sipExecutionLocks.GetOrAdd(sipId, _ => new SemaphoreSlim(1, 1));
            await sipLock.WaitAsync(cancellationToken);
            try
            {
                DateTime istNow = GetIndiaStandardTime();

                // 1. Reload plan fresh from database
                var plan = await _context.StockSipPlans
                    .Include(p => p.IPO)
                    .Include(p => p.User)
                    .FirstOrDefaultAsync(p => p.SipId == sipId, cancellationToken);

                if (plan is null)
                {
                    return (false, "SIP plan not found.", null, null);
                }

                // 2. Ownership verification: manual execution must not allow another investor to execute someone else's SIP
                if (isManualTrigger)
                {
                    if (!requestingUserId.HasValue || plan.UserId != requestingUserId.Value)
                    {
                        _logger.LogWarning("Security violation: User {RequestingUserId} attempted to execute SIP {SipId} owned by User {OwnerId}",
                            requestingUserId, sipId, plan.UserId);
                        return (false, "Unauthorized: You cannot execute another investor's SIP plan.", null, null);
                    }
                }

                // 3. Status lifecycle enforcement
                // Rule: Cancelled SIP should never execute again
                if (string.Equals(plan.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "This SIP plan has been cancelled and cannot execute again.", null, null);
                }

                // Rule: Completed SIP should never execute again
                if (string.Equals(plan.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "This SIP plan has reached its completion tenure and cannot execute again.", null, null);
                }

                // Rule: Paused SIP should not execute automatically
                if (string.Equals(plan.Status, "Paused", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, isManualTrigger
                        ? "This SIP plan is currently paused. Please resume the SIP plan before executing installments."
                        : "Paused SIP plans do not execute automatically.", null, null);
                }

                // Rule: Must be Active
                if (!string.Equals(plan.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"SIP plan cannot execute in '{plan.Status}' status.", null, null);
                }

                // 4. Duplicate / double-run prevention (Automatic execution should not process the same installment twice)
                if (plan.LastExecutedDate.HasValue)
                {
                    DateTime lastExec = plan.LastExecutedDate.Value;
                    bool alreadyExecuted = false;
                    string duplicateReason = "";

                    if (string.Equals(plan.Frequency, "Daily", StringComparison.OrdinalIgnoreCase))
                    {
                        if (lastExec.Date == istNow.Date)
                        {
                            alreadyExecuted = true;
                            duplicateReason = "Today's daily installment has already been executed.";
                        }
                    }
                    else if (string.Equals(plan.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase))
                    {
                        if ((istNow.Date - lastExec.Date).TotalDays < 6)
                        {
                            alreadyExecuted = true;
                            duplicateReason = $"This week's installment has already been executed on {lastExec:dd MMM yyyy}.";
                        }
                    }
                    else // Monthly
                    {
                        if (lastExec.Year == istNow.Year && lastExec.Month == istNow.Month)
                        {
                            alreadyExecuted = true;
                            duplicateReason = $"The monthly installment for {lastExec:MMMM yyyy} has already been executed.";
                        }
                    }

                    if (alreadyExecuted)
                    {
                        if (!isManualTrigger && plan.NextExecutionDate <= istNow)
                        {
                            plan.NextExecutionDate = CalculateNextExecutionDate(istNow, plan.Frequency, plan.InstallmentDay);
                            plan.UpdatedAt = istNow;
                            await _context.SaveChangesAsync(cancellationToken);
                        }
                        return (false, duplicateReason, null, null);
                    }
                }

                if (!isManualTrigger && plan.NextExecutionDate > istNow)
                {
                    return (false, $"SIP installment is not due yet (Next scheduled: {plan.NextExecutionDate:dd MMM yyyy, hh:mm tt}).", null, null);
                }

                // 5. Market Open verification
                bool isMarketOpen = await _calendar.IsMarketOpenAsync(istNow, cancellationToken);
                if (!isMarketOpen)
                {
                    if (!isManualTrigger)
                    {
                        bool isTradingDay = await _calendar.IsTradingDayAsync(istNow, cancellationToken);
                        if (!isTradingDay)
                        {
                            DateTime nextTradingDay = istNow.Date.AddDays(1);
                            while (!await _calendar.IsTradingDayAsync(nextTradingDay, cancellationToken))
                            {
                                nextTradingDay = nextTradingDay.AddDays(1);
                            }
                            plan.NextExecutionDate = nextTradingDay.Add(new TimeSpan(10, 0, 0));
                            plan.UpdatedAt = istNow;
                            await _context.SaveChangesAsync(cancellationToken);
                        }
                        return (false, "Market is currently closed. Automated SIP execution deferred until market open.", null, null);
                    }
                    return (false, "Market is currently closed. SIP orders can only be executed during live market trading hours (09:30 AM - 03:30 PM IST on trading days).", null, null);
                }

                // 6. Investor record check
                var investor = plan.User ?? await _context.Users.FirstOrDefaultAsync(u => u.User_id == plan.UserId, cancellationToken);
                if (investor is null || !investor.Is_active)
                {
                    return (false, "Active investor record not found.", null, null);
                }

                // 7. Get reliable market price without synthetic defaults
                decimal price = await ResolveSipExecutionPriceAsync(plan.IPOId, plan.Exchange, plan.IPO, cancellationToken);

                if (price <= 0m)
                {
                    return (false, $"Market price for {plan.StockSymbol} is currently unavailable. Installment cannot be executed.", null, null);
                }

                int shares = (int)Math.Floor(plan.InstallmentAmount / price);
                if (shares < 1)
                {
                    if (plan.InstallmentAmount >= price * 0.70m && investor.SimulatedCashBalance >= price)
                    {
                        shares = 1;
                    }
                    else
                    {
                        return (false, $"Current market price of {plan.StockSymbol} (₹{price:N2}) exceeds your SIP budget of ₹{plan.InstallmentAmount:N2}. Increase your SIP installment to at least ₹{Math.Ceiling(price)}.", null, null);
                    }
                }

                decimal totalCost = Math.Round(shares * price, 2);

                // 8. Insufficient balance check (must NOT create a successful installment and must NOT deduct money)
                if (investor.SimulatedCashBalance < totalCost)
                {
                    _context.SystemNotifications.Add(new SystemNotification
                    {
                        User_id = plan.UserId,
                        Title = $"SIP Execution Failed: {plan.StockSymbol}",
                        Message = $"Your scheduled SIP installment of ₹{plan.InstallmentAmount:N2} for {plan.StockSymbol} could not execute due to insufficient virtual cash balance (Required: ₹{totalCost:N2}, Available: ₹{investor.SimulatedCashBalance:N2}). Please top up your virtual wallet.",
                        Created_at = istNow,
                        Is_read = false
                    });

                    // Advance NextExecutionDate if automated so it does not retry every minute
                    if (!isManualTrigger)
                    {
                        plan.NextExecutionDate = CalculateNextExecutionDate(istNow, plan.Frequency, plan.InstallmentDay);
                        plan.UpdatedAt = istNow;
                    }

                    await _context.SaveChangesAsync(cancellationToken);

                    return (false, $"Insufficient virtual cash balance. Required: ₹{totalCost:N2}, Available: ₹{investor.SimulatedCashBalance:N2}. No money was deducted.", null, null);
                }

                // 9. Atomic transaction with Serializable isolation
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                try
                {
                    // Strict guard inside isolation boundary
                    if (investor.SimulatedCashBalance < totalCost)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return (false, $"Insufficient virtual cash balance. Required: ₹{totalCost:N2}, Available: ₹{investor.SimulatedCashBalance:N2}. No money was deducted.", null, null);
                    }

                    decimal balanceBefore = investor.SimulatedCashBalance;
                    investor.SimulatedCashBalance -= totalCost;

                    if (investor.SimulatedCashBalance < 0m)
                    {
                        throw new InvalidOperationException("Negative cash balance prevented.");
                    }

                    var investment = await _context.Investments.FirstOrDefaultAsync(
                        item => item.User_id == plan.UserId && item.IPO_id == plan.IPOId && item.Status != "Closed", cancellationToken);

                    if (investment is null)
                    {
                        investment = new Investment
                        {
                            User_id = plan.UserId,
                            IPO_id = plan.IPOId,
                            Quantity = shares,
                            Buy_price = price,
                            Current_price = price,
                            Buy_date = istNow,
                            Status = "Holding"
                        };
                        _context.Investments.Add(investment);
                        await _context.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        decimal existingCost = (investment.Buy_price ?? 0m) * investment.Quantity;
                        investment.Buy_price = Math.Round((existingCost + totalCost) / (investment.Quantity + shares), 2);
                        investment.Quantity += shares;
                        investment.Current_price = price;
                        investment.Status = "Holding";
                    }

                    _context.InvestmentTransactions.Add(new InvestmentTransaction
                    {
                        Investment_id = investment.Investment_id,
                        Transaction_type = "SIP_BUY",
                        Quantity = shares,
                        Price = price,
                        Total_amount = totalCost,
                        Transaction_date = istNow
                    });

                    _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                    {
                        UserId = plan.UserId,
                        AdminUserId = null,
                        TransactionType = "SIP_BUY",
                        Amount = -totalCost,
                        BalanceBefore = balanceBefore,
                        BalanceAfter = investor.SimulatedCashBalance,
                        Reason = $"Automated SIP order: {shares} shares of {plan.StockSymbol} ({plan.Exchange}) @ ₹{price:N2}",
                        CreatedAt = istNow
                    });

                    plan.InstallmentsCompleted++;
                    plan.TotalInvested += totalCost;
                    plan.TotalSharesAccumulated += shares;
                    plan.LastExecutedDate = istNow;
                    plan.NextExecutionDate = CalculateNextExecutionDate(istNow, plan.Frequency, plan.InstallmentDay);
                    plan.UpdatedAt = istNow;

                    if (plan.EndDate.HasValue && plan.NextExecutionDate > plan.EndDate.Value)
                    {
                        plan.Status = "Completed";
                    }

                    _context.SystemNotifications.Add(new SystemNotification
                    {
                        User_id = plan.UserId,
                        Title = $"SIP Executed: {shares} shares of {plan.StockSymbol}",
                        Message = $"Your SIP order was executed successfully at ₹{price:N2}/share on {plan.Exchange}. Debited ₹{totalCost:N2}. Total accumulated: {plan.TotalSharesAccumulated} shares. Next installment: {plan.NextExecutionDate:dd MMM yyyy}.",
                        Created_at = istNow,
                        Is_read = false
                    });

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    try
                    {
                        string investorName = $"{investor.First_name} {investor.Last_name}".Trim();
                        await _hubContext.Clients.All.SendAsync("ReceiveTradeNotification", new
                        {
                            stockName = plan.IPO?.IPO_name ?? plan.StockSymbol,
                            exchange = plan.Exchange,
                            tradeType = "SIP_BUY",
                            quantity = shares,
                            price = price,
                            totalAmount = totalCost,
                            user = string.IsNullOrWhiteSpace(investorName) ? "Investor" : investorName,
                            timestamp = istNow.ToString("hh:mm:ss tt")
                        }, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast SignalR trade notification for SIP {SipId}", sipId);
                    }

                    return (true, $"SIP installment executed! Purchased {shares} share(s) of {plan.StockSymbol} at ₹{price:N2} (Total: ₹{totalCost:N2}).", shares, price);
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "Error executing SIP installment for SIP {SipId}. Rolled back; no funds deducted.", sipId);
                    return (false, "An error occurred while executing the SIP installment: " + ex.Message + ". No funds were deducted.", null, null);
                }
            }
            finally
            {
                sipLock.Release();
            }
        }

        public async Task ProcessDueSipInstallmentsAsync(CancellationToken cancellationToken = default)
        {
            DateTime istNow = GetIndiaStandardTime();

            // Check if market is open before processing automated installments
            bool isMarketOpen = await _calendar.IsMarketOpenAsync(istNow, cancellationToken);
            if (!isMarketOpen)
            {
                return;
            }

            // Find all active plans whose NextExecutionDate <= now
            var duePlans = await _context.StockSipPlans
                .Where(p => p.Status == "Active" && p.NextExecutionDate <= istNow)
                .Select(p => p.SipId)
                .ToListAsync(cancellationToken);

            if (duePlans.Count == 0) return;

            _logger.LogInformation("Processing {Count} due SIP installments...", duePlans.Count);

            foreach (var sipId in duePlans)
            {
                try
                {
                    await ExecuteInstallmentAsync(sipId, requestingUserId: null, isManualTrigger: false, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed automated execution for due SIP {SipId}", sipId);
                }
            }
        }

        private static DateTime CalculateFirstExecutionDate(DateTime fromDate, string frequency, int preferredDay)
        {
            DateTime target;
            if (string.Equals(frequency, "Daily", StringComparison.OrdinalIgnoreCase))
            {
                target = fromDate.Date.AddDays(1);
            }
            else if (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase))
            {
                target = fromDate.Date.AddDays(7);
            }
            else
            {
                // Monthly
                int year = fromDate.Year;
                int month = fromDate.Month;
                int day = Math.Min(preferredDay, DateTime.DaysInMonth(year, month));

                target = new DateTime(year, month, day);
                if (target <= fromDate.Date)
                {
                    // Move to next month
                    var nextMonth = fromDate.Date.AddMonths(1);
                    int daysInNextMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
                    target = new DateTime(nextMonth.Year, nextMonth.Month, Math.Min(preferredDay, daysInNextMonth));
                }
            }

            // If lands on Saturday or Sunday, advance to next Monday
            if (target.DayOfWeek == DayOfWeek.Saturday) target = target.AddDays(2);
            if (target.DayOfWeek == DayOfWeek.Sunday) target = target.AddDays(1);

            return target.Add(new TimeSpan(10, 0, 0)); // 10:00 AM IST on execution day
        }

        private static DateTime CalculateNextExecutionDate(DateTime fromDate, string frequency, int preferredDay)
        {
            DateTime target;
            if (string.Equals(frequency, "Daily", StringComparison.OrdinalIgnoreCase))
            {
                target = fromDate.Date.AddDays(1);
            }
            else if (string.Equals(frequency, "Weekly", StringComparison.OrdinalIgnoreCase))
            {
                target = fromDate.Date.AddDays(7);
            }
            else
            {
                // Monthly
                var nextMonth = fromDate.Date.AddMonths(1);
                int daysInNextMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
                target = new DateTime(nextMonth.Year, nextMonth.Month, Math.Min(preferredDay, daysInNextMonth));
            }

            // If lands on Saturday or Sunday, advance to next Monday
            if (target.DayOfWeek == DayOfWeek.Saturday) target = target.AddDays(2);
            if (target.DayOfWeek == DayOfWeek.Sunday) target = target.AddDays(1);

            return target.Add(new TimeSpan(10, 0, 0));
        }

        private static DateTime GetIndiaStandardTime()
        {
            return IpoClock.Shared.NowIst;
        }
    }
}
