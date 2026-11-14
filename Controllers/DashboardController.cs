using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using IPOInvestmentManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Data;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace IPOInvestmentManagement.Controllers
{
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailNotificationService _emailNotificationService;
        private readonly IRazorpayPaymentService _razorpayPaymentService;
        private readonly IStockMarketService _stockMarketService;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly IInvestorReportService _investorReportService;

        public DashboardController(
            ApplicationDbContext context,
            IEmailNotificationService emailNotificationService,
            IRazorpayPaymentService razorpayPaymentService,
            IStockMarketService stockMarketService,
            IHubContext<MarketHub> hubContext,
            IInvestorReportService investorReportService)
        {
            _context = context;
            _emailNotificationService = emailNotificationService;
            _razorpayPaymentService = razorpayPaymentService;
            _stockMarketService = stockMarketService;
            _hubContext = hubContext;
            _investorReportService = investorReportService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            string? marketStatus,
            string? marketIpoType,
            string? marketIpoName,
            string? marketCompany,
            int? calendarMonth,
            int? calendarYear,
            int[]? compareIpoIds,
            string? adminIpoQuery = null,
            string? adminIpoStatus = null,
            string? adminIpoType = null)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            bool isActive = await _context.Users
                .AsNoTracking()
                .AnyAsync(user => user.User_id == userId.Value && user.Is_active);
            if (!isActive)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            string role = HttpContext.Session.GetString("UserRole") ?? "Investor";
            var model = new DashboardViewModel
            {
                UserName = HttpContext.Session.GetString("UserName") ?? "User",
                Role = role
            };

            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                await LoadAdminDashboardAsync(model, adminIpoQuery, adminIpoStatus, adminIpoType);
                return View("Admin", model);
            }

            if (string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase))
            {
                await LoadCompanyDashboardAsync(model, userId.Value);
                return View("Company", model);
            }

            await LoadInvestorDashboardAsync(model, userId.Value, marketStatus, marketIpoType, marketIpoName, marketCompany, calendarMonth, calendarYear, compareIpoIds);
            return View("Investor", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyIpo(IpoApplicationRequestViewModel request)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            User? investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.User_id == userId.Value && user.Is_active);
            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == request.IpoId);
            DateTime indiaNow = GetIndiaStandardTime();
            DateTime today = indiaNow.Date;
            if (ipo is null ||
                string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                (!string.Equals(ipo.Status, "Approved", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(ipo.Status, "Published", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(ipo.Status, "Open", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(ipo.Status, "Closed", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase)) ||
                ipo.Open_date.Date > today || ipo.Close_date.Date < today)
            {
                ModelState.AddModelError(string.Empty, "This IPO is not currently open for applications.");
            }

            TimeSpan marketOpen = new(9, 30, 0);
            TimeSpan marketClose = new(15, 30, 0);
            if (indiaNow.TimeOfDay < marketOpen || indiaNow.TimeOfDay >= marketClose)
            {
                ModelState.AddModelError(string.Empty, "IPO applications are accepted only during market hours, 9:30 AM to 3:30 PM IST.");
            }

            if (request.AppliedLots <= 0 || ipo is null || !ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0)
            {
                ModelState.AddModelError(nameof(request.AppliedLots), "Number of lots must be at least one.");
            }

            if (!request.AcceptTerms)
            {
                ModelState.AddModelError(nameof(request.AcceptTerms), "Accept the Terms and Conditions before applying.");
            }

            if (investor is null)
            {
                ModelState.AddModelError(string.Empty, "Your investor account could not be found.");
            }

            decimal minimumPrice = ipo?.Price_min ?? 0m;
            decimal maximumPrice = ipo?.Price_max ?? minimumPrice;
            if (ipo is not null && (request.BidPrice < minimumPrice || request.BidPrice > maximumPrice))
            {
                ModelState.AddModelError(nameof(request.BidPrice), $"Bid price must be between ₹{minimumPrice:N2} and ₹{maximumPrice:N2}.");
            }

            bool alreadyApplied = ipo is not null && await _context.IPOApplications.AnyAsync(application =>
                application.User_id == userId.Value &&
                application.IPO_id == request.IpoId &&
                application.Status != "Rejected" &&
                application.Status != "Cancelled");
            if (alreadyApplied)
            {
                ModelState.AddModelError(string.Empty, "You have already submitted an active application for this IPO.");
            }

            if (!ModelState.IsValid || ipo is null || investor is null || ipo.Lot_size is null)
            {
                TempData["InvestorMessage"] = string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));
                return RedirectToAction(nameof(Index));
            }

            int appliedShares = checked(request.AppliedLots * ipo.Lot_size.Value);
            decimal applicationAmount = appliedShares * request.BidPrice;
            var pendingApplication = new IpoPaymentViewModel
            {
                ApplicationNumber = $"IPO-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant()[..28],
                InvestorName = $"{investor.First_name} {investor.Last_name}".Trim(),
                IpoName = ipo.IPO_name,
                BidPrice = request.BidPrice,
                AppliedLots = request.AppliedLots,
                AppliedShares = appliedShares,
                ApplicationAmount = applicationAmount,
                DematAccountNumber = investor.DematAccountNumber ?? string.Empty,
                UpiId = investor.UpiId ?? string.Empty
            };
            HttpContext.Session.SetString("PendingIpoApplication", JsonSerializer.Serialize(new
            {
                UserId = userId.Value,
                IpoId = request.IpoId,
                Application = pendingApplication
            }));
            return RedirectToAction(nameof(IpoPayment));
        }

        [HttpGet]
        public async Task<IActionResult> IpoPayment()
        {
            if (HttpContext.Session.GetInt32("User_id") is null ||
                !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            string? serialized = HttpContext.Session.GetString("PendingIpoApplication");
            if (string.IsNullOrWhiteSpace(serialized))
            {
                TempData["InvestorMessage"] = "Your application session has expired. Please start again.";
                return RedirectToAction(nameof(Index));
            }

            using JsonDocument document = JsonDocument.Parse(serialized);
            IpoPaymentViewModel? application = document.RootElement.GetProperty("Application").Deserialize<IpoPaymentViewModel>();
            if (application is null)
            {
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(application.RazorpayOrderId))
            {
                try
                {
                    application.RazorpayOrderId = await _razorpayPaymentService.CreateOrderAsync(
                        application.ApplicationAmount,
                        $"{application.ApplicationNumber}-{Guid.NewGuid():N}"[..40]);
                    application.RazorpayKeyId = _razorpayPaymentService.KeyId;
                    DateTime now = DateTime.UtcNow;
                    _context.PaymentRecords.Add(new PaymentRecord
                    {
                        UserId = document.RootElement.GetProperty("UserId").GetInt32(),
                        IpoId = document.RootElement.GetProperty("IpoId").GetInt32(),
                        AppliedLots = application.AppliedLots,
                        AppliedShares = application.AppliedShares,
                        BidPrice = application.BidPrice,
                        ApplicationNumber = application.ApplicationNumber,
                        RazorpayOrderId = application.RazorpayOrderId,
                        Amount = application.ApplicationAmount,
                        Status = "Created",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    await _context.SaveChangesAsync();
                    HttpContext.Session.SetString("PendingIpoApplication", JsonSerializer.Serialize(new
                    {
                        UserId = document.RootElement.GetProperty("UserId").GetInt32(),
                        IpoId = document.RootElement.GetProperty("IpoId").GetInt32(),
                        Application = application
                    }));
                }
                catch (InvalidOperationException exception)
                {
                    TempData["InvestorMessage"] = exception.Message;
                    return RedirectToAction(nameof(Index));
                }
            }

            return View(application);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmIpoPayment(
            string razorpayPaymentId,
            string razorpayOrderId,
            string razorpaySignature,
            string upiId)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            string? serialized = HttpContext.Session.GetString("PendingIpoApplication");
            if (!userId.HasValue || string.IsNullOrWhiteSpace(serialized))
            {
                return RedirectToAction(nameof(Index));
            }

            using JsonDocument document = JsonDocument.Parse(serialized);
            int pendingUserId = document.RootElement.GetProperty("UserId").GetInt32();
            int ipoId = document.RootElement.GetProperty("IpoId").GetInt32();
            IpoPaymentViewModel? pending = document.RootElement.GetProperty("Application").Deserialize<IpoPaymentViewModel>();
            if (pendingUserId != userId.Value || pending is null)
            {
                return Forbid();
            }
            if (string.IsNullOrWhiteSpace(upiId) ||
                !Regex.IsMatch(upiId.Trim(), @"^[^@\s]+@[^@\s]+$"))
            {
                TempData["InvestorMessage"] = "Please enter a valid UPI ID before confirming payment.";
                return RedirectToAction(nameof(IpoPayment));
            }
            pending.UpiId = upiId.Trim();
            bool signatureValid = false;
            try
            {
                signatureValid = !string.IsNullOrWhiteSpace(razorpayPaymentId) &&
                    !string.IsNullOrWhiteSpace(razorpayOrderId) &&
                    !string.IsNullOrWhiteSpace(razorpaySignature) &&
                    string.Equals(pending.RazorpayOrderId, razorpayOrderId, StringComparison.Ordinal) &&
                    _razorpayPaymentService.VerifyPaymentSignature(razorpayOrderId, razorpayPaymentId, razorpaySignature);
            }
            catch (InvalidOperationException exception)
            {
                TempData["InvestorMessage"] = exception.Message;
                return RedirectToAction(nameof(Index));
            }
            catch (FormatException)
            {
                signatureValid = false;
            }

            if (!signatureValid ||
                string.IsNullOrWhiteSpace(pending.RazorpayOrderId))
            {
                TempData["InvestorMessage"] = "Payment verification failed. The IPO application was not submitted.";
                return RedirectToAction(nameof(IpoPayment));
            }

            RazorpayPaymentDetails payment;
            try
            {
                payment = await _razorpayPaymentService.GetPaymentAsync(razorpayPaymentId);
            }
            catch (InvalidOperationException exception)
            {
                TempData["InvestorMessage"] = exception.Message;
                return RedirectToAction(nameof(IpoPayment));
            }

            if (!string.Equals(payment.OrderId, razorpayOrderId, StringComparison.Ordinal) ||
                payment.Amount != pending.ApplicationAmount ||
                !string.Equals(payment.Status, "captured", StringComparison.OrdinalIgnoreCase))
            {
                TempData["InvestorMessage"] = "Payment was not captured for the expected amount. The IPO application was not submitted.";
                return RedirectToAction(nameof(IpoPayment));
            }

            PaymentRecord? paymentRecord = await _context.PaymentRecords
                .FirstOrDefaultAsync(record => record.RazorpayOrderId == razorpayOrderId && record.UserId == userId.Value);
            if (paymentRecord is null)
            {
                TempData["InvestorMessage"] = "The payment order could not be matched to your application.";
                return RedirectToAction(nameof(IpoPayment));
            }

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == ipoId);
            DateTime today = GetIndiaStandardTime().Date;
            if (ipo is null ||
                ipo.Open_date.Date > today ||
                ipo.Close_date.Date < today ||
                string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.Remove("PendingIpoApplication");
                TempData["InvestorMessage"] = "This IPO is no longer open for applications.";
                return RedirectToAction(nameof(Index));
            }

            bool alreadyApplied = await _context.IPOApplications.AnyAsync(application =>
                application.User_id == userId.Value && application.IPO_id == ipoId &&
                application.Status != "Rejected" && application.Status != "Cancelled");
            bool paymentAlreadyConfirmed = await _context.IPOApplications.AnyAsync(application =>
                application.Razorpay_payment_id == razorpayPaymentId ||
                application.Razorpay_order_id == razorpayOrderId);
            if (paymentAlreadyConfirmed)
            {
                HttpContext.Session.Remove("PendingIpoApplication");
                TempData["InvestorMessage"] = $"Application {pending.ApplicationNumber} was already confirmed.";
                return RedirectToAction(nameof(Index));
            }
            if (alreadyApplied)
            {
                HttpContext.Session.Remove("PendingIpoApplication");
                TempData["InvestorMessage"] = "You have already submitted an active application for this IPO.";
                return RedirectToAction(nameof(Index));
            }

            paymentRecord.RazorpayPaymentId = payment.PaymentId;
            paymentRecord.Amount = payment.Amount;
            paymentRecord.Status = "Captured";
            paymentRecord.UpdatedAt = DateTime.UtcNow;
            _context.IPOApplications.Add(new IPOApplication
            {
                User_id = userId.Value,
                IPO_id = ipoId,
                Application_number = pending.ApplicationNumber,
                Applied_lots = pending.AppliedLots,
                Applied_shares = pending.AppliedShares,
                Bid_price = pending.BidPrice,
                Application_amount = pending.ApplicationAmount,
                Demat_account_number = pending.DematAccountNumber,
                Upi_id = pending.UpiId,
                Payment_reference = razorpayPaymentId,
                Razorpay_order_id = payment.OrderId,
                Razorpay_payment_id = payment.PaymentId,
                Payment_amount = payment.Amount,
                Payment_status = "Verified",
                Application_date = DateTime.UtcNow,
                Status = "Submitted"
            });
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            HttpContext.Session.Remove("PendingIpoApplication");
            TempData["InvestorMessage"] = $"Application {pending.ApplicationNumber} submitted successfully.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> InvestorApplicationDetails(int applicationId)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Application_id == applicationId && item.User_id == userId.Value);
            if (application is null)
            {
                return NotFound();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == application.IPO_id);
            if (ipo is null)
            {
                return NotFound();
            }

            return View(new AdminApplicationDetailsViewModel
            {
                Application = application,
                Investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.User_id == userId.Value),
                Ipo = ipo,
                Allotment = await _context.IPOAllotments.AsNoTracking()
                    .FirstOrDefaultAsync(allotment => allotment.Application_id == application.Application_id),
                Payment = await _context.PaymentRecords.AsNoTracking()
                    .FirstOrDefaultAsync(payment => payment.RazorpayOrderId == application.Razorpay_order_id)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelInvestorApplication(ApplicationCancellationViewModel model)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            if (!ModelState.IsValid)
            {
                TempData["InvestorMessage"] = "Enter a cancellation reason of at least 5 characters.";
                return RedirectToAction(nameof(InvestorApplicationDetails), new { applicationId = model.ApplicationId });
            }

            IPOApplication? application = await _context.IPOApplications
                .FirstOrDefaultAsync(item => item.Application_id == model.ApplicationId && item.User_id == userId.Value);
            IPO? ipo = application is null
                ? null
                : await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == application.IPO_id);
            bool alreadyAllotted = application is not null &&
                await _context.IPOAllotments.AnyAsync(allotment => allotment.Application_id == application.Application_id);
            DateTime cancellationDate = GetIndiaStandardTime();
            if (application is null || ipo is null)
            {
                return NotFound();
            }

            if (alreadyAllotted ||
                cancellationDate.Date >= ipo.Close_date.Date ||
                !string.Equals(application.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
            {
                TempData["InvestorMessage"] = "Applications can only be cancelled before the IPO closes and before allotment.";
                return RedirectToAction(nameof(InvestorApplicationDetails), new { applicationId = model.ApplicationId });
            }

            application.Status = "Cancelled";
            application.Cancellation_admin_id = null;
            application.Cancellation_reason = model.Reason.Trim();
            application.Cancellation_date = cancellationDate;
            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = userId.Value,
                Title = "IPO application cancelled",
                Message = $"Your IPO application #{application.Application_id} was cancelled. Reason: {application.Cancellation_reason}",
                Created_at = cancellationDate,
                Is_read = false
            });
            await _context.SaveChangesAsync();
            TempData["InvestorMessage"] = $"Application {application.Application_number ?? application.Application_id.ToString()} was cancelled.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryPayment(int paymentRecordId)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            PaymentRecord? record = await _context.PaymentRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.PaymentRecordId == paymentRecordId && item.UserId == userId.Value);
            IPOApplication? application = record is null ? null : await _context.IPOApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Razorpay_order_id == record.RazorpayOrderId);
            IPO? ipo = record is null ? null : await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == record.IpoId);
            User? investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.User_id == userId.Value && item.Is_active);
            if (record is null || application is not null || ipo is null || investor is null ||
                string.Equals(record.Status, "Captured", StringComparison.OrdinalIgnoreCase))
            {
                TempData["InvestorMessage"] = "This payment cannot be retried.";
                return RedirectToAction(nameof(Index));
            }

            HttpContext.Session.SetString("PendingIpoApplication", JsonSerializer.Serialize(new
            {
                UserId = userId.Value,
                IpoId = record.IpoId,
                Application = new IpoPaymentViewModel
                {
                    ApplicationNumber = record.ApplicationNumber,
                    InvestorName = $"{investor.First_name} {investor.Last_name}".Trim(),
                    IpoName = ipo.IPO_name,
                    BidPrice = record.BidPrice,
                    AppliedLots = record.AppliedLots,
                    AppliedShares = record.AppliedShares,
                    ApplicationAmount = record.Amount,
                    DematAccountNumber = investor.DematAccountNumber ?? string.Empty,
                    UpiId = investor.UpiId ?? string.Empty
                }
            }));
            return RedirectToAction(nameof(IpoPayment));
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> RazorpayWebhook()
        {
            string payload = await new StreamReader(Request.Body).ReadToEndAsync();
            string? signature = Request.Headers["X-Razorpay-Signature"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(signature) || !_razorpayPaymentService.VerifyWebhookSignature(payload, signature))
            {
                return Unauthorized();
            }

            using JsonDocument document = JsonDocument.Parse(payload);
            string eventName = document.RootElement.GetProperty("event").GetString() ?? string.Empty;
            JsonElement entity = document.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            string orderId = entity.GetProperty("order_id").GetString() ?? string.Empty;
            string paymentId = entity.GetProperty("id").GetString() ?? string.Empty;
            PaymentRecord? record = await _context.PaymentRecords.FirstOrDefaultAsync(item => item.RazorpayOrderId == orderId);
            if (record is null)
            {
                return Ok();
            }

            record.RazorpayPaymentId = paymentId;
            record.Amount = entity.GetProperty("amount").GetInt64() / 100m;
            record.Status = eventName switch
            {
                "payment.captured" => "Captured",
                "payment.failed" when !string.Equals(record.Status, "Captured", StringComparison.OrdinalIgnoreCase) => "Failed",
                _ => record.Status
            };
            record.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleWatchlist(int ipoId)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue ||
                !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.IPO_id == ipoId &&
                    (item.Status == "Approved" || item.Status == "Published" || item.Status == "Open" || item.Status == "Listed" || item.Status == "Closed"));
            if (ipo is null)
            {
                TempData["InvestorMessage"] = "This IPO is not available in the market.";
                return RedirectToAction(nameof(Index));
            }

            IPOWatchlist? existingItem = await _context.IPOWatchlists
                .FirstOrDefaultAsync(item => item.User_id == userId.Value && item.IPO_id == ipoId);
            if (existingItem is null)
            {
                _context.IPOWatchlists.Add(new IPOWatchlist
                {
                    User_id = userId.Value,
                    IPO_id = ipoId,
                    Added_at = DateTime.UtcNow
                });
                TempData["InvestorMessage"] = $"{ipo.IPO_name} was added to your watchlist.";
            }
            else
            {
                _context.IPOWatchlists.Remove(existingItem);
                TempData["InvestorMessage"] = $"{ipo.IPO_name} was removed from your watchlist.";
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

            [HttpGet]
            public async Task<IActionResult> Portfolio()
            {
                int? userId = HttpContext.Session.GetInt32("User_id");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                ViewBag.MarketIpos = await _context.IPOs
                    .AsNoTracking()
                    .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
                    .OrderBy(ipo => ipo.IPO_name)
                    .ToListAsync();
                return View(await BuildPortfolioAsync(userId.Value));
            }

            [HttpGet]
            public async Task<IActionResult> MarketDetails(int ipoId)
            {
                if (!IsInvestor())
                {
                    return Forbid();
                }

                MarketDataViewModel? market = await _stockMarketService.GetMarketDataAsync(ipoId);
                if (market is null)
                {
                    return NotFound();
                }

                return View(market);
            }

            [HttpGet]
            public async Task<IActionResult> Trade(int ipoId, bool sell = false, int? investmentId = null)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !IsInvestor())
                {
                    return Forbid();
                }

                IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == ipoId);
                if (ipo is null)
                {
                    return NotFound();
                }

                bool isListedSecurity = string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase) ||
                    (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= DateTime.UtcNow.Date);

                if (!isListedSecurity)
                {
                    TempData["InvestorMessage"] = $"Trading security policy: Secondary market trading is restricted to publicly listed stocks. '{ipo.IPO_name}' is currently in '{ipo.Status}' status.";
                    return RedirectToAction(nameof(Portfolio));
                }

                MarketDataViewModel? market = await _stockMarketService.GetMarketDataAsync(ipoId);
                if (market is null || !market.CurrentPrice.HasValue)
                {
                    TempData["InvestorMessage"] = "Market data is unavailable for this stock.";
                    return RedirectToAction(nameof(Index));
                }

                int availableQuantity = 0;
                if (sell && investmentId.HasValue)
                {
                    Investment? investment = await _context.Investments.AsNoTracking()
                        .FirstOrDefaultAsync(item => item.Investment_id == investmentId && item.User_id == userId.Value);
                    if (investment is null || investment.IPO_id != ipoId)
                    {
                        return NotFound();
                    }

                    int soldQuantity = await _context.InvestmentTransactions
                        .Where(transaction => transaction.Investment_id == investment.Investment_id && transaction.Transaction_type == "SELL")
                        .SumAsync(transaction => (int?)transaction.Quantity) ?? 0;
                    availableQuantity = Math.Max(0, investment.Quantity - soldQuantity);
                }

                User? investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.User_id == userId.Value);
                return View(new MarketTradeViewModel
                {
                    IpoId = ipoId,
                    IpoName = market.IpoName,
                    CompanyName = market.CompanyName,
                    StockSymbol = market.StockSymbol,
                    Exchange = "NSE",
                    CurrentPrice = market.CurrentPrice.Value,
                    NsePrice = market.NsePrice ?? market.CurrentPrice.Value,
                    BsePrice = market.BsePrice ?? market.CurrentPrice.Value,
                    AvailableQuantity = availableQuantity,
                    CashBalance = investor?.SimulatedCashBalance ?? 0m,
                    IsSell = sell,
                    InvestmentId = investmentId
                });
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> Buy(PortfolioTradeRequest request)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                if (request.Quantity <= 0 || request.Quantity > 500_000)
                {
                    TempData["InvestorMessage"] = "Invalid trade quantity. Enter a quantity between 1 and 500,000 shares.";
                    return RedirectToAction(nameof(Portfolio));
                }

                string exchange = string.Equals(request.Exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";

                IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == request.IpoId);
                if (ipo is null)
                {
                    TempData["InvestorMessage"] = "Security or IPO not found.";
                    return RedirectToAction(nameof(Portfolio));
                }

                bool isListedSecurity = string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase) ||
                    (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= DateTime.UtcNow.Date);

                if (!isListedSecurity)
                {
                    TempData["InvestorMessage"] = $"Trading security policy: Secondary market trading is restricted to publicly listed stocks. '{ipo.IPO_name}' is currently in '{ipo.Status}' status.";
                    return RedirectToAction(nameof(Portfolio));
                }

                MarketDataViewModel? marketData = await _stockMarketService.GetMarketDataAsync(ipo.IPO_id);
                if (marketData is null || !marketData.CanTrade || !marketData.IsMarketOpen || !marketData.CurrentPrice.HasValue)
                {
                    TempData["InvestorMessage"] = "Trading is unavailable because the market is closed or current market quotes are inactive.";
                    return RedirectToAction(nameof(Portfolio));
                }

                decimal tradePrice = exchange == "BSE" ? (marketData.BsePrice ?? marketData.CurrentPrice.Value) : (marketData.NsePrice ?? marketData.CurrentPrice.Value);
                if (tradePrice <= 0m)
                {
                    TempData["InvestorMessage"] = "Invalid trade price received from market provider.";
                    return RedirectToAction(nameof(Portfolio));
                }

                // Circuit limit safety check
                decimal prevClose = exchange == "BSE" ? (marketData.BsePreviousClose ?? marketData.PreviousClose ?? tradePrice) : (marketData.NsePreviousClose ?? marketData.PreviousClose ?? tradePrice);
                if (prevClose > 0m)
                {
                    decimal lowerCircuit = Math.Round(prevClose * 0.80m, 2);
                    decimal upperCircuit = Math.Round(prevClose * 1.20m, 2);
                    if (tradePrice < lowerCircuit || tradePrice > upperCircuit)
                    {
                        TempData["InvestorMessage"] = $"Trade price ₹{tradePrice:N2} breached daily circuit filter limits (₹{lowerCircuit:N2} - ₹{upperCircuit:N2}).";
                        return RedirectToAction(nameof(Portfolio));
                    }
                }

                decimal totalAmount = request.Quantity * tradePrice;
                User? investor = await _context.Users.FirstOrDefaultAsync(user => user.User_id == userId.Value && user.Is_active);
                if (investor is null || investor.SimulatedCashBalance < totalAmount)
                {
                    TempData["InvestorMessage"] = $"Insufficient simulated cash balance for this purchase. Required ₹{totalAmount:N2}, available ₹{investor?.SimulatedCashBalance:N2}.";
                    return RedirectToAction(nameof(Portfolio));
                }

                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                Investment? investment = await _context.Investments.FirstOrDefaultAsync(item =>
                    item.User_id == userId.Value && item.IPO_id == request.IpoId && item.Status != "Closed");
                if (investment is null)
                {
                    investment = new Investment
                    {
                        User_id = userId.Value,
                        IPO_id = request.IpoId,
                        Quantity = request.Quantity,
                        Buy_price = tradePrice,
                        Buy_date = GetIndiaStandardTime(),
                        Current_price = tradePrice,
                        Status = "Holding"
                    };
                    _context.Investments.Add(investment);
                    await _context.SaveChangesAsync();
                }
                else
                {
                    decimal existingCost = investment.Investment_amount ?? ((investment.Buy_price ?? 0m) * investment.Quantity);
                    investment.Buy_price = (existingCost + totalAmount) / (investment.Quantity + request.Quantity);
                    investment.Quantity = checked(investment.Quantity + request.Quantity);
                    investment.Current_price = tradePrice;
                    investment.Status = "Holding";
                }

                investor.SimulatedCashBalance -= totalAmount;
                _context.InvestmentTransactions.Add(new InvestmentTransaction
                {
                    Investment_id = investment.Investment_id,
                    Transaction_type = "BUY",
                    Quantity = request.Quantity,
                    Price = tradePrice,
                    Total_amount = totalAmount,
                    Transaction_date = GetIndiaStandardTime()
                });

                _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                {
                    UserId = userId.Value,
                    AdminUserId = null,
                    TransactionType = "TRADE_BUY",
                    Amount = -totalAmount,
                    BalanceBefore = investor.SimulatedCashBalance + totalAmount,
                    BalanceAfter = investor.SimulatedCashBalance,
                    Reason = $"Simulated portfolio purchase: {request.Quantity:N0} shares of {ipo.IPO_name} ({exchange}) @ ₹{tradePrice:N2}",
                    CreatedAt = GetIndiaStandardTime()
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                string investorName = $"{investor.First_name} {investor.Last_name}".Trim();
                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveTradeNotification", new
                    {
                        stockName = ipo.IPO_name,
                        exchange = exchange,
                        tradeType = "BUY",
                        quantity = request.Quantity,
                        price = tradePrice,
                        totalAmount = totalAmount,
                        user = string.IsNullOrWhiteSpace(investorName) ? "Investor" : investorName,
                        timestamp = DateTime.Now.ToString("hh:mm:ss tt")
                    });
                }
                catch
                {
                    // Ignore SignalR dispatch failure if client disconnected
                }

                TempData["InvestorMessage"] = $"Bought {request.Quantity:N0} shares of {ipo.IPO_name} on {exchange} at ₹{tradePrice:N2} in simulated trading.";
                return RedirectToAction(nameof(Portfolio));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> Sell(PortfolioSellRequest request)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                if (request.Quantity <= 0 || request.Quantity > 500_000)
                {
                    TempData["InvestorMessage"] = "Invalid trade quantity. Enter a quantity between 1 and 500,000 shares.";
                    return RedirectToAction(nameof(Portfolio));
                }

                Investment? investment = await _context.Investments.FirstOrDefaultAsync(item =>
                    item.Investment_id == request.InvestmentId && item.User_id == userId.Value);
                MarketDataViewModel? marketData = investment is null ? null : await _stockMarketService.GetMarketDataAsync(investment.IPO_id);
                if (investment is null || marketData is null || !marketData.CanTrade || !marketData.IsMarketOpen || !marketData.CurrentPrice.HasValue)
                {
                    TempData["InvestorMessage"] = "Selling is unavailable because the market is closed or current market data is unavailable.";
                    return RedirectToAction(nameof(Portfolio));
                }

                string exchange = string.Equals(request.Exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";

                int soldQuantity = await _context.InvestmentTransactions
                    .Where(transaction => transaction.Investment_id == investment.Investment_id &&
                        string.Equals(transaction.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase))
                    .SumAsync(transaction => (int?)transaction.Quantity) ?? 0;
                int availableQuantity = investment.Quantity - soldQuantity;
                if (request.Quantity > availableQuantity)
                {
                    TempData["InvestorMessage"] = $"You can sell up to {availableQuantity:N0} shares from this holding.";
                    return RedirectToAction(nameof(Portfolio));
                }

                User? investor = await _context.Users.FirstOrDefaultAsync(user => user.User_id == userId.Value && user.Is_active);
                decimal tradePrice = exchange == "BSE" ? (marketData.BsePrice ?? marketData.CurrentPrice.Value) : (marketData.NsePrice ?? marketData.CurrentPrice.Value);
                decimal totalAmount = request.Quantity * tradePrice;
                if (investor is null)
                {
                    TempData["InvestorMessage"] = "The investor account could not be loaded.";
                    return RedirectToAction(nameof(Portfolio));
                }

                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                _context.InvestmentTransactions.Add(new InvestmentTransaction
                {
                    Investment_id = investment.Investment_id,
                    Transaction_type = "SELL",
                    Quantity = request.Quantity,
                    Price = tradePrice,
                    Total_amount = totalAmount,
                    Transaction_date = GetIndiaStandardTime()
                });

                investor.SimulatedCashBalance += totalAmount;
                if (request.Quantity == availableQuantity)
                {
                    investment.Status = "Closed";
                }

                _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                {
                    UserId = userId.Value,
                    AdminUserId = null,
                    TransactionType = "TRADE_SELL",
                    Amount = totalAmount,
                    BalanceBefore = investor.SimulatedCashBalance - totalAmount,
                    BalanceAfter = investor.SimulatedCashBalance,
                    Reason = $"Simulated portfolio sale: {request.Quantity:N0} shares of {marketData.IpoName} ({exchange}) @ ₹{tradePrice:N2}",
                    CreatedAt = GetIndiaStandardTime()
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                string investorName = $"{investor.First_name} {investor.Last_name}".Trim();
                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveTradeNotification", new
                    {
                        stockName = marketData.IpoName,
                        exchange = exchange,
                        tradeType = "SELL",
                        quantity = request.Quantity,
                        price = tradePrice,
                        totalAmount = totalAmount,
                        user = string.IsNullOrWhiteSpace(investorName) ? "Investor" : investorName,
                        timestamp = DateTime.Now.ToString("hh:mm:ss tt")
                    });
                }
                catch
                {
                    // Ignore SignalR dispatch failure if client disconnected
                }

                TempData["InvestorMessage"] = $"Sold {request.Quantity:N0} shares of {marketData.IpoName} on {exchange} at ₹{tradePrice:N2} in simulated trading.";
                return RedirectToAction(nameof(Portfolio));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public IActionResult ResetSimulatedCash()
            {
                // Unrestricted investor resets disabled
                TempData["InvestorMessage"] = "Unrestricted wallet resets have been disabled. Virtual balances can only be adjusted by a platform Administrator with an auditable reason.";
                return RedirectToAction(nameof(Portfolio));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> AdjustVirtualBalance(int userId, decimal amount, string adjustmentType, string reason)
            {
                if (!IsAdmin())
                {
                    return Forbid();
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    TempData["AdminMessage"] = "A mandatory audit reason is required for any virtual balance adjustment.";
                    return RedirectToAction(nameof(UserDetails), new { userId });
                }

                if (amount <= 0 && !string.Equals(adjustmentType, "Set", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AdminMessage"] = "Adjustment amount must be greater than zero.";
                    return RedirectToAction(nameof(UserDetails), new { userId });
                }

                if (amount < 0)
                {
                    TempData["AdminMessage"] = "Adjustment amount cannot be negative.";
                    return RedirectToAction(nameof(UserDetails), new { userId });
                }

                User? investor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == userId);
                if (investor == null)
                {
                    return NotFound();
                }

                if (!string.Equals(investor.Role, "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AdminMessage"] = "Virtual balances can only be adjusted for investor accounts.";
                    return RedirectToAction(nameof(UserDetails), new { userId });
                }

                decimal oldBalance = investor.SimulatedCashBalance;
                decimal newBalance;
                decimal delta;
                string typeCode;

                switch (adjustmentType?.Trim().ToLowerInvariant())
                {
                    case "credit":
                    case "add":
                        newBalance = oldBalance + amount;
                        delta = amount;
                        typeCode = "ADMIN_CREDIT";
                        break;
                    case "debit":
                    case "deduct":
                        if (oldBalance < amount)
                        {
                            TempData["AdminMessage"] = $"Cannot deduct ₹{amount:N2} because the investor's current balance is only ₹{oldBalance:N2}.";
                            return RedirectToAction(nameof(UserDetails), new { userId });
                        }
                        newBalance = oldBalance - amount;
                        delta = -amount;
                        typeCode = "ADMIN_DEBIT";
                        break;
                    case "set":
                        newBalance = amount;
                        delta = newBalance - oldBalance;
                        typeCode = "ADMIN_SET";
                        break;
                    default:
                        TempData["AdminMessage"] = "Invalid adjustment type selected. Choose Credit, Debit, or Set.";
                        return RedirectToAction(nameof(UserDetails), new { userId });
                }

                int? adminId = HttpContext.Session.GetInt32("User_id");
                string adminName = HttpContext.Session.GetString("UserName") ?? "Administrator";
                DateTime now = DateTime.Now;

                await using var transaction = await _context.Database.BeginTransactionAsync();

                try
                {
                    investor.SimulatedCashBalance = newBalance;

                    // 1. Transaction Ledger entry
                    var ledgerEntry = new VirtualBalanceLedger
                    {
                        UserId = investor.User_id,
                        AdminUserId = adminId,
                        TransactionType = typeCode,
                        Amount = delta,
                        BalanceBefore = oldBalance,
                        BalanceAfter = newBalance,
                        Reason = reason.Trim(),
                        CreatedAt = now
                    };
                    _context.VirtualBalanceLedgers.Add(ledgerEntry);

                    // 2. Audit Record
                    var auditEntry = new AuditLog
                    {
                        ActorUserId = adminId,
                        Action = "AdjustVirtualBalance",
                        EntityType = "User",
                        EntityId = investor.User_id.ToString(),
                        Details = $"Admin #{adminId} ({adminName}) adjusted virtual balance for Investor #{investor.User_id} ({investor.First_name} {investor.Last_name}): ₹{oldBalance:N2} -> ₹{newBalance:N2} (Delta: {(delta >= 0 ? "+" : "")}₹{delta:N2}). Reason: {reason.Trim()}",
                        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.AuditLogs.Add(auditEntry);

                    // 3. System Notification for Investor
                    _context.SystemNotifications.Add(new SystemNotification
                    {
                        User_id = investor.User_id,
                        Title = "Virtual Balance Adjusted",
                        Message = $"Your virtual trading balance was adjusted by Admin from ₹{oldBalance:N2} to ₹{newBalance:N2}. Reason: {reason.Trim()}",
                        Created_at = now,
                        Is_read = false
                    });

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    TempData["AdminMessage"] = $"Virtual balance for {investor.First_name} {investor.Last_name} updated from ₹{oldBalance:N2} to ₹{newBalance:N2}. Audit log and transaction ledger recorded.";
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    TempData["AdminMessage"] = $"An error occurred while updating the virtual balance: {ex.Message}";
                }

                return RedirectToAction(nameof(UserDetails), new { userId });
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> AdminBroadcast(string title, string message, string category)
            {
                if (!IsAdmin())
                {
                    return Forbid();
                }

                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
                {
                    TempData["AdminMessage"] = "Broadcast title and message are required.";
                    return RedirectToAction(nameof(Index));
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
                    Title = title.Trim(),
                    Message = message.Trim(),
                    Created_at = now,
                    Is_read = false
                });

                _context.SystemNotifications.AddRange(notifications);
                await _context.SaveChangesAsync();

                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                    {
                        title = title.Trim(),
                        message = message.Trim(),
                        category = string.IsNullOrWhiteSpace(category) ? "Announcement" : category,
                        timestamp = DateTime.Now.ToString("hh:mm:ss tt"),
                        date = DateTime.Now.ToString("dd MMM yyyy")
                    });
                }
                catch
                {
                }

                TempData["AdminMessage"] = $"Broadcast message successfully sent to {activeInvestors.Count} investors.";
                return RedirectToAction(nameof(Index));
            }

            [HttpGet]
            public async Task<IActionResult> DownloadPortfolioStatementPdf()
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                byte[] pdfBytes = await _investorReportService.GeneratePortfolioStatementPdfAsync(userId.Value);
                string fileName = $"Portfolio-Statement-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }

            [HttpGet]
            public async Task<IActionResult> DownloadTransactionReportPdf()
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                byte[] pdfBytes = await _investorReportService.GenerateTransactionReportPdfAsync(userId.Value);
                string fileName = $"Transaction-Report-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }

            [HttpGet]
            public async Task<IActionResult> DownloadIpoApplicationReportPdf()
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                byte[] pdfBytes = await _investorReportService.GenerateIpoApplicationReportPdfAsync(userId.Value);
                string fileName = $"IPO-Applications-Report-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }

            [HttpGet]
            public async Task<IActionResult> DownloadAllotmentReportPdf()
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                byte[] pdfBytes = await _investorReportService.GenerateAllotmentReportPdfAsync(userId.Value);
                string fileName = $"IPO-Allotment-Report-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }

            [HttpGet]
            public async Task<IActionResult> ExportPortfolio()
            {
                int? userId = HttpContext.Session.GetInt32("User_id");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                PortfolioViewModel portfolio = await BuildPortfolioAsync(userId.Value);
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("IPO,Company,Quantity,Average Buy Price,Current Price,Invested Amount,Current Value,Unrealized P/L,Allocation %");
                foreach (PortfolioHoldingViewModel holding in portfolio.Holdings)
                {
                    csv.AppendLine(string.Join(",", Csv(holding.IpoName), Csv(holding.CompanyName), holding.Quantity,
                        holding.AverageBuyPrice.ToString("F2"), holding.CurrentPrice.ToString("F2"),
                        holding.InvestedAmount.ToString("F2"), holding.CurrentValue.ToString("F2"),
                        holding.UnrealizedProfitLoss.ToString("F2"), holding.AllocationPercentage.ToString("F2")));
                }
                csv.AppendLine();
                csv.AppendLine("Transaction Type,IPO,Quantity,Price,Total Amount,Date");
                foreach (PortfolioTransactionViewModel transaction in portfolio.Transactions)
                {
                    csv.AppendLine(string.Join(",", Csv(transaction.TransactionType), Csv(transaction.IpoName), transaction.Quantity,
                        transaction.Price.ToString("F2"), transaction.TotalAmount.ToString("F2"), transaction.TransactionDate.ToString("yyyy-MM-dd HH:mm")));
                }

                return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"portfolio-{DateTime.UtcNow:yyyyMMdd}.csv");
            }

        [HttpGet]
        public async Task<IActionResult> UserDetails(int userId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            User? user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.User_id == userId);

            if (user is null)
            {
                return NotFound();
            }

            if (string.Equals(user.Role, "Investor", StringComparison.OrdinalIgnoreCase))
            {
                var ledger = await _context.VirtualBalanceLedgers
                    .AsNoTracking()
                    .Where(l => l.UserId == userId)
                    .OrderByDescending(l => l.CreatedAt)
                    .ToListAsync();
                ViewBag.BalanceLedger = ledger;

                var adminIds = ledger
                    .Where(l => l.AdminUserId.HasValue)
                    .Select(l => l.AdminUserId!.Value)
                    .Distinct()
                    .ToList();

                ViewBag.AdminNames = await _context.Users
                    .AsNoTracking()
                    .Where(u => adminIds.Contains(u.User_id))
                    .ToDictionaryAsync(u => u.User_id, u => $"{u.First_name} {u.Last_name}".Trim());
            }

            return View(user);
        }

        [HttpGet]
        public async Task<IActionResult> EditProfile()
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            int? userId = HttpContext.Session.GetInt32("User_id");
            User? user = userId.HasValue
                ? await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.User_id == userId.Value)
                : null;
            if (user is null)
            {
                return RedirectToAction("Login", "Account");
            }

            return View(new AdminProfileViewModel
            {
                UserId = user.User_id,
                FirstName = user.First_name ?? string.Empty,
                LastName = user.Last_name ?? string.Empty,
                Email = user.Email,
                Phone = user.Phone,
                Role = user.Role ?? "Admin"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProfile(AdminProfileViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            int? sessionUserId = HttpContext.Session.GetInt32("User_id");
            if (!sessionUserId.HasValue || sessionUserId.Value != model.UserId)
            {
                return Forbid();
            }

            model.Email = model.Email.Trim().ToLowerInvariant();
            bool emailUsedByAnotherUser = await _context.Users.AnyAsync(user =>
                user.User_id != model.UserId && user.Email == model.Email);
            if (emailUsedByAnotherUser)
            {
                ModelState.AddModelError(nameof(model.Email), "This email address is already in use.");
            }

            if (!ModelState.IsValid)
            {
                model.Role = "Admin";
                return View(model);
            }

            User? userToUpdate = await _context.Users.FindAsync(model.UserId);
            if (userToUpdate is null || !userToUpdate.Is_active ||
                !string.Equals(userToUpdate.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            userToUpdate.First_name = model.FirstName.Trim();
            userToUpdate.Last_name = model.LastName.Trim();
            userToUpdate.Email = model.Email;
            userToUpdate.Phone = model.Phone.Trim();
            await _context.SaveChangesAsync();

            HttpContext.Session.SetString("UserName", $"{userToUpdate.First_name} {userToUpdate.Last_name}".Trim());
            HttpContext.Session.SetString("UserEmail", userToUpdate.Email);
            TempData["AdminMessage"] = "Your Admin profile was updated successfully.";
            return RedirectToAction(nameof(EditProfile));
        }

        [HttpGet]
        public async Task<IActionResult> InvestorProfile()
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            int? userId = HttpContext.Session.GetInt32("User_id");
            User? user = userId.HasValue
                ? await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.User_id == userId.Value && item.Is_active)
                : null;
            if (user is null)
            {
                return RedirectToAction("Login", "Account");
            }

            return View(new InvestorProfileViewModel
            {
                UserId = user.User_id,
                FirstName = user.First_name ?? string.Empty,
                LastName = user.Last_name ?? string.Empty,
                Email = user.Email,
                Phone = user.Phone,
                DematAccountNumber = user.DematAccountNumber,
                UpiId = user.UpiId
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> InvestorProfile(InvestorProfileViewModel model)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            int? sessionUserId = HttpContext.Session.GetInt32("User_id");
            if (!sessionUserId.HasValue || sessionUserId.Value != model.UserId)
            {
                return Forbid();
            }

            model.Email = model.Email.Trim().ToLowerInvariant();
            bool emailUsedByAnotherUser = await _context.Users.AnyAsync(user =>
                user.User_id != model.UserId && user.Email == model.Email);
            if (emailUsedByAnotherUser)
            {
                ModelState.AddModelError(nameof(model.Email), "This email address is already in use.");
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            User? userToUpdate = await _context.Users.FindAsync(model.UserId);
            if (userToUpdate is null || !userToUpdate.Is_active ||
                !string.Equals(userToUpdate.Role, "Investor", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            userToUpdate.First_name = model.FirstName.Trim();
            userToUpdate.Last_name = model.LastName.Trim();
            userToUpdate.Email = model.Email;
            userToUpdate.Phone = model.Phone.Trim();
            userToUpdate.DematAccountNumber = string.IsNullOrWhiteSpace(model.DematAccountNumber)
                ? null
                : model.DematAccountNumber.Trim();
            userToUpdate.UpiId = string.IsNullOrWhiteSpace(model.UpiId)
                ? null
                : model.UpiId.Trim();
            await _context.SaveChangesAsync();

            HttpContext.Session.SetString("UserName", $"{userToUpdate.First_name} {userToUpdate.Last_name}".Trim());
            HttpContext.Session.SetString("UserEmail", userToUpdate.Email);
            TempData["InvestorMessage"] = "Your profile was updated successfully.";
            return RedirectToAction(nameof(InvestorProfile));
        }

        [HttpGet]
        public async Task<IActionResult> CompanyDetails(int companyId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            Company? company = await _context.Companies
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Company_id == companyId);

            if (company is null)
            {
                return NotFound();
            }

            var model = new CompanyDetailsViewModel
            {
                Company = company,
                Ipos = await _context.IPOs
                    .AsNoTracking()
                    .Where(ipo => ipo.Company_id == companyId &&
                        (ipo.Status == "Approved" ||
                         ipo.Status == "Published" ||
                         ipo.Status == "Open" ||
                         ipo.Status == "Listed" ||
                         ipo.Status == "Closed"))
                    .OrderByDescending(ipo => ipo.Created_at)
                    .ToListAsync()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> IpoDetails(int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == ipoId);
            if (ipo is null)
            {
                return NotFound();
            }

            string companyName = await _context.Companies
                .Where(company => company.Company_id == ipo.Company_id)
                .Select(company => company.Company_name)
                .FirstOrDefaultAsync() ?? "Company";

            var gmpHistory = await _context.IPOGMPHistories
                .AsNoTracking()
                .Where(g => g.IPO_id == ipoId)
                .OrderBy(g => g.RecordedDate)
                .ThenBy(g => g.Gmp_id)
                .ToListAsync();

            return View(new AdminIpoDetailsViewModel 
            { 
                Ipo = ipo, 
                CompanyName = companyName,
                GmpHistory = gmpHistory 
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddGmpRecord(
            int ipoId,
            decimal gmpPrice,
            DateTime? recordedDate,
            decimal? kostakRate,
            decimal? subjectToSauda,
            string? fireRating,
            string? source,
            string? notes)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.FirstOrDefaultAsync(i => i.IPO_id == ipoId);
            if (ipo is null)
            {
                TempData["AdminMessage"] = "IPO not found.";
                return RedirectToAction(nameof(Index));
            }

            decimal basePrice = ipo.Price_max ?? ipo.Price_min ?? 0m;
            decimal estimatedListingPrice = basePrice + gmpPrice;
            decimal? gainPct = basePrice > 0m ? Math.Round((gmpPrice / basePrice) * 100m, 2) : (decimal?)null;
            DateTime recordDate = recordedDate ?? GetIndiaStandardTime().Date;

            string sentiment = !string.IsNullOrWhiteSpace(fireRating) 
                ? fireRating 
                : (gainPct >= 20m ? "Bullish" : (gainPct >= 10m ? "Moderate" : "Neutral"));

            var entry = new IPOGMPHistory
            {
                IPO_id = ipoId,
                GMPPrice = gmpPrice,
                EstimatedListingPrice = estimatedListingPrice,
                ExpectedListingGainPercentage = gainPct,
                KostakRate = kostakRate,
                SubjectToSauda = subjectToSauda,
                FireRating = sentiment,
                Source = string.IsNullOrWhiteSpace(source) ? "Unofficial Grey Market Desk" : source.Trim(),
                RecordedDate = recordDate,
                CreatedAt = GetIndiaStandardTime()
            };

            _context.IPOGMPHistories.Add(entry);
            await _context.SaveChangesAsync();

            int? adminId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
            string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";
            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = adminId,
                Action = "AddGmpRecord",
                EntityType = "IPOGMPHistory",
                EntityId = entry.Gmp_id.ToString(),
                Details = $"Admin #{adminId} ({adminName}) added unofficial GMP ₹{gmpPrice:N2} for IPO #{ipoId} ({ipo.IPO_name}) on {recordDate:dd MMM yyyy}.",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            TempData["AdminMessage"] = $"GMP record of ₹{gmpPrice:N2} added successfully for {ipo.IPO_name}.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteGmpRecord(int gmpId, int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPOGMPHistory? entry = await _context.IPOGMPHistories.FirstOrDefaultAsync(g => g.Gmp_id == gmpId && g.IPO_id == ipoId);
            if (entry is not null)
            {
                _context.IPOGMPHistories.Remove(entry);
                int? adminId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";
                _context.AuditLogs.Add(new AuditLog
                {
                    ActorUserId = adminId,
                    Action = "DeleteGmpRecord",
                    EntityType = "IPOGMPHistory",
                    EntityId = gmpId.ToString(),
                    Details = $"Admin #{adminId} ({adminName}) deleted unofficial GMP record #{gmpId} for IPO #{ipoId}.",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                TempData["AdminMessage"] = "GMP record deleted.";
            }

            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpGet]
        public async Task<IActionResult> InvestorIpoDetails(int ipoId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item =>
                item.IPO_id == ipoId &&
                (item.Status == "Approved" || item.Status == "Published" || item.Status == "Open" ||
                 item.Status == "Closed" || item.Status == "Listed"));
            if (ipo is null)
            {
                return NotFound();
            }

            CompanyIpoDetailsViewModel? details = (await LoadIpoDetailsAsync(new List<IPO> { ipo })).SingleOrDefault();
            return details is null ? NotFound() : View(details);
        }

        [HttpGet]
        public async Task<IActionResult> Applications(int? ipoId, string? status)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IQueryable<IPOApplication> query = _context.IPOApplications.AsNoTracking();
            if (ipoId.HasValue)
            {
                query = query.Where(application => application.IPO_id == ipoId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(application => application.Status == status);
            }

            List<IPOApplication> applications = await query
                .OrderByDescending(application => application.Application_date)
                .ToListAsync();
            int[] userIds = applications.Select(application => application.User_id).Distinct().ToArray();
            int[] applicationIpoIds = applications.Select(application => application.IPO_id).Distinct().ToArray();
            Dictionary<int, User> users = await _context.Users
                .AsNoTracking()
                .Where(user => userIds.Contains(user.User_id))
                .ToDictionaryAsync(user => user.User_id);
            Dictionary<int, IPO> ipos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => applicationIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id);
            Dictionary<int, IPOAllotment> allotments = await _context.IPOAllotments
                .AsNoTracking()
                .Where(allotment => applications.Select(application => application.Application_id).Contains(allotment.Application_id))
                .ToDictionaryAsync(allotment => allotment.Application_id);

            DateTime today = GetIndiaStandardTime().Date;
            var model = new AdminApplicationsViewModel
            {
                SelectedIpoId = ipoId,
                SelectedStatus = status,
                TotalApplications = applications.Count,
                TotalSharesApplied = applications.Sum(application => (long)application.Applied_shares),
                TotalAmount = applications.Sum(application => application.Application_amount ?? 0m),
                Applications = applications.Select(application =>
                {
                    IPO? appIpo = ipos.GetValueOrDefault(application.IPO_id);
                    var timing = appIpo is not null
                        ? CheckAllotmentTiming(appIpo, today)
                        : (IsClosed: false, HasAllotmentDate: false, IsAllotmentDateReached: false, AllotmentWindowOpen: false, StatusMessage: "IPO not found");
                    return new AdminApplicationListItem
                    {
                        Application = application,
                        Ipo = appIpo,
                        AllotmentWindowOpen = timing.AllotmentWindowOpen,
                        AllotmentTimingStatus = timing.StatusMessage,
                        Allotment = allotments.GetValueOrDefault(application.Application_id),
                        InvestorName = users.GetValueOrDefault(application.User_id) is User user
                            ? $"{user.First_name} {user.Last_name}".Trim()
                            : "Investor",
                        InvestorEmail = users.GetValueOrDefault(application.User_id)?.Email ?? string.Empty,
                        IpoName = appIpo?.IPO_name ?? "IPO"
                    };
                }).ToList(),
                AvailableIpos = await _context.IPOs
                    .AsNoTracking()
                    .OrderByDescending(ipo => ipo.Created_at)
                    .ToListAsync()
            };
            model.EligibleApplications = model.Applications
                .Where(item => item.Allotment is null &&
                    string.Equals(item.Application.Status, "Submitted", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Application.Payment_status, "Verified", StringComparison.OrdinalIgnoreCase) &&
                    item.AllotmentWindowOpen)
                .ToList();

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> AutomaticAllotmentPreview(int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            try
            {
                AutomaticAllotmentPlan plan = await BuildAutomaticAllotmentAsync(ipoId);
                Dictionary<int, string> investorNames = await GetInvestorNamesAsync(plan.Applications);
                int lotSize = plan.Ipo.Lot_size!.Value;
                decimal price = plan.Ipo.Price_max ?? plan.Ipo.Price_min ?? 0m;
                return View(new AutomaticAllotmentPreviewViewModel
                {
                    Ipo = plan.Ipo,
                    AvailableLots = plan.AvailableLots,
                    AllocatedLots = plan.Requests.Sum(request => request.AllottedLots),
                    LotSize = lotSize,
                    AllotmentPrice = price,
                    IsClosed = plan.IsClosed,
                    CloseDate = plan.Ipo.Close_date,
                    IsAllotmentDateReached = plan.IsAllotmentDateReached,
                    AllotmentDate = plan.Ipo.Allotment_date,
                    CategorySummaries = plan.CategorySummaries,
                    Rows = plan.Applications.Select(application =>
                    {
                        AllotmentRequestViewModel request = plan.Requests.First(item => item.ApplicationId == application.Application_id);
                        var cat = plan.ApplicationCategories.GetValueOrDefault(
                            application.Application_id,
                            (Code: "RII", Name: "Retail Individual Investors (RII)"));
                        return new AutomaticAllotmentPreviewRow
                        {
                            Application = application,
                            InvestorName = investorNames.GetValueOrDefault(application.User_id, "Investor"),
                            Category = cat.Name,
                            CategoryCode = cat.Code,
                            AllottedLots = request.AllottedLots,
                            AllottedShares = request.AllottedShares,
                            AllotmentPrice = price
                        };
                    }).ToList()
                });
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = exception.Message;
                return RedirectToAction(nameof(Applications), new { ipoId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmAutomaticAllotment(int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            try
            {
                AutomaticAllotmentPlan plan = await BuildAutomaticAllotmentAsync(ipoId);
                string[] statuses = await ProcessAllotmentsAsync(plan.Requests, false, ipoId);
                TempData["AdminMessage"] = $"{statuses.Length} automatic allotment(s) confirmed using category-wise allocation.";
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = exception.Message;
            }

            return RedirectToAction(nameof(Applications), new { ipoId });
        }

        private sealed class AutomaticAllotmentPlan
        {
            public IPO Ipo { get; set; } = new();
            public List<AllotmentRequestViewModel> Requests { get; set; } = new();
            public List<IPOApplication> Applications { get; set; } = new();
            public int AvailableLots { get; set; }
            public List<CategoryAllotmentSummaryViewModel> CategorySummaries { get; set; } = new();
            public Dictionary<int, (string Code, string Name)> ApplicationCategories { get; set; } = new();
            public bool IsClosed { get; set; }
            public bool IsAllotmentDateReached { get; set; }
        }

        private static (bool IsClosed, bool HasAllotmentDate, bool IsAllotmentDateReached, bool AllotmentWindowOpen, string StatusMessage) CheckAllotmentTiming(IPO ipo, DateTime today)
        {
            bool isClosed = today > ipo.Close_date.Date ||
                string.Equals(ipo.Status, "Closed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase);
            bool hasAllotmentDate = ipo.Allotment_date.HasValue;
            bool isAllotmentDateReached = hasAllotmentDate && today >= ipo.Allotment_date!.Value.Date;
            bool windowOpen = isClosed && isAllotmentDateReached;

            string statusMessage;
            if (!isClosed)
            {
                statusMessage = $"Bidding open until {ipo.Close_date:dd MMM yyyy}";
            }
            else if (!hasAllotmentDate)
            {
                statusMessage = "Bidding closed. Allotment date not configured.";
            }
            else if (!isAllotmentDateReached)
            {
                statusMessage = $"Bidding closed. Allotment on {ipo.Allotment_date!.Value:dd MMM yyyy}";
            }
            else
            {
                statusMessage = "Allotment window open";
            }

            return (isClosed, hasAllotmentDate, isAllotmentDateReached, windowOpen, statusMessage);
        }

        private static (string Code, string Name, string Rule) GetApplicationCategory(IPOApplication application, IPO ipo)
        {
            decimal price = ipo.Price_max ?? ipo.Price_min ?? 0m;
            decimal amount = application.Application_amount ?? (application.Applied_shares * price);
            if (amount <= 200000m)
            {
                return ("RII", "Retail Individual Investors (RII)", "Application ≤ ₹2,00,000");
            }
            if (amount <= 1000000m)
            {
                return ("NII", "Non-Institutional Investors (NII / HNI)", "Application > ₹2,00,000 & ≤ ₹10,00,000");
            }
            return ("QIB", "Qualified Institutional Buyers (QIB)", "Application > ₹10,00,000");
        }

        private static void AllocateProportionallyWithLargestRemainder(
            List<IPOApplication> applicants,
            int lotsToDistribute,
            Func<IPOApplication, int> getDemand,
            Action<int, int> assignLots)
        {
            if (applicants.Count == 0 || lotsToDistribute <= 0) return;

            int totalDemand = applicants.Sum(getDemand);
            if (totalDemand <= 0) return;

            if (totalDemand <= lotsToDistribute)
            {
                foreach (var app in applicants)
                {
                    assignLots(app.Application_id, getDemand(app));
                }
                return;
            }

            var allocationData = new List<(IPOApplication App, int BaseLots, decimal Remainder, int Demand)>();
            int totalBase = 0;

            foreach (var app in applicants)
            {
                int demand = getDemand(app);
                decimal exact = (decimal)demand * lotsToDistribute / totalDemand;
                int baseLots = (int)Math.Floor(exact);
                decimal remainder = exact - baseLots;
                allocationData.Add((app, baseLots, remainder, demand));
                totalBase += baseLots;
            }

            int remainingLots = lotsToDistribute - totalBase;

            var sorted = allocationData
                .OrderByDescending(x => x.Remainder)
                .ThenByDescending(x => x.Demand)
                .ThenBy(x => x.App.Application_date)
                .ThenBy(x => x.App.Application_id)
                .ToList();

            var finalLots = new Dictionary<int, int>();
            foreach (var item in sorted)
            {
                finalLots[item.App.Application_id] = item.BaseLots;
            }

            for (int i = 0; i < sorted.Count && remainingLots > 0; i++)
            {
                var item = sorted[i];
                if (finalLots[item.App.Application_id] < item.Demand)
                {
                    finalLots[item.App.Application_id]++;
                    remainingLots--;
                }
            }

            foreach (var kvp in finalLots)
            {
                assignLots(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Documented Category-Wise IPO Allotment Algorithm (SEBI ICDR Aligned):
        /// 1. Timing Verification: Strict check that bidding window is closed (today > Close_date or Status == Closed/Listed)
        ///    AND the configured Allotment_date has arrived (today >= Allotment_date).
        /// 2. Investor Categorization:
        ///    - Retail Individual Investors (RII): Application Amount <= ₹2,00,000 (35% Quota).
        ///    - Non-Institutional Investors (NII / HNI): Application Amount > ₹2,00,000 & <= ₹10,00,000 (15% Quota).
        ///    - Qualified Institutional Buyers (QIB): Application Amount > ₹10,00,000 (50% Quota).
        /// 3. Quota Rebalancing & Under-Subscription Spillover:
        ///    - If total demand <= available lots, 100% full allotment across all categories.
        ///    - If any category is under-subscribed (demand < quota), its unutilized surplus lots spill over to
        ///      the over-subscribed categories in proportion to their unmet excess demand.
        /// 4. Category-Wise Lot Allocation:
        ///    - Retail (RII) Over-Subscription:
        ///      * If quota >= retail applicants: Minimum 1-lot guarantee to each applicant, remainder distributed proportionally.
        ///      * If quota < retail applicants: Deterministic computerized lottery draw granting 1 lot to quota-many applicants.
        ///    - NII & QIB Over-Subscription:
        ///      * Proportional allocation with Largest Remainder Method (Hamilton/Hare-Niemeyer method) ensuring zero lot wastage.
        /// </summary>
        private async Task<AutomaticAllotmentPlan> BuildAutomaticAllotmentAsync(int ipoId)
        {
            IPO? ipo = await _context.IPOs.FirstOrDefaultAsync(item => item.IPO_id == ipoId);
            if (ipo is null)
            {
                throw new InvalidOperationException("The selected IPO could not be found.");
            }

            DateTime today = GetIndiaStandardTime().Date;
            var timing = CheckAllotmentTiming(ipo, today);
            if (!timing.IsClosed)
            {
                throw new InvalidOperationException($"Automatic allotment can be processed only after the IPO bidding window closes (Closing date: {ipo.Close_date:dd MMM yyyy}). Current status: {ipo.Status}.");
            }
            if (!timing.HasAllotmentDate)
            {
                throw new InvalidOperationException("Automatic allotment cannot be processed until an allotment date is configured on the IPO.");
            }
            if (!timing.IsAllotmentDateReached)
            {
                throw new InvalidOperationException($"Automatic allotment can be processed on or after the configured allotment date: {ipo.Allotment_date!.Value:dd MMM yyyy}.");
            }
            if (!ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0)
            {
                throw new InvalidOperationException("Automatic allotment requires a valid IPO lot size.");
            }

            List<IPOApplication> applications = await _context.IPOApplications
                .AsNoTracking()
                .Where(application => application.IPO_id == ipoId &&
                    application.Status == "Submitted" &&
                    application.Payment_status == "Verified" &&
                    !_context.IPOAllotments.Any(allotment => allotment.Application_id == application.Application_id))
                .OrderBy(application => application.Application_date)
                .ThenBy(application => application.Application_id)
                .ToListAsync();

            long alreadyAllocatedShares = await _context.IPOAllotments
                .Where(allotment => allotment.IPO_id == ipoId)
                .SumAsync(allotment => (long)allotment.Allotted_shares);
            long remainingShares = ipo.Total_shares.HasValue
                ? Math.Max(0, ipo.Total_shares.Value - alreadyAllocatedShares)
                : applications.Sum(application => (long)application.Applied_shares);
            int availableLots = (int)Math.Min(int.MaxValue, remainingShares / ipo.Lot_size.Value);

            var appCategories = new Dictionary<int, (string Code, string Name)>();
            var retailApps = new List<IPOApplication>();
            var niiApps = new List<IPOApplication>();
            var qibApps = new List<IPOApplication>();

            foreach (var app in applications)
            {
                var cat = GetApplicationCategory(app, ipo);
                appCategories[app.Application_id] = (cat.Code, cat.Name);
                if (cat.Code == "RII") retailApps.Add(app);
                else if (cat.Code == "NII") niiApps.Add(app);
                else qibApps.Add(app);
            }

            int retailDemand = retailApps.Sum(a => a.Applied_lots);
            int niiDemand = niiApps.Sum(a => a.Applied_lots);
            int qibDemand = qibApps.Sum(a => a.Applied_lots);
            int totalDemand = retailDemand + niiDemand + qibDemand;

            // Statutory Quotas (SEBI Norms: 35% Retail, 15% NII, 50% QIB)
            int initialRiiLots = (int)Math.Floor(availableLots * 0.35m);
            int initialNiiLots = (int)Math.Floor(availableLots * 0.15m);
            int initialQibLots = Math.Max(0, availableLots - initialRiiLots - initialNiiLots);

            int effectiveRiiLots = initialRiiLots;
            int effectiveNiiLots = initialNiiLots;
            int effectiveQibLots = initialQibLots;

            if (totalDemand <= availableLots)
            {
                // Full allotment across all categories
                effectiveRiiLots = retailDemand;
                effectiveNiiLots = niiDemand;
                effectiveQibLots = qibDemand;
            }
            else
            {
                // Iterative spillover of under-subscribed quota to over-subscribed categories
                bool redistributed;
                do
                {
                    redistributed = false;
                    int surplusLots = 0;

                    if (effectiveRiiLots > retailDemand)
                    {
                        surplusLots += (effectiveRiiLots - retailDemand);
                        effectiveRiiLots = retailDemand;
                    }
                    if (effectiveNiiLots > niiDemand)
                    {
                        surplusLots += (effectiveNiiLots - niiDemand);
                        effectiveNiiLots = niiDemand;
                    }
                    if (effectiveQibLots > qibDemand)
                    {
                        surplusLots += (effectiveQibLots - qibDemand);
                        effectiveQibLots = qibDemand;
                    }

                    if (surplusLots > 0)
                    {
                        int unmetRii = Math.Max(0, retailDemand - effectiveRiiLots);
                        int unmetNii = Math.Max(0, niiDemand - effectiveNiiLots);
                        int unmetQib = Math.Max(0, qibDemand - effectiveQibLots);
                        int totalUnmet = unmetRii + unmetNii + unmetQib;

                        if (totalUnmet > 0)
                        {
                            redistributed = true;
                            int lotsToDistribute = Math.Min(surplusLots, totalUnmet);
                            int toRii = totalUnmet > 0 ? (int)Math.Floor((decimal)lotsToDistribute * unmetRii / totalUnmet) : 0;
                            int toNii = totalUnmet > 0 ? (int)Math.Floor((decimal)lotsToDistribute * unmetNii / totalUnmet) : 0;
                            int toQib = totalUnmet > 0 ? (int)Math.Floor((decimal)lotsToDistribute * unmetQib / totalUnmet) : 0;

                            int remainder = lotsToDistribute - (toRii + toNii + toQib);
                            var priority = new[] {
                                (Cat: "RII", Unmet: unmetRii - toRii),
                                (Cat: "NII", Unmet: unmetNii - toNii),
                                (Cat: "QIB", Unmet: unmetQib - toQib)
                            }.OrderByDescending(x => x.Unmet).ToList();

                            foreach (var p in priority)
                            {
                                if (remainder <= 0) break;
                                if (p.Cat == "RII" && p.Unmet > 0) { toRii++; remainder--; }
                                else if (p.Cat == "NII" && p.Unmet > 0) { toNii++; remainder--; }
                                else if (p.Cat == "QIB" && p.Unmet > 0) { toQib++; remainder--; }
                            }

                            effectiveRiiLots += toRii;
                            effectiveNiiLots += toNii;
                            effectiveQibLots += toQib;
                        }
                    }
                } while (redistributed);
            }

            var allottedLotsMap = new Dictionary<int, int>();

            // Allocate Retail (RII)
            string riiMethod;
            string riiSpilloverNote = effectiveRiiLots > initialRiiLots
                ? $"+{effectiveRiiLots - initialRiiLots} lots spilled over from under-subscribed categories"
                : (effectiveRiiLots < initialRiiLots && retailDemand < initialRiiLots
                    ? $"{initialRiiLots - effectiveRiiLots} unused surplus lots spilled over to other categories"
                    : "Standard statutory quota");

            if (retailDemand <= effectiveRiiLots)
            {
                foreach (var app in retailApps)
                {
                    allottedLotsMap[app.Application_id] = app.Applied_lots;
                }
                riiMethod = "Full allotment (100% of demand satisfied)";
            }
            else if (effectiveRiiLots >= retailApps.Count)
            {
                foreach (var app in retailApps)
                {
                    allottedLotsMap[app.Application_id] = 1;
                }
                int remainingLots = effectiveRiiLots - retailApps.Count;
                if (remainingLots > 0)
                {
                    var multiLotApps = retailApps.Where(a => a.Applied_lots > 1).ToList();
                    int multiLotExcess = multiLotApps.Sum(a => a.Applied_lots - 1);
                    if (multiLotExcess > 0)
                    {
                        AllocateProportionallyWithLargestRemainder(
                            multiLotApps,
                            remainingLots,
                            a => a.Applied_lots - 1,
                            (appId, extra) => allottedLotsMap[appId] += extra
                        );
                    }
                }
                riiMethod = "1-Lot Minimum Guarantee + Proportional excess distribution";
            }
            else
            {
                var rng = new Random(ipo.IPO_id * 397 ^ 0x4950);
                var shuffled = retailApps.OrderBy(_ => rng.Next()).ToList();
                for (int i = 0; i < shuffled.Count; i++)
                {
                    allottedLotsMap[shuffled[i].Application_id] = i < effectiveRiiLots ? 1 : 0;
                }
                riiMethod = $"Computerized lottery draw (1 lot each to {effectiveRiiLots} of {retailApps.Count} applicants; {retailApps.Count - effectiveRiiLots} unallotted)";
            }

            // Allocate NII
            string niiMethod;
            string niiSpilloverNote = effectiveNiiLots > initialNiiLots
                ? $"+{effectiveNiiLots - initialRiiLots} lots spilled over from under-subscribed categories"
                : (effectiveNiiLots < initialNiiLots && niiDemand < initialNiiLots
                    ? $"{initialNiiLots - effectiveNiiLots} unused surplus lots spilled over to other categories"
                    : "Standard statutory quota");

            if (niiDemand <= effectiveNiiLots)
            {
                foreach (var app in niiApps)
                {
                    allottedLotsMap[app.Application_id] = app.Applied_lots;
                }
                niiMethod = "Full allotment (100% of demand satisfied)";
            }
            else
            {
                AllocateProportionallyWithLargestRemainder(
                    niiApps,
                    effectiveNiiLots,
                    a => a.Applied_lots,
                    (appId, lots) => allottedLotsMap[appId] = lots
                );
                decimal ratio = niiDemand > 0 ? Math.Round((decimal)effectiveNiiLots / niiDemand * 100m, 2) : 0m;
                niiMethod = $"Proportional allotment ({ratio}% ratio, Largest Remainder Method)";
            }

            // Allocate QIB
            string qibMethod;
            string qibSpilloverNote = effectiveQibLots > initialQibLots
                ? $"+{effectiveQibLots - initialQibLots} lots spilled over from under-subscribed categories"
                : (effectiveQibLots < initialQibLots && qibDemand < initialQibLots
                    ? $"{initialQibLots - effectiveQibLots} unused surplus lots spilled over to other categories"
                    : "Standard statutory quota");

            if (qibDemand <= effectiveQibLots)
            {
                foreach (var app in qibApps)
                {
                    allottedLotsMap[app.Application_id] = app.Applied_lots;
                }
                qibMethod = "Full allotment (100% of demand satisfied)";
            }
            else
            {
                AllocateProportionallyWithLargestRemainder(
                    qibApps,
                    effectiveQibLots,
                    a => a.Applied_lots,
                    (appId, lots) => allottedLotsMap[appId] = lots
                );
                decimal ratio = qibDemand > 0 ? Math.Round((decimal)effectiveQibLots / qibDemand * 100m, 2) : 0m;
                qibMethod = $"Proportional allotment ({ratio}% ratio, Largest Remainder Method)";
            }

            var categorySummaries = new List<CategoryAllotmentSummaryViewModel>
            {
                new CategoryAllotmentSummaryViewModel
                {
                    CategoryCode = "RII",
                    CategoryName = "Retail Individual Investors (RII)",
                    MaxApplicationRule = "Application ≤ ₹2,00,000",
                    QuotaPercentage = 35m,
                    InitialQuotaLots = initialRiiLots,
                    EffectiveQuotaLots = effectiveRiiLots,
                    EffectiveQuotaShares = (long)effectiveRiiLots * ipo.Lot_size.Value,
                    ApplicationCount = retailApps.Count,
                    AppliedLots = retailDemand,
                    AppliedShares = (long)retailDemand * ipo.Lot_size.Value,
                    AllocatedLots = retailApps.Sum(a => allottedLotsMap.GetValueOrDefault(a.Application_id, 0)),
                    AllocatedShares = (long)retailApps.Sum(a => allottedLotsMap.GetValueOrDefault(a.Application_id, 0)) * ipo.Lot_size.Value,
                    AllocationMethod = riiMethod,
                    SpilloverNote = riiSpilloverNote
                },
                new CategoryAllotmentSummaryViewModel
                {
                    CategoryCode = "NII",
                    CategoryName = "Non-Institutional Investors (NII / HNI)",
                    MaxApplicationRule = "Application > ₹2,00,000 & ≤ ₹10,00,000",
                    QuotaPercentage = 15m,
                    InitialQuotaLots = initialNiiLots,
                    EffectiveQuotaLots = effectiveNiiLots,
                    EffectiveQuotaShares = (long)effectiveNiiLots * ipo.Lot_size.Value,
                    ApplicationCount = niiApps.Count,
                    AppliedLots = niiDemand,
                    AppliedShares = (long)niiDemand * ipo.Lot_size.Value,
                    AllocatedLots = niiApps.Sum(a => allottedLotsMap.GetValueOrDefault(a.Application_id, 0)),
                    AllocatedShares = (long)niiApps.Sum(a => allottedLotsMap.GetValueOrDefault(a.Application_id, 0)) * ipo.Lot_size.Value,
                    AllocationMethod = niiMethod,
                    SpilloverNote = niiSpilloverNote
                },
                new CategoryAllotmentSummaryViewModel
                {
                    CategoryCode = "QIB",
                    CategoryName = "Qualified Institutional Buyers (QIB)",
                    MaxApplicationRule = "Application > ₹10,00,000",
                    QuotaPercentage = 50m,
                    InitialQuotaLots = initialQibLots,
                    EffectiveQuotaLots = effectiveQibLots,
                    EffectiveQuotaShares = (long)effectiveQibLots * ipo.Lot_size.Value,
                    ApplicationCount = qibApps.Count,
                    AppliedLots = qibDemand,
                    AppliedShares = (long)qibDemand * ipo.Lot_size.Value,
                    AllocatedLots = qibApps.Sum(a => allottedLotsMap.GetValueOrDefault(a.Application_id, 0)),
                    AllocatedShares = (long)qibApps.Sum(a => allottedLotsMap.GetValueOrDefault(a.Application_id, 0)) * ipo.Lot_size.Value,
                    AllocationMethod = qibMethod,
                    SpilloverNote = qibSpilloverNote
                }
            };

            List<AllotmentRequestViewModel> requests = applications.Select(application =>
            {
                int lots = allottedLotsMap.GetValueOrDefault(application.Application_id, 0);
                return new AllotmentRequestViewModel
                {
                    ApplicationId = application.Application_id,
                    AllottedLots = lots,
                    AllottedShares = checked(lots * ipo.Lot_size.Value),
                    AllotmentPrice = ipo.Price_max ?? ipo.Price_min ?? 0m
                };
            }).ToList();

            return new AutomaticAllotmentPlan
            {
                Ipo = ipo,
                Requests = requests,
                Applications = applications,
                AvailableLots = availableLots,
                CategorySummaries = categorySummaries,
                ApplicationCategories = appCategories,
                IsClosed = timing.IsClosed,
                IsAllotmentDateReached = timing.IsAllotmentDateReached
            };
        }

        private async Task<Dictionary<int, string>> GetInvestorNamesAsync(IEnumerable<IPOApplication> applications)
        {
            int[] userIds = applications.Select(application => application.User_id).Distinct().ToArray();
            return await _context.Users
                .AsNoTracking()
                .Where(user => userIds.Contains(user.User_id))
                .ToDictionaryAsync(user => user.User_id, user => $"{user.First_name} {user.Last_name}".Trim());
        }

        [HttpGet]
        public async Task<IActionResult> PreviewAllotment(int applicationId, int allottedLots, int allottedShares, decimal allotmentPrice)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Application_id == applicationId);
            IPO? ipo = application is null ? null : await _context.IPOs.AsNoTracking()
                .FirstOrDefaultAsync(item => item.IPO_id == application.IPO_id);
            if (application is null || ipo is null)
            {
                return NotFound();
            }

            ValidateAllotmentRequest(application, ipo, allottedLots, allottedShares, allotmentPrice);
            if (!ModelState.IsValid)
            {
                TempData["AdminMessage"] = string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage));
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId });
            }

            decimal price = allotmentPrice > 0m
                ? allotmentPrice
                : (application.Application_amount ?? 0m) / application.Applied_shares;
            var categoryInfo = GetApplicationCategory(application, ipo);
            return View("AllotmentPreview", new AllotmentPreviewViewModel
            {
                Application = application,
                Ipo = ipo,
                AllottedLots = allottedLots,
                AllottedShares = allottedShares,
                AllotmentPrice = price,
                CategoryName = categoryInfo.Name,
                CategoryCode = categoryInfo.Code
            });
        }

        [HttpGet]
        public async Task<IActionResult> AllotmentReport(int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == ipoId);
            if (ipo is null)
            {
                return NotFound();
            }

            var rows = await _context.IPOApplications.AsNoTracking()
                .Where(application => application.IPO_id == ipoId)
                .Join(_context.IPOAllotments.AsNoTracking(),
                    application => application.Application_id,
                    allotment => allotment.Application_id,
                    (application, allotment) => new
                    {
                        application.Application_number,
                        application.User_id,
                        application.Payment_status,
                        allotment.Status,
                        allotment.Allotted_lots,
                        allotment.Allotted_shares,
                        allotment.Allotment_price,
                        allotment.Allotment_date
                    })
                .OrderBy(row => row.Application_number)
                .ToListAsync();
            StringBuilder csv = new("Application,User,PaymentStatus,Result,Lots,Shares,Price,Date\r\n");
            foreach (var row in rows)
            {
                csv.AppendLine($"{row.Application_number},{row.User_id},{row.Payment_status},{row.Status},{row.Allotted_lots},{row.Allotted_shares},{row.Allotment_price:F2},{row.Allotment_date:O}");
            }
            return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"{ipo.IPO_name}-allotment-report.csv");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SimulateUnblock(int applicationId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications.FindAsync(applicationId);
            PaymentRecord? payment = application is null
                ? null
                : await _context.PaymentRecords.FirstOrDefaultAsync(record => record.RazorpayOrderId == application.Razorpay_order_id);
            if (payment is null || payment.UnblockedAmount <= 0m || payment.RefundStatus == "Completed")
            {
                TempData["AdminMessage"] = "No amount is pending for simulated unblocking.";
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId });
            }

            payment.RefundStatus = "Completed";
            payment.RefundProcessedAt = GetIndiaStandardTime();
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"₹{payment.UnblockedAmount:N2} was marked as unblocked in the simulated workflow.";
            return RedirectToAction(nameof(ApplicationDetails), new { applicationId });
        }

        [HttpGet]
        public async Task<IActionResult> Search(AdminSearchViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IQueryable<User> users = _context.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(model.UserQuery))
            {
                string query = model.UserQuery.Trim();
                users = users.Where(user =>
                    (user.First_name + " " + user.Last_name).Contains(query) ||
                    user.Email.Contains(query) ||
                    user.Phone.Contains(query));
            }
            if (!string.IsNullOrWhiteSpace(model.UserRole))
            {
                users = users.Where(user => user.Role == model.UserRole);
            }
            if (!string.IsNullOrWhiteSpace(model.UserStatus))
            {
                bool isActive = model.UserStatus == "Active";
                users = users.Where(user => user.Is_active == isActive);
            }
            model.Users = await users.OrderBy(user => user.First_name).ThenBy(user => user.Last_name).Take(100).ToListAsync();

            IQueryable<IPO> ipos = _context.IPOs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(model.IpoQuery))
            {
                string query = model.IpoQuery.Trim();
                ipos = ipos.Where(ipo => ipo.IPO_name.Contains(query));
            }
            if (!string.IsNullOrWhiteSpace(model.IpoStatus))
            {
                ipos = ipos.Where(ipo => ipo.Status == model.IpoStatus);
            }
            if (!string.IsNullOrWhiteSpace(model.IpoCompanyQuery))
            {
                string companyQuery = model.IpoCompanyQuery.Trim();
                ipos = ipos.Where(ipo => _context.Companies.Any(company =>
                    company.Company_id == ipo.Company_id &&
                    company.Company_name.Contains(companyQuery)));
            }
            if (model.IpoDate.HasValue)
            {
                DateTime date = model.IpoDate.Value.Date;
                ipos = ipos.Where(ipo => ipo.Open_date.Date == date || ipo.Close_date.Date == date || ipo.Created_at.Date == date);
            }
            if (!string.IsNullOrWhiteSpace(model.IpoCategory))
            {
                ipos = ipos.Where(ipo => ipo.IPO_type == model.IpoCategory);
            }
            List<IPO> ipoResults = await ipos.OrderByDescending(ipo => ipo.Created_at).Take(100).ToListAsync();
            int[] companyIds = ipoResults.Select(ipo => ipo.Company_id).Distinct().ToArray();
            Dictionary<int, string> companyNames = await _context.Companies.AsNoTracking()
                .Where(company => companyIds.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
            model.Ipos = ipoResults.Select(ipo => new AdminSearchIpoItem
            {
                Ipo = ipo,
                CompanyName = companyNames.GetValueOrDefault(ipo.Company_id, "Company")
            }).ToList();

            IQueryable<Company> companies = _context.Companies.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(model.CompanyQuery))
            {
                companies = companies.Where(company => company.Company_name.Contains(model.CompanyQuery.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(model.CompanyCin))
            {
                companies = companies.Where(company => company.CIN != null && company.CIN.Contains(model.CompanyCin.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(model.CompanyIndustry))
            {
                companies = companies.Where(company => company.Industry != null && company.Industry.Contains(model.CompanyIndustry.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(model.CompanyStatus))
            {
                companies = companies.Where(company => company.Approval_status == model.CompanyStatus);
            }
            model.Companies = await companies.OrderBy(company => company.Company_name).Take(100).ToListAsync();

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> ApplicationDetails(int applicationId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Application_id == applicationId);
            if (application is null)
            {
                return NotFound();
            }

            IPO? ipo = await _context.IPOs
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.IPO_id == application.IPO_id);
            if (ipo is null)
            {
                return NotFound();
            }

            return View(new AdminApplicationDetailsViewModel
            {
                Application = application,
                Investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.User_id == application.User_id),
                Ipo = ipo,
                Allotment = await _context.IPOAllotments.AsNoTracking().FirstOrDefaultAsync(allotment => allotment.Application_id == application.Application_id),
                Payment = await _context.PaymentRecords.AsNoTracking().FirstOrDefaultAsync(payment => payment.RazorpayOrderId == application.Razorpay_order_id)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelApplication(ApplicationCancellationViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            if (!ModelState.IsValid)
            {
                TempData["AdminMessage"] = "Enter a cancellation reason of at least 5 characters.";
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
            }

            IPOApplication? application = await _context.IPOApplications
                .FirstOrDefaultAsync(item => item.Application_id == model.ApplicationId);
            if (application is null)
            {
                return NotFound();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking()
                .FirstOrDefaultAsync(item => item.IPO_id == application.IPO_id);
            if (ipo is null)
            {
                return NotFound();
            }

            bool alreadyAllotted = await _context.IPOAllotments
                .AnyAsync(allotment => allotment.Application_id == application.Application_id);
            if (alreadyAllotted ||
                ipo.Close_date.Date <= GetIndiaStandardTime().Date ||
                string.Equals(application.Status, "Allotted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(application.Status, "Partially Allotted", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "Applications cannot be cancelled after the IPO closes or after allotment.";
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
            }

            if (string.Equals(application.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(application.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(application.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "This application has already been closed and cannot be cancelled.";
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
            }

            int? adminId = HttpContext.Session.GetInt32("User_id");
            if (!adminId.HasValue)
            {
                return Forbid();
            }

            DateTime cancellationDate = GetIndiaStandardTime();
            application.Status = "Cancelled";
            application.Cancellation_admin_id = adminId.Value;
            application.Cancellation_reason = model.Reason.Trim();
            application.Cancellation_date = cancellationDate;
            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = application.User_id,
                Title = "IPO application cancelled",
                Message = $"Your IPO application #{application.Application_id} was cancelled by the administrator. Reason: {application.Cancellation_reason}",
                Created_at = cancellationDate,
                Is_read = false
            });

            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"Application #{application.Application_id} was cancelled and the investor was notified.";
            return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessAllotment(int applicationId, int allottedLots, int allottedShares, decimal allotmentPrice)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            try
            {
                string[] statuses = await ProcessAllotmentsAsync(new[]
                {
                    new AllotmentRequestViewModel
                    {
                        ApplicationId = applicationId,
                        AllottedLots = allottedLots,
                        AllottedShares = allottedShares,
                        AllotmentPrice = allotmentPrice
                    }
                }, false);
                TempData["AdminMessage"] = $"Application #{applicationId} processed as {statuses[0]}.";
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = exception.Message;
            }

            return RedirectToAction(nameof(ApplicationDetails), new { applicationId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessBulkAllotment(BulkAllotmentRequestViewModel request)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            if (request.ApplicationIds.Length == 0)
            {
                TempData["AdminMessage"] = "Select at least one application to process.";
                return RedirectToAction(nameof(Applications), new { ipoId = request.IpoId });
            }

            try
            {
                string[] statuses = await ProcessAllotmentsAsync(request.ApplicationIds.Distinct().Select(applicationId =>
                    new AllotmentRequestViewModel { ApplicationId = applicationId }), true, request.IpoId);
                TempData["AdminMessage"] = $"{statuses.Length} allotment(s) processed successfully.";
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = exception.Message;
            }

            return RedirectToAction(nameof(Applications), new { ipoId = request.IpoId });
        }

        private async Task<string[]> ProcessAllotmentsAsync(
            IEnumerable<AllotmentRequestViewModel> requests,
            bool defaultToApplied,
            int? expectedIpoId = null)
        {
            List<AllotmentRequestViewModel> requested = requests.ToList();
            if (requested.Count == 0)
            {
                throw new InvalidOperationException("No applications were selected.");
            }

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            int[] applicationIds = requested.Select(item => item.ApplicationId).Distinct().ToArray();
                List<IPOApplication> applications = await _context.IPOApplications
                    .Where(application => applicationIds.Contains(application.Application_id))
                    .ToListAsync();
                if (applications.Count != applicationIds.Length)
                {
                    throw new InvalidOperationException("One or more selected applications could not be found.");
                }

                int ipoId = applications[0].IPO_id;
                if (expectedIpoId.HasValue && expectedIpoId.Value != ipoId)
                {
                    throw new InvalidOperationException("The selected applications do not belong to the selected IPO.");
                }
                if (applications.Any(application => application.IPO_id != ipoId))
                {
                    throw new InvalidOperationException("Bulk allotment can contain applications from only one IPO.");
                }

                IPO? ipo = await _context.IPOs.FirstOrDefaultAsync(item => item.IPO_id == ipoId);
                if (ipo is null)
                {
                    throw new InvalidOperationException("The selected IPO could not be found.");
                }

                DateTime today = GetIndiaStandardTime().Date;
                var timing = CheckAllotmentTiming(ipo, today);
                if (!timing.IsClosed)
                {
                    throw new InvalidOperationException($"Allotment can be processed only after the IPO bidding window closes (Closing date: {ipo.Close_date:dd MMM yyyy}). Current status: {ipo.Status}.");
                }
                if (!timing.HasAllotmentDate)
                {
                    throw new InvalidOperationException("Allotment cannot be processed until an allotment date is configured on the IPO.");
                }
                if (!timing.IsAllotmentDateReached)
                {
                    throw new InvalidOperationException($"Allotment can be processed on or after the configured allotment date: {ipo.Allotment_date!.Value:dd MMM yyyy}.");
                }

                if (!ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0)
                {
                    throw new InvalidOperationException("The IPO does not have a valid lot size.");
                }

                int[] alreadyProcessedIds = await _context.IPOAllotments
                    .Where(allotment => applicationIds.Contains(allotment.Application_id))
                    .Select(allotment => allotment.Application_id)
                    .ToArrayAsync();
                if (alreadyProcessedIds.Length > 0)
                {
                    throw new InvalidOperationException($"Application(s) already processed: {string.Join(", ", alreadyProcessedIds)}.");
                }

                long existingShares = await _context.IPOAllotments
                    .Where(allotment => allotment.IPO_id == ipoId)
                    .SumAsync(allotment => (long)allotment.Allotted_shares);
                long requestedShares = 0;
                DateTime allotmentDate = GetIndiaStandardTime();
                foreach (IPOApplication application in applications)
                {
                    AllotmentRequestViewModel request = requested.First(item => item.ApplicationId == application.Application_id);
                    int allottedLots = defaultToApplied && request.AllottedLots == 0 && request.AllottedShares == 0
                        ? application.Applied_lots
                        : request.AllottedLots;
                    int allottedShares = defaultToApplied && request.AllottedShares == 0 && request.AllottedLots == 0
                        ? application.Applied_shares
                        : request.AllottedShares;

                    ModelState.Clear();
                    ValidateAllotmentRequest(application, ipo, allottedLots, allottedShares, requested.First(item => item.ApplicationId == application.Application_id).AllotmentPrice);
                    if (!ModelState.IsValid)
                    {
                        throw new InvalidOperationException($"Application #{application.Application_id} is not eligible for allotment: {string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage))}");
                    }

                    requestedShares = checked(requestedShares + allottedShares);
                    decimal appliedPrice = application.Applied_shares == 0
                        ? 0m
                        : (application.Application_amount ?? 0m) / application.Applied_shares;
                    decimal price = request.AllotmentPrice > 0m ? request.AllotmentPrice : appliedPrice;
                    if (price <= 0m)
                    {
                        price = ipo.Price_max ?? ipo.Price_min ?? 0m;
                    }

                    await ApplyAllotmentAsync(application, allottedLots, allottedShares, price, allotmentDate, ipo);
                }

                if (ipo.Total_shares.HasValue && existingShares + requestedShares > ipo.Total_shares.Value)
                {
                    throw new InvalidOperationException($"The selected allotments exceed the IPO's available shares. Remaining shares: {ipo.Total_shares.Value - existingShares:N0}.");
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                foreach (IPOApplication application in applications)
                {
                    User? investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.User_id == application.User_id);
                    if (investor is not null)
                    {
                        bool successfullyAllotted = string.Equals(application.Status, "Allotted", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(application.Status, "Partially Allotted", StringComparison.OrdinalIgnoreCase);
                        IPOAllotment? allotment = await _context.IPOAllotments
                            .AsNoTracking()
                            .FirstOrDefaultAsync(item => item.Application_id == application.Application_id);
                        int allottedResultShares = allotment?.Allotted_shares ?? 0;
                        decimal allotmentResultPrice = allotment?.Allotment_price ?? 0m;
                        string subject = successfullyAllotted
                            ? $"{ipo.IPO_name} IPO allotment successful"
                            : $"{ipo.IPO_name} IPO allotment result";
                        string message = successfullyAllotted
                            ? $"{ipo.IPO_name}: your IPO application #{application.Application_id} was successfully allotted {allottedResultShares:N0} share(s) at ₹{allotmentResultPrice:N2}. Please log in to your investor dashboard to view the complete result."
                            : $"{ipo.IPO_name}: no shares were allotted for your application #{application.Application_id}. Please log in to your investor dashboard to view the result.";
                        await TrySendEventEmailAsync(investor, subject, message);
                    }
                }
            return applications.Select(application => application.Status ?? "Not Allotted").ToArray();
        }

        private void ValidateAllotmentRequest(IPOApplication application, IPO ipo, int allottedLots, int allottedShares, decimal allotmentPrice)
        {
            DateTime today = GetIndiaStandardTime().Date;
            var timing = CheckAllotmentTiming(ipo, today);
            if (!timing.IsClosed)
            {
                ModelState.AddModelError(string.Empty, $"Allotment can be processed only after the IPO bidding window closes (Closing date: {ipo.Close_date:dd MMM yyyy}).");
            }
            if (!timing.HasAllotmentDate)
            {
                ModelState.AddModelError(string.Empty, "Allotment cannot be processed until an allotment date is configured on the IPO.");
            }
            else if (!timing.IsAllotmentDateReached)
            {
                ModelState.AddModelError(string.Empty, $"Allotment can be processed on or after the configured allotment date: {ipo.Allotment_date!.Value:dd MMM yyyy}.");
            }

            if (!string.Equals(application.Status, "Submitted", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(application.Payment_status, "Verified", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(string.Empty, "Only submitted applications with verified payment are eligible.");
            }
            if (!ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0 ||
                allottedLots < 0 || allottedShares < 0 ||
                allottedLots > application.Applied_lots ||
                allottedShares > application.Applied_shares ||
                allottedShares != checked(allottedLots * (ipo.Lot_size ?? 0)))
            {
                ModelState.AddModelError(string.Empty, "Allotment must use valid lots and cannot exceed the application.");
            }
            decimal price = allotmentPrice > 0m
                ? allotmentPrice
                : application.Applied_shares == 0 ? 0m : (application.Application_amount ?? 0m) / application.Applied_shares;
            if (price <= 0m || price < (ipo.Price_min ?? 0m) || price > (ipo.Price_max ?? decimal.MaxValue))
            {
                ModelState.AddModelError(string.Empty, "Allotment price must be within the IPO price range.");
            }
        }

        private async Task ApplyAllotmentAsync(
            IPOApplication application,
            int allottedLots,
            int allottedShares,
            decimal price,
            DateTime allotmentDate,
            IPO ipo)
        {
            string status = allottedShares == 0
                ? "Not Allotted"
                : allottedShares == application.Applied_shares
                    ? "Allotted"
                    : "Partially Allotted";

            _context.IPOAllotments.Add(new IPOAllotment
            {
                Application_id = application.Application_id,
                User_id = application.User_id,
                IPO_id = application.IPO_id,
                Allotted_lots = allottedLots,
                Allotted_shares = allottedShares,
                Allotment_price = price,
                Status = status,
                Allotment_date = allotmentDate
            });
            application.Status = status;
            PaymentRecord? paymentRecord = await _context.PaymentRecords
                .FirstOrDefaultAsync(record => record.RazorpayOrderId == application.Razorpay_order_id);
            if (paymentRecord is not null)
            {
                paymentRecord.UnblockedAmount = Math.Max(0m, (application.Application_amount ?? 0m) - (allottedShares * price));
                paymentRecord.RefundStatus = paymentRecord.UnblockedAmount > 0m ? "Pending Simulation" : "Not Applicable";
            }

            if (allottedShares == 0)
            {
                _context.SystemNotifications.Add(new SystemNotification
                {
                    User_id = application.User_id,
                    Title = "IPO allotment result",
                    Message = $"{ipo.IPO_name}: no shares were allotted for application #{application.Application_id}.",
                    Created_at = allotmentDate,
                    Is_read = false
                });
                return;
            }

            Investment? investment = _context.Investments.Local.FirstOrDefault(item =>
                item.User_id == application.User_id && item.IPO_id == application.IPO_id && item.Status != "Closed");
            if (investment is null)
            {
                investment = await _context.Investments.FirstOrDefaultAsync(item =>
                    item.User_id == application.User_id && item.IPO_id == application.IPO_id && item.Status != "Closed");
            }

            if (investment is null)
            {
                investment = new Investment
                {
                    User_id = application.User_id,
                    IPO_id = application.IPO_id,
                    Quantity = allottedShares,
                    Buy_price = price,
                    Buy_date = allotmentDate,
                    Current_price = price,
                    Status = "Holding"
                };
                _context.Investments.Add(investment);
            }
            else
            {
                decimal existingCost = investment.Investment_amount ?? ((investment.Buy_price ?? 0m) * investment.Quantity);
                investment.Quantity = checked(investment.Quantity + allottedShares);
                investment.Buy_price = (existingCost + allottedShares * price) / investment.Quantity;
                investment.Current_price ??= price;
                investment.Status = "Holding";
            }

            _context.InvestmentTransactions.Add(new InvestmentTransaction
            {
                Investment = investment,
                Transaction_type = "ALLOTMENT",
                Quantity = allottedShares,
                Price = price,
                Total_amount = allottedShares * price,
                Transaction_date = allotmentDate
            });
            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = application.User_id,
                Title = "IPO allotment confirmed",
                Message = $"{ipo.IPO_name}: {allottedShares:N0} share(s) allotted at ₹{price:N2} for application #{application.Application_id}.",
                Created_at = allotmentDate,
                Is_read = false
            });
        }

        [HttpGet]
        public async Task<IActionResult> CreateNotification()
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            return View(await BuildNotificationModelAsync(new CreateNotificationViewModel()));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateNotification(CreateNotificationViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            if (model.RecipientType == "User" && !model.UserId.HasValue)
            {
                ModelState.AddModelError(nameof(model.UserId), "Select a specific user.");
            }
            if (model.RecipientType == "Company" && !model.CompanyId.HasValue)
            {
                ModelState.AddModelError(nameof(model.CompanyId), "Select a specific company.");
            }

            if (!ModelState.IsValid)
            {
                return View(await BuildNotificationModelAsync(model));
            }

            int[] recipientIds;
            switch (model.RecipientType)
            {
                case "Investors":
                    recipientIds = await _context.Users.Where(user => user.Role == "Investor" && user.Is_active).Select(user => user.User_id).ToArrayAsync();
                    break;
                case "Companies":
                    recipientIds = await _context.Users.Where(user => user.Role == "Company" && user.Is_active).Select(user => user.User_id).ToArrayAsync();
                    break;
                case "User":
                    bool userExists = await _context.Users.AnyAsync(user => user.User_id == model.UserId && user.Is_active);
                    if (!userExists)
                    {
                        ModelState.AddModelError(nameof(model.UserId), "The selected user is unavailable.");
                        return View(await BuildNotificationModelAsync(model));
                    }
                    recipientIds = new[] { model.UserId!.Value };
                    break;
                case "Company":
                    int? companyUserId = await _context.Companies
                        .Where(company => company.Company_id == model.CompanyId)
                        .Select(company => (int?)company.User_id)
                        .FirstOrDefaultAsync();
                    if (!companyUserId.HasValue || !await _context.Users.AnyAsync(user => user.User_id == companyUserId && user.Is_active))
                    {
                        ModelState.AddModelError(nameof(model.CompanyId), "The selected company account is unavailable.");
                        return View(await BuildNotificationModelAsync(model));
                    }
                    recipientIds = new[] { companyUserId.Value };
                    break;
                default:
                    ModelState.AddModelError(nameof(model.RecipientType), "Select a valid recipient.");
                    return View(await BuildNotificationModelAsync(model));
            }

            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(recipientIds.Select(userId => new SystemNotification
            {
                User_id = userId,
                Title = model.Title.Trim(),
                Message = model.Message.Trim(),
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"Notification sent to {recipientIds.Length} recipient(s).";

            User[] recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.User_id) && user.Is_active)
                .ToArrayAsync();
            foreach (User recipient in recipients)
            {
                await TrySendEventEmailAsync(
                    recipient,
                    model.Title.Trim(),
                    model.Message.Trim());
            }
            return RedirectToAction(nameof(CreateNotification));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeactivateUser(int userId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            int? currentUserId = HttpContext.Session.GetInt32("User_id");
            if (currentUserId == userId)
            {
                TempData["AdminMessage"] = "The active administrator account cannot be deactivated.";
                return RedirectToAction(nameof(UserDetails), new { userId });
            }

            User? user = await _context.Users.FindAsync(userId);
            if (user is null)
            {
                return NotFound();
            }

            if (!user.Is_active)
            {
                TempData["AdminMessage"] = "This user account is already inactive.";
                return RedirectToAction(nameof(UserDetails), new { userId });
            }

            user.Is_active = false;
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{user.First_name} {user.Last_name}'s account was deactivated.";
            return RedirectToAction(nameof(UserDetails), new { userId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveCompany(int companyId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            Company? company = await _context.Companies.FindAsync(companyId);
            if (company is null || !string.Equals(company.Approval_status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That Company request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }

            User? user = await _context.Users.FindAsync(company.User_id);
            if (user is null)
            {
                TempData["AdminMessage"] = "The user linked to this Company request could not be found.";
                return RedirectToAction(nameof(Index));
            }

            company.Approval_status = "Approved";
            user.Role = "Company";
            await _context.SaveChangesAsync();
            await TrySendEventEmailAsync(
                user,
                "Company Registration Approved",
                $"{company.Company_name} registration has been approved. You can now access the Company Dashboard.");

            TempData["AdminMessage"] = $"{company.Company_name} was approved. The user can now access the Company Dashboard.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DenyCompany(int companyId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            Company? company = await _context.Companies.FindAsync(companyId);
            if (company is null || !string.Equals(company.Approval_status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That Company request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }

            company.Approval_status = "Denied";
            await _context.SaveChangesAsync();

            TempData["AdminMessage"] = $"{company.Company_name} was denied. The request is no longer in the approval queue.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null || !string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That IPO request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }
            ipo.Status = "Approved";
            await _context.SaveChangesAsync();
            await NotifyCompanyIpoApprovedAsync(ipo);
            TempData["AdminMessage"] = $"{ipo.IPO_name} was approved and can now be published publicly.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            if (!string.Equals(ipo.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "Only an approved IPO can be published.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }
            ipo.Status = "Open";
            await _context.SaveChangesAsync();
            User[] investors = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .ToArrayAsync();
            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(investors.Select(investor => new SystemNotification
            {
                User_id = investor.User_id,
                Title = "IPO Available",
                Message = $"{ipo.IPO_name} is now available to investors.",
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
            foreach (User investor in investors)
            {
                await TrySendEventEmailAsync(
                    investor,
                    "IPO Available",
                    $"{ipo.IPO_name} is now available to investors. Log in to review the offering.");
            }
            TempData["AdminMessage"] = $"{ipo.IPO_name} was published and is now visible to investors.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult PublishCompanyIpo(int ipoId)
        {
            // Trading Security Policy: Companies cannot configure trading status or publish IPOs.
            // Only platform administrators are authorized to configure trading security and open IPO offerings for subscription or trading.
            TempData["CompanyMessage"] = "Trading Security Policy: Only platform administrators are authorized to configure trading security, approve, and open IPOs.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DenyIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            if (string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That IPO is already closed and cannot be rejected again.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }
            ipo.Status = "Rejected";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} was rejected and is no longer available.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            ipo.Status = "Cancelled";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} was cancelled.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeIpoStatus(int ipoId, string status)
        {
            if (!IsAdmin()) return Forbid();
            string[] validStatuses = { "Pending", "Approved", "Open", "Closed", "Listed", "Rejected", "Cancelled" };
            string? selectedStatus = validStatuses.FirstOrDefault(value =>
                string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            if (selectedStatus is null)
            {
                TempData["AdminMessage"] = "Please select a valid IPO status.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }

            string? previousStatus = ipo.Status;
            ipo.Status = selectedStatus;
            await _context.SaveChangesAsync();
            if (string.Equals(selectedStatus, "Approved", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousStatus, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                await NotifyCompanyIpoApprovedAsync(ipo);
            }
            TempData["AdminMessage"] = $"{ipo.IPO_name} status changed to {selectedStatus}.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitIpo([Bind(Prefix = "IpoSubmission")] IpoSubmissionViewModel submission)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Company", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            if (submission.OpeningDate.HasValue && submission.ClosingDate.HasValue && submission.ClosingDate < submission.OpeningDate)
            {
                ModelState.AddModelError(nameof(submission.ClosingDate), "Closing Date must be after Opening Date.");
            }

            if (submission.PriceMin.HasValue && submission.PriceMax.HasValue && submission.PriceMax < submission.PriceMin)
            {
                ModelState.AddModelError(nameof(submission.PriceMax), "Maximum Price must be greater than or equal to Minimum Price.");
            }

            try
            {
                submission.TotalShares = ConvertShareValue(submission.TotalShares, "Units");
                submission.LotSize = ConvertIntShareValue(submission.LotSize, "Units");
                submission.IssueSize = ConvertMoneyValue(submission.IssueSize, submission.IssueSizeUnit);
                submission.Revenue = ConvertMoneyValue(submission.Revenue, submission.RevenueUnit);
                submission.Profit = ConvertMoneyValue(submission.Profit, submission.ProfitUnit);
                submission.NetWorth = ConvertMoneyValue(submission.NetWorth, submission.NetWorthUnit);
            }
            catch (ArgumentException exception)
            {
                ModelState.AddModelError("", exception.Message);
            }

            Company? company = await _context.Companies
                .FirstOrDefaultAsync(item => item.User_id == userId.Value && item.Approval_status == "Approved");
            if (company is null)
            {
                ModelState.AddModelError("", "An approved company profile is required before submitting an IPO.");
            }

            if (!ModelState.IsValid)
            {
                var model = new DashboardViewModel
                {
                    UserName = HttpContext.Session.GetString("UserName") ?? "User",
                    Role = "Company",
                    IpoSubmission = submission
                };
                await LoadCompanyDashboardAsync(model, userId.Value);
                model.IpoSubmission = submission;
                return View("Company", model);
            }

            var ipo = new IPO
            {
                Company_id = company!.Company_id,
                IPO_name = submission.IpoName,
                Total_shares = submission.TotalShares,
                Open_date = submission.OpeningDate!.Value,
                Close_date = submission.ClosingDate!.Value,
                Price_min = submission.PriceMin,
                Price_max = submission.PriceMax,
                Lot_size = submission.LotSize,
                Issue_size = submission.IssueSize,
                Allotment_date = submission.AllotmentDate,
                Funds_unblock_date = submission.FundsUnblockDate,
                Listing_date = submission.ListingDate,
                Description = submission.Description,
                Apply_type = "Retail",
                Status = "Pending",
                Created_at = DateTime.Now
            };

            try
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                _context.IPOs.Add(ipo);
                await _context.SaveChangesAsync();

                _context.IPOFinancials.Add(new IPOFinancial
                {
                    IPO_id = ipo.IPO_id,
                    Financial_year = submission.FinancialYear!.Value,
                    Revenue = submission.Revenue,
                    Profit = submission.Profit,
                    Net_worth = submission.NetWorth
                });
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (DbUpdateException)
            {
                ModelState.AddModelError("", "The IPO could not be saved. Please verify all required information and try again.");
                var model = new DashboardViewModel
                {
                    UserName = HttpContext.Session.GetString("UserName") ?? "User",
                    Role = "Company",
                    IpoSubmission = submission
                };
                await LoadCompanyDashboardAsync(model, userId.Value);
                model.IpoSubmission = submission;
                return View("Company", model);
            }

            TempData["SubmittedIpoName"] = ipo.IPO_name;
            return RedirectToAction(nameof(IpoSubmissionPending));
        }

        [HttpGet]
        public IActionResult IpoSubmissionPending()
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Company", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Login", "Account");
            }

            return View("~/Views/Home/IpoSubmissionPending.cshtml");
        }

        private static long? ConvertShareValue(long? value, string unit)
        {
            if (!value.HasValue)
            {
                return null;
            }

            decimal converted = value.Value * GetUnitMultiplier(unit);
            return checked((long)converted);
        }

        private static int? ConvertIntShareValue(int? value, string unit)
        {
            if (!value.HasValue)
            {
                return null;
            }

            decimal converted = value.Value * GetUnitMultiplier(unit);
            return checked((int)converted);
        }

        private static decimal? ConvertMoneyValue(decimal? value, string unit)
        {
            return value.HasValue ? value.Value * GetUnitMultiplier(unit) : null;
        }

        private static decimal GetUnitMultiplier(string unit)
        {
            return unit?.Trim().ToLowerInvariant() switch
            {
                "units" => 1m,
                "thousands" => 1_000m,
                "lakhs" => 100_000m,
                "crores" => 10_000_000m,
                _ => throw new ArgumentException("Please select a valid unit: Units, Thousands, Lakhs, or Crores.")
            };
        }

        private async Task LoadInvestorDashboardAsync(
            DashboardViewModel model,
            int userId,
            string? marketStatus,
            string? marketIpoType,
            string? marketIpoName,
            string? marketCompany,
            int? calendarMonth,
            int? calendarYear,
            int[]? compareIpoIds)
        {
            model.Notifications = await LoadNotificationsAsync(userId);
            model.Market = await _stockMarketService.GetDashboardAsync();
            model.PaymentHistory = await _context.PaymentRecords
                .AsNoTracking()
                .Where(record => record.UserId == userId)
                .OrderByDescending(record => record.CreatedAt)
                .Take(20)
                .ToListAsync();
            int[] paymentIpoIds = model.PaymentHistory.Select(record => record.IpoId).Distinct().ToArray();
            model.PaymentIpoNames = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => paymentIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id, ipo => ipo.IPO_name);
            User? investor = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user => user.User_id == userId && user.Is_active);
            model.InvestorDematAccountNumber = investor?.DematAccountNumber ?? string.Empty;
            model.InvestorUpiId = investor?.UpiId ?? string.Empty;
            DateTime today = DateTime.UtcNow.Date;
            model.OpenIpoCount = await _context.IPOs.CountAsync(ipo =>
                (ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed") &&
                ipo.Open_date.Date <= today && ipo.Close_date.Date >= today);
            model.ApplicationCount = await _context.IPOApplications.CountAsync(application => application.User_id == userId);
            model.WatchlistCount = await _context.IPOWatchlists.CountAsync(item => item.User_id == userId);
            List<Investment> investorInvestments = await _context.Investments
                .AsNoTracking()
                .Where(investment => investment.User_id == userId)
                .ToListAsync();
            PortfolioViewModel portfolio = await BuildPortfolioAsync(userId);
            model.PortfolioPerformance = portfolio.Performance;
            model.InvestmentCount = investorInvestments.Count;
            model.InvestmentTotal = investorInvestments.Sum(investment =>
                investment.Investment_amount ?? ((investment.Buy_price ?? 0m) * investment.Quantity));
            model.ProfitLoss = investorInvestments.Sum(investment =>
                investment.Current_price.HasValue
                    ? (investment.Current_price.Value * investment.Quantity) -
                      (investment.Investment_amount ?? ((investment.Buy_price ?? 0m) * investment.Quantity))
                    : 0m);
            int[] investmentIpoIds = investorInvestments.Select(investment => investment.IPO_id).Distinct().ToArray();
            model.InvestmentIpoNames = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => investmentIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id, ipo => ipo.IPO_name);
            model.RecentApplications = await _context.IPOApplications
                .AsNoTracking()
                .Where(application => application.User_id == userId)
                .OrderByDescending(application => application.Application_date)
                .ToListAsync();
            int[] applicationIpoIds = model.RecentApplications.Select(application => application.IPO_id).Distinct().ToArray();
            model.ApplicationIpoNames = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => applicationIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id, ipo => ipo.IPO_name);
            model.ApplicationAllotments = await _context.IPOAllotments
                .AsNoTracking()
                .Where(allotment => model.RecentApplications.Select(application => application.Application_id).Contains(allotment.Application_id))
                .ToDictionaryAsync(allotment => allotment.Application_id);
            List<IPO> publishedIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
                .OrderByDescending(ipo => ipo.Created_at)
                .ToListAsync();
            model.RecentIpos = publishedIpos.Take(5).ToList();
            model.PublishedIpoDetails = await LoadIpoDetailsAsync(publishedIpos);
            model.Investments = investorInvestments
                .OrderByDescending(investment => investment.Buy_date)
                .Take(5)
                .ToList();
            await LoadCompanyNamesAsync(model, publishedIpos.Select(ipo => ipo.Company_id));
            int[] watchlistIpoIds = await _context.IPOWatchlists
                .AsNoTracking()
                .Where(item => item.User_id == userId)
                .Select(item => item.IPO_id)
                .ToArrayAsync();
            model.WatchlistDetails = model.PublishedIpoDetails
                .Where(detail => watchlistIpoIds.Contains(detail.Ipo.IPO_id))
                .ToList();
            DateTime calendarNow = GetIndiaStandardTime();
            model.CalendarYear = calendarYear is >= 2000 and <= 2100 ? calendarYear.Value : calendarNow.Year;
            model.CalendarMonth = calendarMonth is >= 1 and <= 12 ? calendarMonth.Value : calendarNow.Month;
            model.CalendarIpoDetails = model.PublishedIpoDetails
                .Where(detail => HasIpoDateInMonth(detail.Ipo, model.CalendarYear, model.CalendarMonth))
                .OrderBy(detail => GetFirstIpoDateInMonth(detail.Ipo, model.CalendarYear, model.CalendarMonth))
                .ThenBy(detail => detail.Ipo.IPO_name)
                .ToList();
            model.ComparisonIpoIds = (compareIpoIds ?? Array.Empty<int>()).Distinct().Take(3).ToArray();
            model.ComparisonIpoDetails = model.PublishedIpoDetails
                .Where(detail => model.ComparisonIpoIds.Contains(detail.Ipo.IPO_id))
                .OrderBy(detail => Array.IndexOf(model.ComparisonIpoIds, detail.Ipo.IPO_id))
                .ToList();
            if (model.ComparisonIpoDetails.Count > 0)
            {
                int[] comparisonIds = model.ComparisonIpoDetails.Select(detail => detail.Ipo.IPO_id).ToArray();
                Dictionary<int, long> offeredShares = model.ComparisonIpoDetails
                    .Where(detail => detail.Ipo.Total_shares.HasValue)
                    .ToDictionary(detail => detail.Ipo.IPO_id, detail => detail.Ipo.Total_shares!.Value);
                Dictionary<int, long> appliedShares = await _context.IPOApplications
                    .AsNoTracking()
                    .Where(application => comparisonIds.Contains(application.IPO_id) &&
                        application.Status != "Rejected" && application.Status != "Cancelled")
                    .GroupBy(application => application.IPO_id)
                    .Select(group => new
                    {
                        IpoId = group.Key,
                        AppliedShares = group.Sum(application => (long)application.Applied_shares)
                    })
                    .ToDictionaryAsync(item => item.IpoId, item => item.AppliedShares);
                model.ComparisonSubscriptionMultiples = comparisonIds.ToDictionary(
                    ipoId => ipoId,
                    ipoId => offeredShares.TryGetValue(ipoId, out long offered) && offered > 0
                        ? (decimal)appliedShares.GetValueOrDefault(ipoId) / offered
                        : 0m);
            }

            model.MarketStatus = marketStatus;
            model.MarketIpoType = marketIpoType;
            model.MarketIpoName = marketIpoName;
            model.MarketCompany = marketCompany;
            model.MarketIpoTypes = publishedIpos
                .Where(ipo => !string.IsNullOrWhiteSpace(ipo.IPO_type))
                .Select(ipo => ipo.IPO_type!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type)
                .ToList();
            model.MarketCompanies = publishedIpos
                .Select(ipo => model.CompanyNames.GetValueOrDefault(ipo.Company_id, "Company"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(company => company)
                .ToList();
            model.MarketStatuses = publishedIpos.ToDictionary(
                ipo => ipo.IPO_id,
                ipo => GetInvestorMarketStatus(ipo, today));

            IEnumerable<IPO> marketIpos = publishedIpos;
            if (!string.IsNullOrWhiteSpace(marketStatus))
            {
                marketIpos = marketIpos.Where(ipo =>
                    string.Equals(GetInvestorMarketStatus(ipo, today), marketStatus.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(marketIpoType))
            {
                marketIpos = marketIpos.Where(ipo =>
                    string.Equals(ipo.IPO_type, marketIpoType.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(marketIpoName))
            {
                marketIpos = marketIpos.Where(ipo =>
                    ipo.IPO_name.Contains(marketIpoName.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(marketCompany))
            {
                marketIpos = marketIpos.Where(ipo =>
                    string.Equals(model.CompanyNames.GetValueOrDefault(ipo.Company_id, "Company"), marketCompany.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            model.MarketIpos = marketIpos.ToList();
        }

        private static string GetInvestorMarketStatus(IPO ipo, DateTime today)
        {
            if (string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase) ||
                (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= today))
            {
                return "Listed";
            }

            if (ipo.Open_date.Date > today)
            {
                return "Upcoming";
            }

            if (ipo.Close_date.Date < today)
            {
                return "Closed";
            }

            return "Open";
        }

        private static bool HasIpoDateInMonth(IPO ipo, int year, int month)
        {
            return IsInMonth(ipo.Open_date, year, month) ||
                IsInMonth(ipo.Close_date, year, month) ||
                IsInMonth(ipo.Allotment_date, year, month) ||
                IsInMonth(ipo.Funds_unblock_date, year, month) ||
                IsInMonth(ipo.Listing_date, year, month);
        }

        private static DateTime GetFirstIpoDateInMonth(IPO ipo, int year, int month)
        {
            DateTime monthStart = new(year, month, 1);
            DateTime monthEnd = monthStart.AddMonths(1);
            return new[] { ipo.Open_date, ipo.Close_date, ipo.Allotment_date, ipo.Funds_unblock_date, ipo.Listing_date }
                .Where(date => date.HasValue && date.Value >= monthStart && date.Value < monthEnd)
                .Select(date => date!.Value)
                .DefaultIfEmpty(monthEnd)
                .Min();
        }

        private static bool IsInMonth(DateTime? date, int year, int month)
        {
            return date.HasValue && date.Value.Year == year && date.Value.Month == month;
        }

        private async Task<PortfolioViewModel> BuildPortfolioAsync(int userId)
        {
            List<Investment> investments = await _context.Investments
                .AsNoTracking()
                .Where(investment => investment.User_id == userId)
                .OrderByDescending(investment => investment.Buy_date)
                .ToListAsync();
            int[] investmentIds = investments.Select(investment => investment.Investment_id).ToArray();
            List<InvestmentTransaction> transactions = await _context.InvestmentTransactions
                .AsNoTracking()
                .Where(transaction => investmentIds.Contains(transaction.Investment_id))
                .OrderByDescending(transaction => transaction.Transaction_date)
                .ToListAsync();
            int[] ipoIds = investments.Select(investment => investment.IPO_id).Distinct().ToArray();
            Dictionary<int, IPO> ipos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id);
            Dictionary<int, string> companies = await _context.Companies
                .AsNoTracking()
                .Where(company => ipos.Values.Select(ipo => ipo.Company_id).Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
            Dictionary<int, IPOListing> listings = await _context.IPOListings
                .AsNoTracking()
                .Where(listing => ipoIds.Contains(listing.IPO_id))
                .ToDictionaryAsync(listing => listing.IPO_id);
            MarketDashboardViewModel market = await _stockMarketService.GetDashboardAsync();

            List<PortfolioHoldingViewModel> holdings = new();
            decimal realizedProfitLoss = 0m;
            foreach (Investment investment in investments)
            {
                List<InvestmentTransaction> investmentTransactions = transactions
                    .Where(transaction => transaction.Investment_id == investment.Investment_id)
                    .ToList();
                int soldQuantity = investmentTransactions
                    .Where(transaction => string.Equals(transaction.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase))
                    .Sum(transaction => transaction.Quantity);
                int quantity = Math.Max(0, investment.Quantity - soldQuantity);
                decimal averageBuyPrice = investment.Buy_price ?? 0m;
                realizedProfitLoss += investmentTransactions
                    .Where(transaction => string.Equals(transaction.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase))
                    .Sum(transaction => transaction.Quantity * (transaction.Price ?? 0m - averageBuyPrice));
                if (quantity == 0 || !ipos.TryGetValue(investment.IPO_id, out IPO? ipo))
                {
                    continue;
                }

                listings.TryGetValue(investment.IPO_id, out IPOListing? listing);
                decimal currentPrice = market.Listings.FirstOrDefault(row => row.IpoId == investment.IPO_id)?.CurrentPrice
                    ?? investment.Current_price
                    ?? listing?.NSE_closing_price
                    ?? listing?.NSE_listing_price
                    ?? listing?.BSE_closing_price
                    ?? listing?.BSE_listing_price
                    ?? ipo.Price_max
                    ?? ipo.Price_min
                    ?? averageBuyPrice;
                decimal investedAmount = quantity * averageBuyPrice;
                decimal currentValue = quantity * currentPrice;
                var marketRow = market.Listings.FirstOrDefault(row => row.IpoId == investment.IPO_id);
                holdings.Add(new PortfolioHoldingViewModel
                {
                    InvestmentId = investment.Investment_id,
                    IpoId = ipo.IPO_id,
                    IpoName = ipo.IPO_name,
                    CompanyName = companies.GetValueOrDefault(ipo.Company_id, "Company"),
                    StockSymbol = marketRow?.StockSymbol ?? listing?.StockSymbol ?? "STOCK",
                    Exchange = "NSE",
                    Quantity = quantity,
                    AverageBuyPrice = averageBuyPrice,
                    CurrentPrice = currentPrice,
                    NsePrice = marketRow?.NsePrice ?? currentPrice,
                    BsePrice = marketRow?.BsePrice ?? currentPrice,
                    InvestedAmount = investedAmount,
                    CurrentValue = currentValue,
                    UnrealizedProfitLoss = currentValue - investedAmount
                });
            }

            decimal totalInvested = holdings.Sum(holding => holding.InvestedAmount);
            decimal totalCurrentValue = holdings.Sum(holding => holding.CurrentValue);
            decimal unrealizedProfitLoss = holdings.Sum(holding => holding.UnrealizedProfitLoss);
            decimal totalReturn = realizedProfitLoss + unrealizedProfitLoss;
            foreach (PortfolioHoldingViewModel holding in holdings)
            {
                holding.AllocationPercentage = totalCurrentValue == 0m ? 0m : holding.CurrentValue / totalCurrentValue * 100m;
            }

            List<PortfolioTransactionViewModel> transactionRows = transactions
                .Where(transaction => ipos.ContainsKey(investments.FirstOrDefault(investment => investment.Investment_id == transaction.Investment_id)?.IPO_id ?? 0))
                .Select(transaction =>
                {
                    int ipoId = investments.First(investment => investment.Investment_id == transaction.Investment_id).IPO_id;
                    return new PortfolioTransactionViewModel
                    {
                        IpoName = ipos[ipoId].IPO_name,
                        TransactionType = transaction.Transaction_type ?? "TRADE",
                        Exchange = "NSE",
                        Quantity = transaction.Quantity,
                        Price = transaction.Price ?? 0m,
                        TotalAmount = transaction.Total_amount ?? transaction.Quantity * (transaction.Price ?? 0m),
                        TransactionDate = transaction.Transaction_date
                    };
                }).ToList();

            List<PortfolioAllocationViewModel> allocation = holdings
                .GroupBy(holding => holding.CompanyName)
                .Select(group => new PortfolioAllocationViewModel
                {
                    Label = group.Key,
                    Value = group.Sum(holding => holding.CurrentValue),
                    Percentage = totalCurrentValue == 0m ? 0m : group.Sum(holding => holding.CurrentValue) / totalCurrentValue * 100m
                })
                .OrderByDescending(item => item.Value)
                .ToList();
            decimal runningCashFlow = 0m;
            List<PortfolioPerformancePoint> performance = transactions
                .OrderBy(transaction => transaction.Transaction_date)
                .Select(transaction =>
                {
                    runningCashFlow += string.Equals(transaction.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase)
                        ? transaction.Total_amount ?? 0m
                        : -(transaction.Total_amount ?? 0m);
                    return new PortfolioPerformancePoint
                    {
                        Label = transaction.Transaction_date.ToLocalTime().ToString("dd MMM"),
                        Value = runningCashFlow
                    };
                }).ToList();

            User? investorUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == userId);

            return new PortfolioViewModel
            {
                InvestedAmount = totalInvested,
                CurrentValue = totalCurrentValue,
                RealizedProfitLoss = realizedProfitLoss,
                UnrealizedProfitLoss = unrealizedProfitLoss,
                TotalReturn = totalReturn,
                ReturnPercentage = totalInvested == 0m ? 0m : totalReturn / totalInvested * 100m,
                SimulatedCashBalance = investorUser?.SimulatedCashBalance ?? 100000m,
                Holdings = holdings,
                Transactions = transactionRows,
                Allocation = allocation,
                Performance = performance,
                Market = market
            };
        }

        private static string Csv(string value)
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        private async Task<List<CompanyIpoDetailsViewModel>> LoadIpoDetailsAsync(List<IPO> ipos)
        {
            int[] ipoIds = ipos.Select(ipo => ipo.IPO_id).ToArray();
            var applicationCounts = await _context.IPOApplications
                .Where(application => ipoIds.Contains(application.IPO_id))
                .GroupBy(application => application.IPO_id)
                .Select(group => new { IpoId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.IpoId, item => item.Count);
            var financials = await _context.IPOFinancials
                .AsNoTracking()
                .Where(financial => ipoIds.Contains(financial.IPO_id))
                .OrderByDescending(financial => financial.Financial_year)
                .ToListAsync();
            var analyses = await _context.IPOAnalyses
                .AsNoTracking()
                .Where(analysis => ipoIds.Contains(analysis.IPO_id))
                .OrderByDescending(analysis => analysis.Updated_at)
                .ToListAsync();
            var listings = await _context.IPOListings
                .AsNoTracking()
                .Where(listing => ipoIds.Contains(listing.IPO_id))
                .ToListAsync();
            var gmpHistories = await _context.IPOGMPHistories
                .AsNoTracking()
                .Where(g => ipoIds.Contains(g.IPO_id))
                .OrderBy(g => g.RecordedDate)
                .ThenBy(g => g.Gmp_id)
                .ToListAsync();
            Dictionary<int, Company> companies = await _context.Companies
                .AsNoTracking()
                .Where(company => ipos.Select(ipo => ipo.Company_id).Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id);

            return ipos.Select(ipo => new CompanyIpoDetailsViewModel
            {
                Ipo = ipo,
                Company = companies.GetValueOrDefault(ipo.Company_id),
                ApplicationCount = applicationCounts.GetValueOrDefault(ipo.IPO_id),
                Financial = financials.FirstOrDefault(financial => financial.IPO_id == ipo.IPO_id),
                Analysis = analyses.FirstOrDefault(analysis => analysis.IPO_id == ipo.IPO_id),
                Listing = listings.FirstOrDefault(listing => listing.IPO_id == ipo.IPO_id),
                GmpHistory = gmpHistories.Where(g => g.IPO_id == ipo.IPO_id).ToList()
            }).Select(detail =>
            {
                IPOFinancial? latestFinancial = detail.Financial;
                IPOFinancial? previousFinancial = financials
                    .Where(financial => financial.IPO_id == detail.Ipo.IPO_id &&
                        latestFinancial is not null &&
                        financial.Financial_year < latestFinancial.Financial_year)
                    .OrderByDescending(financial => financial.Financial_year)
                    .FirstOrDefault();
                decimal? listingPrice = detail.Listing?.NSE_listing_price ?? detail.Listing?.BSE_listing_price;
                decimal? listingPerformance = detail.Listing?.Listing_gain_percentage;
                if (!listingPerformance.HasValue &&
                    detail.Listing?.Issue_price > 0 &&
                    listingPrice.HasValue)
                {
                    listingPerformance = (listingPrice.Value - detail.Listing.Issue_price.Value) /
                        detail.Listing.Issue_price.Value * 100m;
                }

                detail.ComparisonRoe = detail.Analysis?.ROE ??
                    (latestFinancial?.Profit.HasValue == true &&
                     latestFinancial.Net_worth.HasValue &&
                     latestFinancial.Net_worth.Value > 0m
                        ? latestFinancial.Profit.Value / latestFinancial.Net_worth.Value * 100m
                        : null);
                detail.ComparisonRevenueGrowth = detail.Analysis?.Revenue_growth ??
                    CalculateGrowth(latestFinancial?.Revenue, previousFinancial?.Revenue);
                detail.ComparisonProfitGrowth = detail.Analysis?.Profit_growth ??
                    CalculateGrowth(latestFinancial?.Profit, previousFinancial?.Profit);
                detail.ComparisonListingPerformance = listingPerformance;
                return detail;
            }).ToList();
        }

        private static decimal? CalculateGrowth(decimal? current, decimal? previous)
        {
            return current.HasValue && previous.HasValue && previous.Value != 0m
                ? (current.Value - previous.Value) / previous.Value * 100m
                : null;
        }

        private async Task LoadAdminDashboardAsync(
            DashboardViewModel model,
            string? adminIpoQuery = null,
            string? adminIpoStatus = null,
            string? adminIpoType = null)
        {
            model.UserCount = await _context.Users.CountAsync(user =>
                user.Is_active &&
                user.Role == "Investor");
            model.CompanyCount = await _context.Companies.CountAsync();
            model.IpoCount = await _context.IPOs.CountAsync();
            model.PendingCompanyCount = await _context.Companies.CountAsync(company => company.Approval_status == "Pending");
            model.PendingApplicationCount = await _context.IPOApplications.CountAsync(application =>
                application.Status == "Submitted");
            model.PendingIpoCount = await _context.IPOs.CountAsync(ipo => ipo.Status == "Pending");
            DateTime today = DateTime.UtcNow.Date;
            model.ActiveIpoCount = await _context.IPOs.CountAsync(ipo =>
                (ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open") &&
                ipo.Open_date.Date <= today &&
                ipo.Close_date.Date >= today);
            model.ListedIpoCount = await _context.IPOs.CountAsync(ipo =>
                ipo.Status == "Listed" ||
                (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= today));
            model.PendingIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Pending")
                .OrderBy(ipo => ipo.Created_at)
                .Take(10)
                .ToListAsync();
            int[] pendingIpoCompanyIds = model.PendingIpos.Select(ipo => ipo.Company_id).Distinct().ToArray();
            model.PendingIpoCompanyNames = await _context.Companies
                .AsNoTracking()
                .Where(company => pendingIpoCompanyIds.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
            model.ApprovedIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Approved")
                .OrderBy(ipo => ipo.Created_at)
                .Take(10)
                .ToListAsync();
            int[] approvedIpoCompanyIds = model.ApprovedIpos.Select(ipo => ipo.Company_id).Distinct().ToArray();
            model.ApprovedIpoCompanyNames = await _context.Companies
                .AsNoTracking()
                .Where(company => approvedIpoCompanyIds.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
            model.PendingApplications = await _context.IPOApplications
                .AsNoTracking()
                .Where(application => application.Status == "Submitted")
                .OrderBy(application => application.Application_date)
                .Take(10)
                .ToListAsync();
            int[] pendingApplicationIpoIds = model.PendingApplications.Select(application => application.IPO_id).Distinct().ToArray();
            int[] pendingApplicationUserIds = model.PendingApplications.Select(application => application.User_id).Distinct().ToArray();
            model.PendingApplicationIpoNames = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => pendingApplicationIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id, ipo => ipo.IPO_name);
            model.PendingApplicationUserNames = await _context.Users
                .AsNoTracking()
                .Where(user => pendingApplicationUserIds.Contains(user.User_id))
                .ToDictionaryAsync(user => user.User_id, user => $"{user.First_name} {user.Last_name}".Trim());
            model.PendingCompanies = await _context.Companies
                .AsNoTracking()
                .Where(company => company.Approval_status == "Pending")
                .OrderBy(company => company.Created_at)
                .Take(10)
                .ToListAsync();
            model.RecentInvestors = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor")
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToListAsync();
            model.RecentCompanies = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Company")
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToListAsync();
            model.RecentUsers = model.RecentInvestors
                .Concat(model.RecentCompanies)
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToList();
            model.RecentApplications = await _context.IPOApplications
                .AsNoTracking()
                .OrderByDescending(application => application.Application_date)
                .Take(5)
                .ToListAsync();
            List<IPO> subscriptionIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Total_shares.HasValue && ipo.Total_shares.Value > 0)
                .OrderByDescending(ipo => ipo.Close_date)
                .Take(10)
                .ToListAsync();
            int[] subscriptionIpoIds = subscriptionIpos.Select(ipo => ipo.IPO_id).ToArray();
            List<IPOApplication> subscriptionApplications = await _context.IPOApplications
                .AsNoTracking()
                .Where(application => subscriptionIpoIds.Contains(application.IPO_id))
                .ToListAsync();
            model.SubscriptionStatistics = subscriptionIpos
                .Select(ipo =>
                {
                    List<IPOApplication> applications = subscriptionApplications
                        .Where(application =>
                            application.IPO_id == ipo.IPO_id &&
                            !string.Equals(application.Status, "Rejected", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(application.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    long appliedShares = applications.Sum(application => (long)application.Applied_shares);
                    long offeredShares = ipo.Total_shares ?? 0L;
                    return new AdminSubscriptionStatisticsViewModel
                    {
                        Ipo = ipo,
                        OfferedShares = offeredShares,
                        AppliedShares = appliedShares,
                        ApplicationCount = applications.Count,
                        SubscriptionMultiple = offeredShares == 0 ? 0m : (decimal)appliedShares / offeredShares
                    };
                })
                .ToList();
            // All IPOs for Admin Directory & Search
            var allIposQuery = _context.IPOs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(adminIpoStatus))
            {
                allIposQuery = allIposQuery.Where(i => i.Status == adminIpoStatus);
            }
            if (!string.IsNullOrWhiteSpace(adminIpoType))
            {
                allIposQuery = allIposQuery.Where(i => i.IPO_type == adminIpoType);
            }
            var allIposList = await allIposQuery.OrderByDescending(i => i.Created_at).ToListAsync();

            int[] allIpoIds = allIposList.Select(i => i.IPO_id).ToArray();
            int[] allCompanyIds = allIposList.Select(i => i.Company_id).Distinct().ToArray();

            var allCompanyNames = await _context.Companies
                .AsNoTracking()
                .Where(c => allCompanyIds.Contains(c.Company_id))
                .ToDictionaryAsync(c => c.Company_id, c => c.Company_name);

            var ipoAppCounts = await _context.IPOApplications
                .AsNoTracking()
                .Where(a => allIpoIds.Contains(a.IPO_id))
                .GroupBy(a => a.IPO_id)
                .Select(g => new { IpoId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.IpoId, g => g.Count);

            var latestGmpQuotes = await _context.IPOGMPHistories
                .AsNoTracking()
                .Where(g => allIpoIds.Contains(g.IPO_id))
                .OrderBy(g => g.RecordedDate)
                .ThenBy(g => g.Gmp_id)
                .ToListAsync();

            var latestGmpMap = latestGmpQuotes
                .GroupBy(g => g.IPO_id)
                .ToDictionary(g => g.Key, g => g.Last());

            DateTime todayDate = GetIndiaStandardTime().Date;

            model.AllAdminIpos = allIposList.Select(ipo =>
            {
                string compName = allCompanyNames.GetValueOrDefault(ipo.Company_id, "Company");
                var gmp = latestGmpMap.GetValueOrDefault(ipo.IPO_id);
                decimal basePrice = ipo.Price_max ?? ipo.Price_min ?? 0m;
                decimal? currentGmp = gmp?.GMPPrice;
                decimal? estPrice = gmp?.EstimatedListingPrice ?? (currentGmp.HasValue ? basePrice + currentGmp.Value : (decimal?)null);
                decimal? gainPct = gmp?.ExpectedListingGainPercentage ?? (basePrice > 0 && currentGmp.HasValue ? Math.Round((currentGmp.Value / basePrice) * 100m, 2) : (decimal?)null);

                var timing = CheckAllotmentTiming(ipo, todayDate);

                return new AdminSearchIpoItem
                {
                    Ipo = ipo,
                    CompanyName = compName,
                    ApplicationCount = ipoAppCounts.GetValueOrDefault(ipo.IPO_id, 0),
                    CurrentGmp = currentGmp,
                    EstimatedListingPrice = estPrice,
                    ExpectedGainPercentage = gainPct,
                    AllotmentWindowOpen = timing.AllotmentWindowOpen
                };
            }).ToList();

            if (!string.IsNullOrWhiteSpace(adminIpoQuery))
            {
                string q = adminIpoQuery.Trim().ToLower();
                model.AllAdminIpos = model.AllAdminIpos
                    .Where(item => item.Ipo.IPO_name.ToLower().Contains(q) || item.CompanyName.ToLower().Contains(q))
                    .ToList();
            }

            model.AdminIpoSearchQuery = adminIpoQuery;
            model.AdminIpoSearchStatus = adminIpoStatus;
            model.AdminIpoSearchType = adminIpoType;

            await LoadMarketDataAsync(model);
        }

        private bool IsAdmin()
        {
            return string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsInvestor()
        {
            return string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase);
        }


        private static DateTime GetIndiaStandardTime()
        {
            TimeZoneInfo indiaTimeZone;
            try
            {
                indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch (InvalidTimeZoneException)
            {
                indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }

            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, indiaTimeZone);
        }

        private async Task<CreateNotificationViewModel> BuildNotificationModelAsync(CreateNotificationViewModel model)
        {
            model.Investors = await _context.Users.AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .OrderBy(user => user.First_name).ThenBy(user => user.Last_name).ToListAsync();
            model.Companies = await _context.Users.AsNoTracking()
                .Where(user => user.Role == "Company" && user.Is_active)
                .OrderBy(user => user.First_name).ThenBy(user => user.Last_name).ToListAsync();
            model.CompanyProfiles = await _context.Companies.AsNoTracking()
                .OrderBy(company => company.Company_name).ToListAsync();
            return model;
        }

        private async Task LoadCompanyDashboardAsync(DashboardViewModel model, int userId)
        {
            model.Notifications = await LoadNotificationsAsync(userId);
            model.Companies = await _context.Companies
                .AsNoTracking()
                .Where(company => company.User_id == userId)
                .OrderByDescending(company => company.Created_at)
                .ToListAsync();

            int[] companyIds = model.Companies.Select(company => company.Company_id).ToArray();
            model.CompanyIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => companyIds.Contains(ipo.Company_id))
                .OrderByDescending(ipo => ipo.Created_at)
                .ToListAsync();
            int[] ipoIds = model.CompanyIpos.Select(ipo => ipo.IPO_id).ToArray();
            var applicationCounts = await _context.IPOApplications
                .Where(application => ipoIds.Contains(application.IPO_id))
                .GroupBy(application => application.IPO_id)
                .Select(group => new { IpoId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.IpoId, item => item.Count);
            var financials = await _context.IPOFinancials
                .AsNoTracking()
                .Where(financial => ipoIds.Contains(financial.IPO_id))
                .OrderByDescending(financial => financial.Financial_year)
                .ToListAsync();
            var analyses = await _context.IPOAnalyses
                .AsNoTracking()
                .Where(analysis => ipoIds.Contains(analysis.IPO_id))
                .OrderByDescending(analysis => analysis.Updated_at)
                .ToListAsync();
            var listings = await _context.IPOListings
                .AsNoTracking()
                .Where(listing => ipoIds.Contains(listing.IPO_id))
                .ToListAsync();
            model.CompanyIpoDetails = model.CompanyIpos.Select(ipo => new CompanyIpoDetailsViewModel
            {
                Ipo = ipo,
                ApplicationCount = applicationCounts.GetValueOrDefault(ipo.IPO_id),
                Financial = financials.FirstOrDefault(financial => financial.IPO_id == ipo.IPO_id),
                Analysis = analyses.FirstOrDefault(analysis => analysis.IPO_id == ipo.IPO_id),
                Listing = listings.FirstOrDefault(listing => listing.IPO_id == ipo.IPO_id)
            }).ToList();
            model.CompanyCount = model.Companies.Count;
            model.IpoCount = model.CompanyIpos.Count;
            DateTime today = DateTime.UtcNow.Date;
            model.TotalCompanyIpoCount = model.CompanyIpos.Count;
            model.ActiveCompanyIpoCount = model.CompanyIpos.Count(ipo => ipo.Open_date.Date <= today && ipo.Close_date.Date >= today);
            model.TotalCompanyIssueSize = model.CompanyIpos.Sum(ipo => ipo.Issue_size ?? 0m);
            model.ListedCompanyIpoCount = model.CompanyIpos.Count(ipo =>
                (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= today) ||
                string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase));
            model.CompanyApplicationCount = await _context.IPOApplications
                .CountAsync(application => ipoIds.Contains(application.IPO_id));
            model.PendingCompanyCount = model.Companies.Count(company => company.Approval_status == "Pending");
            model.PendingIpoCount = model.CompanyIpos.Count(ipo => ipo.Status == "Pending");
            model.CompanyNotificationCount = model.PendingCompanyCount + model.PendingIpoCount;
            await LoadMarketDataAsync(model);
        }

        private async Task LoadMarketDataAsync(DashboardViewModel model)
        {
            model.Market = await _stockMarketService.GetDashboardAsync();
            List<IPO> publishedIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
                .OrderByDescending(ipo => ipo.Created_at)
                .ToListAsync();

            model.PublishedIpoDetails = await LoadIpoDetailsAsync(publishedIpos);
            DateTime today = DateTime.UtcNow.Date;
            model.MarketStatuses = publishedIpos.ToDictionary(
                ipo => ipo.IPO_id,
                ipo => GetInvestorMarketStatus(ipo, today));
            await LoadCompanyNamesAsync(model, publishedIpos.Select(ipo => ipo.Company_id));
        }

        private async Task LoadCompanyNamesAsync(DashboardViewModel model, IEnumerable<int> companyIds)
        {
            int[] ids = companyIds.Distinct().ToArray();
            model.CompanyNames = await _context.Companies
                .AsNoTracking()
                .Where(company => ids.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
        }

        private async Task<List<SystemNotification>> LoadNotificationsAsync(int userId)
        {
            return await _context.SystemNotifications
                .AsNoTracking()
                .Where(notification => notification.User_id == userId)
                .OrderByDescending(notification => notification.Created_at)
                .Take(5)
                .ToListAsync();
        }

        private async Task NotifyCompanyIpoApprovedAsync(IPO ipo)
        {
            User? companyUser = await _context.Companies
                .Where(company => company.Company_id == ipo.Company_id)
                .Join(_context.Users, company => company.User_id, user => user.User_id, (_, user) => user)
                .FirstOrDefaultAsync();
            if (companyUser is null)
            {
                return;
            }

            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = companyUser.User_id,
                Title = "IPO Approved",
                Message = $"{ipo.IPO_name} has been approved and can now be published.",
                Created_at = DateTime.UtcNow,
                Is_read = false
            });
            await _context.SaveChangesAsync();
            await TrySendEventEmailAsync(
                companyUser,
                "IPO Approved",
                $"{ipo.IPO_name} has been approved and can now be published.");
        }

        private async Task NotifyInvestorsIpoAvailableAsync(IPO ipo)
        {
            User[] investors = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .ToArrayAsync();
            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(investors.Select(investor => new SystemNotification
            {
                User_id = investor.User_id,
                Title = "IPO Available",
                Message = $"{ipo.IPO_name} is now available to investors.",
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
            foreach (User investor in investors)
            {
                await TrySendEventEmailAsync(
                    investor,
                    "IPO Available",
                    $"{ipo.IPO_name} is now available to investors. Log in to review the offering.");
            }
        }

        private async Task TrySendEventEmailAsync(User user, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return;
            }

            try
            {
                await _emailNotificationService.SendAsync(
                    user.Email,
                    $"{user.First_name} {user.Last_name}".Trim(),
                    subject,
                    body);
            }
            catch (SmtpException exception)
            {
                TempData["AdminMessage"] = $"The database update succeeded, but the email to {user.Email} could not be sent: {exception.Message}";
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = $"The database update succeeded, but email notifications are not configured: {exception.Message}";
            }
            catch (FormatException exception)
            {
                TempData["AdminMessage"] = $"The database update succeeded, but the email address is invalid: {exception.Message}";
            }
        }
    }
}