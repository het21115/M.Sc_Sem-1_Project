using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Services
{
    public class IpoStatusService : IIpoStatusService
    {
        private readonly ApplicationDbContext _context;
        private readonly IIpoClock _clock;
        private readonly IConfiguration _configuration;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly ILogger<IpoStatusService> _logger;
        private readonly IEmailNotificationService _emailService;

        public IpoStatusService(
            ApplicationDbContext context,
            IIpoClock clock,
            IConfiguration configuration,
            IHubContext<MarketHub> hubContext,
            ILogger<IpoStatusService> logger,
            IEmailNotificationService emailService)
        {
            _context = context;
            _clock = clock;
            _configuration = configuration;
            _hubContext = hubContext;
            _logger = logger;
            _emailService = emailService;
        }

        public TimeSpan GetConfiguredOpeningTime()
        {
            string? openStr = _configuration["MarketTrading:IpoBiddingOpeningTime"] ?? _configuration["MarketTrading:OpeningTime"];
            return TimeSpan.TryParse(openStr, out var ot) ? ot : new TimeSpan(10, 0, 0);
        }

        public TimeSpan GetConfiguredClosingTime()
        {
            string? closeStr = _configuration["MarketTrading:IpoBiddingClosingTime"] ?? _configuration["MarketTrading:ClosingTime"];
            return TimeSpan.TryParse(closeStr, out var ct) ? ct : new TimeSpan(17, 0, 0);
        }

        public (bool IsOpen, string? Reason) IsBiddingWindowOpen(DateTime? istNow = null)
        {
            DateTime now = istNow ?? _clock.NowIst;

            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return (false, "IPO applications can only be submitted during the market period (Monday to Friday, 10:00 AM to 05:00 PM IST). Bidding is closed on weekends.");
            }

            try
            {
                if (_context.MarketTradingHolidays.Any(h => h.HolidayDate.Date == now.Date))
                {
                    return (false, "IPO applications are closed today due to an exchange market holiday.");
                }
            }
            catch
            {
                // Graceful fallback if database context is not queryable
            }

            TimeSpan startTime = new TimeSpan(10, 0, 0); // 10:00 AM IST
            TimeSpan endTime = new TimeSpan(17, 0, 0);   // 05:00 PM IST
            TimeSpan timeOfDay = now.TimeOfDay;

            if (timeOfDay < startTime)
            {
                return (false, "IPO bidding for today opens at 10:00 AM IST. Applications are accepted Monday to Friday between 10:00 AM and 05:00 PM IST.");
            }

            if (timeOfDay >= endTime)
            {
                return (false, "IPO bidding for today closed at 05:00 PM IST. Applications are accepted Monday to Friday between 10:00 AM and 05:00 PM IST.");
            }

            return (true, null);
        }

        public DateTime GetOpeningDateTime(IPO ipo)
        {
            TimeSpan time = ipo.Opening_time ?? 
                (ipo.Open_date.TimeOfDay != TimeSpan.Zero ? ipo.Open_date.TimeOfDay : GetConfiguredOpeningTime());
            return ipo.Open_date.Date.Add(time);
        }

        public DateTime GetClosingDateTime(IPO ipo)
        {
            TimeSpan time = ipo.Closing_time ?? 
                (ipo.Close_date.TimeOfDay != TimeSpan.Zero ? ipo.Close_date.TimeOfDay : GetConfiguredClosingTime());
            return ipo.Close_date.Date.Add(time);
        }

        public string CalculateStatus(IPO ipo, DateTime? istNow = null)
        {
            DateTime now = istNow ?? _clock.NowIst;

            // 1. Terminal / Non-reopening statuses
            if (string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                return "Rejected";
            }

            if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "Cancelled";
            }

            // 2. Unapproved IPOs must remain Pending until Admin approves and publishes
            if (string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                return "Pending";
            }

            // 3. Listed securities
            if (string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase))
            {
                return "Listed";
            }

            if (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= now.Date &&
                (string.Equals(ipo.Status, "Closed", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ipo.Status, "Allotted", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase)))
            {
                return "Listed";
            }

            // 4. Allotted securities must remain Allotted until listing date
            if (string.Equals(ipo.Status, "Allotted", StringComparison.OrdinalIgnoreCase) ||
                (ipo.Allotment_finalized && string.Equals(ipo.Status, "Closed", StringComparison.OrdinalIgnoreCase)))
            {
                return "Allotted";
            }

            // 5. Automatic time-based transitions for Approved / Published / Active IPOs
            DateTime openingDateTime = GetOpeningDateTime(ipo);
            DateTime closingDateTime = GetClosingDateTime(ipo);

            if (now < openingDateTime)
            {
                return "Upcoming";
            }

            if (now >= openingDateTime && now < closingDateTime)
            {
                return "Open";
            }

            // After closing date and time
            return "Closed";
        }

        public (bool CanApply, string? ErrorReason) CanApply(IPO ipo, DateTime? istNow = null)
        {
            DateTime now = istNow ?? _clock.NowIst;

            if (ipo is null)
            {
                return (false, "IPO not found.");
            }

            if (string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "This IPO has been rejected and is not accepting applications.");
            }

            if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "This IPO has been cancelled and is not accepting applications.");
            }

            if (string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "This IPO is awaiting administrator approval and is not yet open.");
            }

            if (string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "This IPO is already listed for secondary market trading. Primary applications are closed.");
            }

            if (string.Equals(ipo.Status, "Allotted", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Allotment has already been finalized for this IPO. Applications are closed.");
            }

            if (string.Equals(ipo.Status, "Upcoming", StringComparison.OrdinalIgnoreCase))
            {
                DateTime openDt = GetOpeningDateTime(ipo);
                return (false, $"Upcoming IPOs do not accept applications. Applications open on {openDt:dd MMM yyyy, hh:mm tt} IST.");
            }

            if (string.Equals(ipo.Status, "Closed", StringComparison.OrdinalIgnoreCase))
            {
                DateTime closeDt = GetClosingDateTime(ipo);
                return (false, $"Closed IPOs do not accept applications. Bidding closed on {closeDt:dd MMM yyyy, hh:mm tt} IST.");
            }

            DateTime openingDateTime = GetOpeningDateTime(ipo);
            DateTime closingDateTime = GetClosingDateTime(ipo);

            if (now < openingDateTime)
            {
                return (false, $"Upcoming IPOs do not accept applications. Applications open on {openingDateTime:dd MMM yyyy, hh:mm tt} IST.");
            }

            if (now >= closingDateTime)
            {
                return (false, $"Closed IPOs do not accept applications. The application window closed on {closingDateTime:dd MMM yyyy, hh:mm tt} IST.");
            }

            // Market Period Validation: Monday to Friday only
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return (false, "IPO applications are only accepted during the market period (Monday to Friday, 10:00 AM to 05:00 PM IST). Bidding is closed on weekends.");
            }

            // Exchange Trading Holiday Validation
            try
            {
                if (_context.MarketTradingHolidays.Any(h => h.HolidayDate.Date == now.Date))
                {
                    return (false, "IPO applications are closed today due to an exchange market holiday.");
                }
            }
            catch
            {
                // Fallback if context is in non-relational or mock mode
            }

            // Daily Bidding Hours Validation: 10:00 AM to 05:00 PM IST
            TimeSpan dailyBiddingStart = GetConfiguredOpeningTime();
            TimeSpan dailyBiddingEnd = GetConfiguredClosingTime();
            TimeSpan currentTime = now.TimeOfDay;

            if (currentTime < dailyBiddingStart)
            {
                return (false, $"IPO bidding for today opens at {dailyBiddingStart:hh\\:mm} AM IST. Applications are accepted Monday to Friday between 10:00 AM and 05:00 PM IST.");
            }

            if (currentTime >= dailyBiddingEnd)
            {
                if (now.Date >= ipo.Close_date.Date)
                {
                    return (false, $"Closed IPOs do not accept applications. The application window closed on {closingDateTime:dd MMM yyyy, hh:mm tt} IST.");
                }

                return (false, "IPO bidding for today closed at 05:00 PM IST. Applications are accepted Monday to Friday between 10:00 AM and 05:00 PM IST.");
            }

            return (true, null);
        }

        public async Task<IpoStatusSyncResult> SynchronizeIpoStatusesAsync(CancellationToken cancellationToken = default)
        {
            DateTime nowIst = _clock.NowIst;
            var result = new IpoStatusSyncResult
            {
                TimestampIst = nowIst
            };

            // Select candidate IPOs: Do not touch Pending, Rejected, Cancelled, or Listed
            var candidates = await _context.IPOs
                .Where(i => i.Status != "Rejected" &&
                            i.Status != "Cancelled" &&
                            i.Status != "Pending" &&
                            i.Status != "Listed")
                .ToListAsync(cancellationToken);

            result.TotalEvaluated = candidates.Count;
            var changedDetails = new List<IpoStatusChangeDetail>();

            foreach (var ipo in candidates)
            {
                // If already Allotted, only allow transition to Listed if listing date has arrived
                if (string.Equals(ipo.Status, "Allotted", StringComparison.OrdinalIgnoreCase) &&
                    !(ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= nowIst.Date))
                {
                    continue;
                }

                string currentStatus = ipo.Status ?? "Upcoming";
                string targetStatus = CalculateStatus(ipo, nowIst);

                if (!string.Equals(currentStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
                {
                    ipo.Status = targetStatus;

                    DateTime openDt = GetOpeningDateTime(ipo);
                    DateTime closeDt = GetClosingDateTime(ipo);

                    changedDetails.Add(new IpoStatusChangeDetail
                    {
                        IpoId = ipo.IPO_id,
                        IpoName = ipo.IPO_name,
                        PreviousStatus = currentStatus,
                        NewStatus = targetStatus,
                        OpeningDateTime = openDt,
                        ClosingDateTime = closeDt
                    });

                    // Log audit trail
                    _context.AuditLogs.Add(new AuditLog
                    {
                        ActorUserId = null, // System automated process
                        Action = "AutoStatusTransition",
                        EntityType = "IPO",
                        EntityId = ipo.IPO_id.ToString(),
                        Details = $"Automatic status transition: IPO #{ipo.IPO_id} ('{ipo.IPO_name}') updated from '{currentStatus}' to '{targetStatus}' at {nowIst:dd MMM yyyy, hh:mm:ss tt} IST. (Open: {openDt:dd MMM yyyy hh:mm tt}, Close: {closeDt:dd MMM yyyy hh:mm tt}).",
                        IpAddress = "127.0.0.1",
                        CreatedAt = nowIst
                    });

                    _logger.LogInformation(
                        "Automatic IPO status transition: IPO #{IpoId} ('{IpoName}') changed from {OldStatus} to {NewStatus}. IST Time: {IstTime:O}",
                        ipo.IPO_id, ipo.IPO_name, currentStatus, targetStatus, nowIst);
                }
            }

            if (changedDetails.Count > 0)
            {
                result.TotalUpdated = changedDetails.Count;
                result.Changes = changedDetails;

                await _context.SaveChangesAsync(cancellationToken);

                // Broadcast live status changes and dispatch notifications to investors
                try
                {
                    foreach (var change in changedDetails)
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveIpoStatusChange", new
                        {
                            ipoId = change.IpoId,
                            ipoName = change.IpoName,
                            previousStatus = change.PreviousStatus,
                            status = change.NewStatus,
                            openingDateTime = change.OpeningDateTime.ToString("dd MMM yyyy, hh:mm tt"),
                            closingDateTime = change.ClosingDateTime.ToString("dd MMM yyyy, hh:mm tt"),
                            timestamp = nowIst.ToString("hh:mm:ss tt"),
                            istNow = nowIst.ToString("dd MMM yyyy, hh:mm:ss tt")
                        }, cancellationToken);

                        var ipo = candidates.FirstOrDefault(c => c.IPO_id == change.IpoId);
                        if (ipo != null)
                        {
                            await DispatchInvestorNotificationsAsync(ipo, change.PreviousStatus, change.NewStatus, change.OpeningDateTime, change.ClosingDateTime, cancellationToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to broadcast IPO status change via SignalR.");
                }
            }

            return result;
        }

        public async Task<bool> SynchronizeSingleIpoStatusAsync(int ipoId, CancellationToken cancellationToken = default)
        {
            var ipo = await _context.IPOs.FindAsync(new object[] { ipoId }, cancellationToken);
            if (ipo is null) return false;

            DateTime nowIst = _clock.NowIst;
            string currentStatus = ipo.Status ?? "Upcoming";
            string targetStatus = CalculateStatus(ipo, nowIst);

            if (!string.Equals(currentStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
            {
                ipo.Status = targetStatus;
                DateTime openDt = GetOpeningDateTime(ipo);
                DateTime closeDt = GetClosingDateTime(ipo);

                _context.AuditLogs.Add(new AuditLog
                {
                    ActorUserId = null,
                    Action = "AutoStatusTransition",
                    EntityType = "IPO",
                    EntityId = ipo.IPO_id.ToString(),
                    Details = $"Automatic status transition: IPO #{ipo.IPO_id} ('{ipo.IPO_name}') updated from '{currentStatus}' to '{targetStatus}' at {nowIst:dd MMM yyyy, hh:mm:ss tt} IST.",
                    IpAddress = "127.0.0.1",
                    CreatedAt = nowIst
                });

                await _context.SaveChangesAsync(cancellationToken);

                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveIpoStatusChange", new
                    {
                        ipoId = ipo.IPO_id,
                        ipoName = ipo.IPO_name,
                        previousStatus = currentStatus,
                        status = targetStatus,
                        openingDateTime = openDt.ToString("dd MMM yyyy, hh:mm tt"),
                        closingDateTime = closeDt.ToString("dd MMM yyyy, hh:mm tt"),
                        timestamp = nowIst.ToString("hh:mm:ss tt"),
                        istNow = nowIst.ToString("dd MMM yyyy, hh:mm:ss tt")
                    }, cancellationToken);

                    await DispatchInvestorNotificationsAsync(ipo, currentStatus, targetStatus, openDt, closeDt, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to broadcast single IPO status change via SignalR.");
                }

                return true;
            }

            return false;
        }

        private async Task DispatchInvestorNotificationsAsync(
            IPO ipo,
            string previousStatus,
            string newStatus,
            DateTime openDt,
            DateTime closeDt,
            CancellationToken cancellationToken)
        {
            try
            {
                var activeInvestors = await _context.Users
                    .AsNoTracking()
                    .Where(u => u.Role == "Investor" && u.Is_active)
                    .ToListAsync(cancellationToken);

                if (activeInvestors.Count == 0) return;

                string title = string.Empty;
                string message = string.Empty;
                string emailSubject = string.Empty;
                string emailBody = string.Empty;
                string broadcastCategory = string.Empty;

                DateTime nowIst = _clock.NowIst;

                if (string.Equals(newStatus, "Open", StringComparison.OrdinalIgnoreCase))
                {
                    title = $"IPO Bidding Open: {ipo.IPO_name}";
                    message = $"Bidding is now OPEN for {ipo.IPO_name} (Price Band: ₹{ipo.Price_min:N2} - ₹{ipo.Price_max:N2}, Lot size: {ipo.Lot_size} shares). The bidding window closes on {closeDt:dd MMM yyyy, hh:mm tt} IST. Submit your bid now.";
                    emailSubject = $"IPO Bidding Open: {ipo.IPO_name}";
                    emailBody = $"Dear Investor,\n\nBidding has officially opened for {ipo.IPO_name}.\n\nOffering Details:\n- Price Band: ₹{ipo.Price_min:N2} to ₹{ipo.Price_max:N2}\n- Lot Size: {ipo.Lot_size} shares\n- Bidding Closes: {closeDt:dd MMM yyyy, hh:mm tt} IST\n- Allotment Date: {(ipo.Allotment_date.HasValue ? ipo.Allotment_date.Value.ToString("dd MMM yyyy") : "To be announced")}\n- Listing Date: {(ipo.Listing_date.HasValue ? ipo.Listing_date.Value.ToString("dd MMM yyyy") : "To be announced")}\n\nLog in to your Investor Dashboard to submit your application before the closing deadline.\n\nBest regards,\nIPO Investment Management";
                    broadcastCategory = "IPO OPENING";
                }
                else if (string.Equals(newStatus, "Closed", StringComparison.OrdinalIgnoreCase))
                {
                    title = $"IPO Bidding Closed: {ipo.IPO_name}";
                    string allotmentStr = ipo.Allotment_date.HasValue ? ipo.Allotment_date.Value.ToString("dd MMM yyyy") : "announced soon";
                    message = $"The bidding window for {ipo.IPO_name} is now CLOSED. Allotment finalization is scheduled for {allotmentStr}. Track your application status on your dashboard.";
                    emailSubject = $"IPO Bidding Closed: {ipo.IPO_name}";
                    emailBody = $"Dear Investor,\n\nThe bidding window for {ipo.IPO_name} is now closed.\n\nKey Milestones:\n- Allotment Finalization: {allotmentStr}\n- Funds Unblock / Refund Date: {(ipo.Funds_unblock_date.HasValue ? ipo.Funds_unblock_date.Value.ToString("dd MMM yyyy") : "Follows allotment")}\n- Expected Listing Date: {(ipo.Listing_date.HasValue ? ipo.Listing_date.Value.ToString("dd MMM yyyy") : "To be announced")}\n\nYou can track the progress of your application and allotment results from your Investor Dashboard.\n\nBest regards,\nIPO Investment Management";
                    broadcastCategory = "IPO CLOSING";
                }
                else if (string.Equals(newStatus, "Listed", StringComparison.OrdinalIgnoreCase))
                {
                    title = $"IPO Listed: {ipo.IPO_name}";
                    message = $"{ipo.IPO_name} is now officially listed on NSE & BSE! Live secondary market trading is active. Real-time prices can be viewed in the Live Market terminal.";
                    emailSubject = $"IPO Listed on NSE & BSE: {ipo.IPO_name}";
                    emailBody = $"Dear Investor,\n\n{ipo.IPO_name} is now officially listed on the National Stock Exchange (NSE) and Bombay Stock Exchange (BSE).\n\nSecondary market trading has commenced. You can track real-time price feeds, place simulated buy/sell orders, and monitor your portfolio gains directly in the Live Market Terminal.\n\nBest regards,\nIPO Investment Management";
                    broadcastCategory = "IPO LISTED";
                }

                if (string.IsNullOrEmpty(title)) return;

                // Deduplication check: verify notification hasn't been added already
                bool alreadyDispatched = await _context.SystemNotifications
                    .AnyAsync(n => n.Title == title, cancellationToken);

                if (!alreadyDispatched)
                {
                    var notifications = activeInvestors.Select(investor => new SystemNotification
                    {
                        User_id = investor.User_id,
                        Title = title,
                        Message = message,
                        Created_at = nowIst,
                        Is_read = false
                    }).ToList();

                    _context.SystemNotifications.AddRange(notifications);
                    await _context.SaveChangesAsync(cancellationToken);

                    foreach (var investor in activeInvestors)
                    {
                        if (!string.IsNullOrWhiteSpace(investor.Email))
                        {
                            try
                            {
                                await _emailService.SendAsync(
                                    investor.Email,
                                    $"{investor.First_name} {investor.Last_name}".Trim(),
                                    emailSubject,
                                    emailBody);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to send status change email to {Email}", investor.Email);
                            }
                        }
                    }
                }

                // Send live SignalR broadcast toast
                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                    {
                        category = broadcastCategory,
                        title = title,
                        message = message,
                        timestamp = nowIst.ToString("hh:mm tt")
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send SignalR broadcast toast for {Title}", title);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dispatching investor notifications for IPO #{IpoId} transition to {Status}", ipo.IPO_id, newStatus);
            }
        }
    }
}
