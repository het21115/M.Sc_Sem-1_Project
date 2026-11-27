using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using IPOInvestmentManagement.Services;
using Microsoft.AspNetCore.Http.Features;
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
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentPortfolioTrades = new();
        private readonly ApplicationDbContext _context;
        private readonly IEmailNotificationService _emailNotificationService;
        private readonly IRazorpayPaymentService _razorpayPaymentService;
        private readonly IStockMarketService _stockMarketService;
        private readonly IHubContext<MarketHub> _hubContext;
        private readonly IInvestorReportService _investorReportService;
        private readonly IIpoClock _clock;
        private readonly IIpoStatusService _ipoStatusService;
        private readonly IPaymentReconciliationService _paymentReconciliationService;
        private readonly IStockSipService _stockSipService;
        private readonly IWalletWithdrawalService _walletWithdrawalService;
        private readonly IConfiguration _configuration;

        public DashboardController(
            ApplicationDbContext context,
            IEmailNotificationService emailNotificationService,
            IRazorpayPaymentService razorpayPaymentService,
            IStockMarketService stockMarketService,
            IHubContext<MarketHub> hubContext,
            IInvestorReportService investorReportService,
            IIpoClock clock,
            IIpoStatusService ipoStatusService,
            IPaymentReconciliationService paymentReconciliationService,
            IStockSipService stockSipService,
            IWalletWithdrawalService walletWithdrawalService,
            IConfiguration configuration)
        {
            _context = context;
            _emailNotificationService = emailNotificationService;
            _razorpayPaymentService = razorpayPaymentService;
            _stockMarketService = stockMarketService;
            _hubContext = hubContext;
            _investorReportService = investorReportService;
            _clock = clock;
            _ipoStatusService = ipoStatusService;
            _paymentReconciliationService = paymentReconciliationService;
            _stockSipService = stockSipService;
            _walletWithdrawalService = walletWithdrawalService;
            _configuration = configuration;
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
            if (ipo is null)
            {
                TempData["InvestorMessage"] = "Selected IPO was not found.";
                return RedirectToAction(nameof(Index));
            }

            DateTime indiaNow = GetIndiaStandardTime();
            var (canApply, applyError) = _ipoStatusService.CanApply(ipo, indiaNow);
            if (!canApply)
            {
                ModelState.AddModelError(string.Empty, applyError ?? "This IPO is not currently open for applications.");
            }

            if (request.AppliedLots <= 0 || !ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0)
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
            if (investor.SimulatedCashBalance < applicationAmount)
            {
                TempData["InvestorError"] = $"Insufficient virtual wallet balance to apply for {ipo.IPO_name}. Required ₹{applicationAmount:N2}, available ₹{investor.SimulatedCashBalance:N2}.";
                return RedirectToAction(nameof(Index));
            }

            var pendingApplication = new IpoPaymentViewModel
            {
                ApplicationNumber = $"IPO-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".ToUpperInvariant()[..28],
                InvestorName = $"{investor.First_name} {investor.Last_name}".Trim(),
                IpoName = ipo.IPO_name,
                BidPrice = request.BidPrice,
                AppliedLots = request.AppliedLots,
                AppliedShares = appliedShares,
                ApplicationAmount = applicationAmount,
                WalletBalance = investor.SimulatedCashBalance,
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

            int currentUserId = document.RootElement.GetProperty("UserId").GetInt32();
            int currentIpoId = document.RootElement.GetProperty("IpoId").GetInt32();
            User? currentInvestor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == currentUserId && u.Is_active);
            application.WalletBalance = currentInvestor?.SimulatedCashBalance ?? application.WalletBalance;
            application.IsSimulated = _razorpayPaymentService.IsTestMode;
            application.PaymentMode = _razorpayPaymentService.IsTestMode ? "SIMULATED_MANDATE" : "RAZORPAY_GATEWAY";
            application.RazorpayKeyId = _razorpayPaymentService.KeyId;

            if (string.IsNullOrWhiteSpace(application.RazorpayOrderId))
            {
                try
                {
                    // Prevent duplicate order creation by looking for an active, uncaptured order created in last 15 minutes
                    DateTime recentWindow = DateTime.UtcNow.AddMinutes(-15);
                    PaymentRecord? existingActiveRecord = await _context.PaymentRecords
                        .Where(record => record.UserId == currentUserId &&
                                         record.IpoId == currentIpoId &&
                                         record.Amount == application.ApplicationAmount &&
                                         record.Status == "Created" &&
                                         record.CreatedAt >= recentWindow)
                        .OrderByDescending(record => record.CreatedAt)
                        .FirstOrDefaultAsync();

                    if (existingActiveRecord != null && !string.IsNullOrWhiteSpace(existingActiveRecord.RazorpayOrderId))
                    {
                        application.RazorpayOrderId = existingActiveRecord.RazorpayOrderId;
                    }
                    else
                    {
                        application.RazorpayOrderId = await _razorpayPaymentService.CreateOrderAsync(
                            application.ApplicationAmount,
                            $"{application.ApplicationNumber}-{Guid.NewGuid():N}"[..40]);

                        DateTime now = DateTime.UtcNow;
                        _context.PaymentRecords.Add(new PaymentRecord
                        {
                            UserId = currentUserId,
                            IpoId = currentIpoId,
                            AppliedLots = application.AppliedLots,
                            AppliedShares = application.AppliedShares,
                            BidPrice = application.BidPrice,
                            ApplicationNumber = application.ApplicationNumber,
                            RazorpayOrderId = application.RazorpayOrderId,
                            Amount = application.ApplicationAmount,
                            Status = "Created",
                            PaymentMode = application.PaymentMode,
                            IsSimulated = application.IsSimulated,
                            CreatedAt = now,
                            UpdatedAt = now
                        });
                        await _context.SaveChangesAsync();
                    }

                    HttpContext.Session.SetString("PendingIpoApplication", JsonSerializer.Serialize(new
                    {
                        UserId = currentUserId,
                        IpoId = currentIpoId,
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

            // Duplicate prevention: If payment record is already captured, verify if application already exists
            if (string.Equals(paymentRecord.Status, "Captured", StringComparison.OrdinalIgnoreCase))
            {
                bool existingApp = await _context.IPOApplications.AnyAsync(a =>
                    a.Razorpay_order_id == razorpayOrderId || a.Razorpay_payment_id == razorpayPaymentId);
                if (existingApp)
                {
                    HttpContext.Session.Remove("PendingIpoApplication");
                    TempData["InvestorMessage"] = $"Application {pending.ApplicationNumber} has already been verified and confirmed.";
                    return RedirectToAction(nameof(Index));
                }
            }

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == ipoId);
            if (ipo is null)
            {
                HttpContext.Session.Remove("PendingIpoApplication");
                TempData["InvestorMessage"] = "Selected IPO was not found.";
                return RedirectToAction(nameof(Index));
            }

            DateTime indiaNow = GetIndiaStandardTime();
            var (canApply, applyError) = _ipoStatusService.CanApply(ipo, indiaNow);
            if (!canApply)
            {
                HttpContext.Session.Remove("PendingIpoApplication");
                TempData["InvestorMessage"] = applyError ?? "This IPO is no longer open for applications.";
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

            User? payingInvestor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == userId.Value && u.Is_active);
            if (payingInvestor is null || payingInvestor.SimulatedCashBalance < payment.Amount)
            {
                await transaction.RollbackAsync();
                HttpContext.Session.Remove("PendingIpoApplication");
                TempData["InvestorError"] = $"Insufficient virtual wallet balance to submit application for {ipo.IPO_name}. Required ₹{payment.Amount:N2}, available ₹{payingInvestor?.SimulatedCashBalance ?? 0m:N2}.";
                return RedirectToAction(nameof(Index));
            }

            decimal walletBalanceBefore = payingInvestor.SimulatedCashBalance;
            payingInvestor.SimulatedCashBalance = walletBalanceBefore - payment.Amount;
            if (payingInvestor.SimulatedCashBalance < 0m)
            {
                throw new InvalidOperationException("Negative wallet balance prevented.");
            }

            _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
            {
                UserId = userId.Value,
                AdminUserId = null,
                TransactionType = "IPO_APPLICATION",
                Amount = -payment.Amount,
                BalanceBefore = walletBalanceBefore,
                BalanceAfter = payingInvestor.SimulatedCashBalance,
                Reason = $"IPO application #{pending.ApplicationNumber} for {ipo.IPO_name} ({pending.AppliedLots} lot(s), {pending.AppliedShares} shares @ ₹{pending.BidPrice:N2})",
                CreatedAt = indiaNow
            });

            string receiptNumber = $"RCP-{indiaNow:yyyyMMdd}-{paymentRecord.PaymentRecordId:D5}";

            paymentRecord.RazorpayPaymentId = payment.PaymentId;
            paymentRecord.Amount = payment.Amount;
            paymentRecord.Status = "Captured";
            paymentRecord.ReceiptNumber = receiptNumber;
            paymentRecord.PaymentMode = _razorpayPaymentService.IsTestMode ? "SIMULATED_MANDATE" : "RAZORPAY_GATEWAY";
            paymentRecord.IsSimulated = _razorpayPaymentService.IsTestMode;
            paymentRecord.ReconciliationStatus = "Confirmed";
            paymentRecord.ReconciledAt = indiaNow;
            paymentRecord.UpdatedAt = indiaNow;

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
                Payment_mode = paymentRecord.PaymentMode,
                Is_simulated_payment = paymentRecord.IsSimulated,
                Receipt_number = receiptNumber,
                Application_date = indiaNow,
                Status = "Submitted"
            });
            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = userId.Value,
                Title = $"Payment Confirmed - {ipo.IPO_name} IPO",
                Message = $"Payment of ₹{payment.Amount:N2} (Txn: {payment.PaymentId}) verified and debited from your wallet for application #{pending.ApplicationNumber}. Receipt: {receiptNumber}. Remaining wallet balance: ₹{payingInvestor.SimulatedCashBalance:N2}.",
                Created_at = indiaNow,
                Is_read = false
            });
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            if (payingInvestor != null)
            {
                string workflowLabel = _razorpayPaymentService.IsTestMode
                    ? "Simulated UPI ASBA Mandate (Testnet Sandbox)"
                    : "Genuine Payment Gateway (Production)";
                string emailSubject = $"Payment Receipt & Application Confirmed: {ipo.IPO_name}";
                string emailBody = $"Dear {payingInvestor.First_name},\n\nYour payment for {ipo.IPO_name} IPO application #{pending.ApplicationNumber} has been verified and captured successfully.\n\nTransaction Summary:\n- Receipt Number: {receiptNumber}\n- Application Number: {pending.ApplicationNumber}\n- IPO Name: {ipo.IPO_name}\n- Applied Lots: {pending.AppliedLots}\n- Applied Shares: {pending.AppliedShares}\n- Bid Price: ₹{pending.BidPrice:N2}\n- Total Amount: ₹{payment.Amount:N2}\n- Payment Transaction ID: {payment.PaymentId}\n- Razorpay Order ID: {payment.OrderId}\n- Payment Workflow: {workflowLabel}\n- UPI ID: {pending.UpiId}\n- Demat Account: {pending.DematAccountNumber}\n- Payment Status: Verified (Captured)\n- Application Status: Submitted\n\nYou can view and download your official payment receipt from your Investor Dashboard.\n\nThank you,\nIPO Investment Management";
                await TrySendEventEmailAsync(payingInvestor, emailSubject, emailBody);
            }

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                {
                    category = "PAYMENT CONFIRMED",
                    title = $"Payment Confirmed - {ipo.IPO_name}",
                    message = $"Application #{pending.ApplicationNumber} submitted successfully with verified payment of ₹{payment.Amount:N2}.",
                    timestamp = indiaNow.ToString("hh:mm tt")
                });
            }
            catch { }

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
                cancellationDate >= _ipoStatusService.GetClosingDateTime(ipo) ||
                !string.Equals(application.Status, "Submitted", StringComparison.OrdinalIgnoreCase))
            {
                TempData["InvestorMessage"] = "Applications can only be cancelled before the IPO closes and before allotment.";
                return RedirectToAction(nameof(InvestorApplicationDetails), new { applicationId = model.ApplicationId });
            }

            application.Status = "Cancelled";
            application.Cancellation_admin_id = null;
            application.Cancellation_reason = model.Reason.Trim();
            application.Cancellation_date = cancellationDate;

            PaymentRecord? paymentRecord = await _context.PaymentRecords
                .FirstOrDefaultAsync(record => record.RazorpayOrderId == application.Razorpay_order_id);
            if (paymentRecord != null)
            {
                paymentRecord.UnblockedAmount = application.Payment_amount ?? 0m;
                paymentRecord.RefundStatus = "Pending Unblock";
                paymentRecord.RefundReferenceId = $"UNBLK-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpper();
                paymentRecord.UpdatedAt = DateTime.UtcNow;
            }

            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = userId.Value,
                Title = "IPO Application Cancelled & Refund Initiated",
                Message = $"Your IPO application #{application.Application_id} was cancelled. Full amount of ₹{application.Payment_amount:N2} has been marked for unblocking to your UPI ID {application.Upi_id}. Refund Ref: {paymentRecord?.RefundReferenceId ?? "N/A"}.",
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

            string eventId = string.Empty;
            string eventName = "unknown";
            string? orderId = null;
            string? paymentId = null;

            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                eventId = document.RootElement.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                eventName = document.RootElement.TryGetProperty("event", out var evElem) ? evElem.GetString() ?? string.Empty : string.Empty;

                if (document.RootElement.TryGetProperty("payload", out var payloadObj) &&
                    payloadObj.TryGetProperty("payment", out var pObj) &&
                    pObj.TryGetProperty("entity", out var pEnt))
                {
                    orderId = pEnt.TryGetProperty("order_id", out var oElem) ? oElem.GetString() : null;
                    paymentId = pEnt.TryGetProperty("id", out var payElem) ? payElem.GetString() : null;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(eventId))
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                eventId = "evt_" + Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(payload)))[..16].ToLower();
            }

            if (string.IsNullOrWhiteSpace(signature) || !_razorpayPaymentService.VerifyWebhookSignature(payload, signature))
            {
                await _context.PaymentWebhookLogs.AddAsync(new PaymentWebhookLog
                {
                    EventId = eventId,
                    EventType = eventName,
                    RazorpayOrderId = orderId,
                    RazorpayPaymentId = paymentId,
                    Payload = payload,
                    Status = "SignatureFailed",
                    ErrorMessage = "Invalid or missing X-Razorpay-Signature header",
                    ReceivedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Unauthorized();
            }

            PaymentWebhookLog log = await _paymentReconciliationService.LogAndProcessWebhookAsync(eventId, eventName, orderId, paymentId, payload);
            if (log.Status == "Failed")
            {
                // Return 500 so Razorpay retries webhook delivery, and background reconciliation also handles retry
                return StatusCode(500, new { message = "Webhook processing failed; queued for retry.", eventId, status = log.Status });
            }

            return Ok(new { status = "received", eventId, logStatus = log.Status });
        }

        [HttpGet]
        public async Task<IActionResult> PaymentReceipt(int paymentRecordId)
        {
            int? currentUserId = HttpContext.Session.GetInt32("User_id");
            string? role = HttpContext.Session.GetString("UserRole");
            if (!currentUserId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            PaymentRecord? payment = await _context.PaymentRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PaymentRecordId == paymentRecordId);

            if (payment is null)
            {
                TempData["InvestorMessage"] = "Payment record was not found.";
                return RedirectToAction(nameof(Index));
            }

            bool isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            if (!isAdmin && payment.UserId != currentUserId.Value)
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == payment.IpoId);
            IPOApplication? application = await _context.IPOApplications.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Razorpay_order_id == payment.RazorpayOrderId);
            User? investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == payment.UserId);
            IPOListing? listing = await _context.IPOListings.AsNoTracking().FirstOrDefaultAsync(l => l.IPO_id == payment.IpoId);

            string receiptNumber = payment.ReceiptNumber ?? application?.Receipt_number ?? $"RCP-{payment.CreatedAt:yyyyMMdd}-{payment.PaymentRecordId:D5}";

            PaymentReceiptViewModel model = new()
            {
                ReceiptNumber = receiptNumber,
                PaymentRecordId = payment.PaymentRecordId,
                ApplicationId = application?.Application_id,
                ApplicationNumber = payment.ApplicationNumber,
                UserId = payment.UserId,
                InvestorName = $"{investor?.First_name} {investor?.Last_name}".Trim(),
                InvestorEmail = investor?.Email ?? "N/A",
                InvestorPhone = investor?.Phone ?? "N/A",
                DematAccountNumber = application?.Demat_account_number ?? investor?.DematAccountNumber ?? "N/A",
                UpiId = application?.Upi_id ?? investor?.UpiId ?? "N/A",
                IpoId = payment.IpoId,
                IpoName = ipo?.IPO_name ?? "IPO",
                CompanySymbol = listing?.StockSymbol ?? "N/A",
                CategoryName = "Retail Individual Investor (RII)",
                AppliedLots = payment.AppliedLots,
                AppliedShares = payment.AppliedShares,
                BidPrice = payment.BidPrice,
                TotalAmount = payment.Amount,
                AmountInWords = RupeeWordsHelper.ToWords(payment.Amount),
                RazorpayOrderId = payment.RazorpayOrderId,
                RazorpayPaymentId = payment.RazorpayPaymentId ?? application?.Razorpay_payment_id ?? application?.Payment_reference ?? "Pending Capture",
                PaymentStatus = payment.Status,
                PaymentDate = payment.CreatedAt,
                IsSimulated = payment.IsSimulated,
                PaymentMode = payment.PaymentMode,
                RefundStatus = payment.RefundStatus,
                UnblockedAmount = payment.UnblockedAmount,
                RefundReferenceId = payment.RefundReferenceId,
                RefundProcessedAt = payment.RefundProcessedAt,
                ReconciliationStatus = payment.ReconciliationStatus,
                ReconciledAt = payment.ReconciledAt
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> PaymentReceiptByApplication(int applicationId)
        {
            int? currentUserId = HttpContext.Session.GetInt32("User_id");
            string? role = HttpContext.Session.GetString("UserRole");
            if (!currentUserId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            IPOApplication? app = await _context.IPOApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Application_id == applicationId);

            if (app is null)
            {
                TempData["InvestorMessage"] = "Application record was not found.";
                return RedirectToAction(nameof(Index));
            }

            bool isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            if (!isAdmin && app.User_id != currentUserId.Value)
            {
                return Forbid();
            }

            PaymentRecord? payment = await _context.PaymentRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.RazorpayOrderId == app.Razorpay_order_id);

            if (payment is null)
            {
                TempData["InvestorMessage"] = "No payment transaction record found for this application.";
                return RedirectToAction(nameof(InvestorApplicationDetails), new { applicationId });
            }

            return RedirectToAction(nameof(PaymentReceipt), new { paymentRecordId = payment.PaymentRecordId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReconcilePayments(string? returnUrl = null)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            PaymentReconciliationReport report = await _paymentReconciliationService.ReconcileAllPaymentsAsync();
            TempData["AdminMessage"] = $"Reconciliation Completed: {report.ReconciledCaptured} payment(s) auto-captured, {report.ReconciledFailed} expired, {report.ApplicationsAutoCreated} missing application(s) healed, {report.ReconciledUnblocks} mandate unblock(s) synced.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction(nameof(Applications));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReconcilePaymentRecord(int paymentRecordId, string? returnUrl = null)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            bool reconciled = await _paymentReconciliationService.ReconcilePaymentRecordAsync(paymentRecordId);
            PaymentRecord? record = await _context.PaymentRecords.FindAsync(paymentRecordId);
            if (reconciled)
            {
                TempData["AdminMessage"] = $"Payment #{paymentRecordId} successfully reconciled. New status: {record?.Status ?? "Updated"}.";
            }
            else
            {
                TempData["AdminMessage"] = $"Payment #{paymentRecordId} status checked with gateway. Current status: {record?.Status ?? "Unchanged"}.";
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction(nameof(PaymentManagement));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessPaymentRefund(int paymentRecordId, string? returnUrl = null)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            PaymentRecord? payment = await _context.PaymentRecords.FindAsync(paymentRecordId);
            if (payment == null)
            {
                TempData["AdminMessage"] = "Payment record was not found.";
                return RedirectToAction(nameof(PaymentManagement));
            }

            decimal refundAmt = payment.UnblockedAmount > 0m ? payment.UnblockedAmount : payment.Amount;
            if (refundAmt <= 0m || payment.RefundStatus == "Completed")
            {
                TempData["AdminMessage"] = "Payment is already marked as refunded or has no unblockable amount.";
                return RedirectToAction(nameof(PaymentManagement));
            }

            if (payment.UnblockedAmount <= 0m)
            {
                payment.UnblockedAmount = payment.Amount;
            }

            await CompleteIpoRefundAsync(payment, GetIndiaStandardTime());
            await _context.SaveChangesAsync();
            await TrySendRefundEmailAsync(payment);
            TempData["AdminMessage"] = $"Payment #{payment.PaymentRecordId} refund/unblock of ₹{payment.UnblockedAmount:N2} marked as completed and credited to investor wallet (Ref: {payment.RefundReferenceId}).";

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction(nameof(PaymentManagement));
        }

        private async Task CompleteIpoRefundAsync(PaymentRecord payment, DateTime nowIst)
        {
            payment.RefundStatus = "Completed";
            payment.RefundProcessedAt = nowIst;
            if (string.IsNullOrWhiteSpace(payment.RefundReferenceId))
            {
                payment.RefundReferenceId = $"UNBLK-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpper();
            }
            payment.UpdatedAt = DateTime.UtcNow;

            if (payment.UnblockedAmount > 0m)
            {
                bool alreadyRefundedToWallet = await _context.VirtualBalanceLedgers.AnyAsync(l =>
                    l.UserId == payment.UserId &&
                    l.TransactionType == "IPO_REFUND" &&
                    l.Reason.Contains(payment.ApplicationNumber));

                if (!alreadyRefundedToWallet)
                {
                    User? investor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == payment.UserId);
                    if (investor is not null)
                    {
                        decimal before = investor.SimulatedCashBalance;
                        investor.SimulatedCashBalance = before + payment.UnblockedAmount;
                        _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                        {
                            UserId = payment.UserId,
                            AdminUserId = null,
                            TransactionType = "IPO_REFUND",
                            Amount = payment.UnblockedAmount,
                            BalanceBefore = before,
                            BalanceAfter = investor.SimulatedCashBalance,
                            Reason = $"IPO mandate unblock/refund for #{payment.ApplicationNumber} (Ref: {payment.RefundReferenceId})",
                            CreatedAt = nowIst
                        });
                    }
                }
            }

            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = payment.UserId,
                Title = "Mandate Amount Unblocked / Refunded",
                Message = $"₹{payment.UnblockedAmount:N2} has been released and credited back to your investor wallet. Refund Ref: {payment.RefundReferenceId}.",
                Created_at = nowIst,
                Is_read = false
            });
        }

        [HttpGet]
        public async Task<IActionResult> PaymentManagement(string? status, string? search, int? ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            var allPayments = await _context.PaymentRecords
                .AsNoTracking()
                .OrderByDescending(record => record.CreatedAt)
                .ToListAsync();

            int[] userIds = allPayments.Select(p => p.UserId).Distinct().ToArray();
            int[] ipoIds = allPayments.Select(p => p.IpoId).Distinct().ToArray();
            string[] orderIds = allPayments.Select(p => p.RazorpayOrderId).Where(o => !string.IsNullOrEmpty(o)).Distinct().ToArray();

            var users = await _context.Users
                .AsNoTracking()
                .Where(u => userIds.Contains(u.User_id))
                .Select(u => new { u.User_id, FullName = (u.First_name + " " + u.Last_name).Trim(), u.Email, u.DematAccountNumber, u.UpiId })
                .ToListAsync();

            var ipos = await _context.IPOs
                .AsNoTracking()
                .Where(i => ipoIds.Contains(i.IPO_id))
                .Select(i => new { i.IPO_id, i.IPO_name })
                .ToListAsync();

            var allIposList = await _context.IPOs
                .AsNoTracking()
                .OrderByDescending(i => i.Open_date)
                .ToListAsync();

            var apps = await _context.IPOApplications
                .AsNoTracking()
                .Where(a => orderIds.Contains(a.Razorpay_order_id))
                .Select(a => new { a.Application_id, a.Razorpay_order_id, a.Application_number })
                .ToListAsync();

            var model = new AdminPaymentManagementViewModel
            {
                Payments = allPayments,
                UserNames = users.ToDictionary(u => u.User_id, u => u.FullName),
                UserEmails = users.ToDictionary(u => u.User_id, u => u.Email ?? "N/A"),
                UserDemats = users.ToDictionary(u => u.User_id, u => u.DematAccountNumber ?? "N/A"),
                UserUpis = users.ToDictionary(u => u.User_id, u => u.UpiId ?? "N/A"),
                IpoNames = ipos.ToDictionary(i => i.IPO_id, i => i.IPO_name),
                AllIpos = allIposList,
                SelectedStatus = status,
                SearchQuery = search,
                SelectedIpoId = ipoId,

                TotalTransactions = allPayments.Count,
                SuccessfulCount = allPayments.Count(p => p.Status == "Captured" || p.Status == "Verified"),
                FailedCount = allPayments.Count(p => p.Status == "Failed"),
                PendingCount = allPayments.Count(p => p.Status == "Created" || p.Status == "Pending"),
                RefundedCount = allPayments.Count(p => p.RefundStatus == "Completed" || p.RefundStatus == "Pending Unblock" || p.UnblockedAmount > 0m || p.Status == "Refunded"),

                TotalVolume = allPayments.Sum(p => p.Amount),
                SuccessfulVolume = allPayments.Where(p => p.Status == "Captured" || p.Status == "Verified").Sum(p => p.Amount),
                FailedVolume = allPayments.Where(p => p.Status == "Failed").Sum(p => p.Amount),
                PendingVolume = allPayments.Where(p => p.Status == "Created" || p.Status == "Pending").Sum(p => p.Amount),
                RefundedVolume = allPayments.Sum(p => p.UnblockedAmount > 0m ? p.UnblockedAmount : (p.RefundStatus == "Completed" ? p.Amount : 0m))
            };

            foreach (var app in apps)
            {
                if (!string.IsNullOrEmpty(app.Razorpay_order_id))
                {
                    model.ApplicationIds[app.Razorpay_order_id] = app.Application_id;
                }
                if (!string.IsNullOrEmpty(app.Application_number))
                {
                    model.ApplicationIds[app.Application_number] = app.Application_id;
                }
            }

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> ExportPaymentsCsv(string? status, int? ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IQueryable<PaymentRecord> query = _context.PaymentRecords.AsNoTracking();
            if (ipoId.HasValue)
            {
                query = query.Where(p => p.IpoId == ipoId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (status.Equals("Captured", StringComparison.OrdinalIgnoreCase) || status.Equals("Successful", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(p => p.Status == "Captured" || p.Status == "Verified");
                }
                else if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(p => p.Status == "Failed");
                }
                else if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase) || status.Equals("Created", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(p => p.Status == "Created" || p.Status == "Pending");
                }
                else if (status.Equals("Refunded", StringComparison.OrdinalIgnoreCase) || status.Equals("Unblocked", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(p => p.RefundStatus == "Completed" || p.RefundStatus == "Pending Unblock" || p.UnblockedAmount > 0m || p.Status == "Refunded");
                }
            }

            List<PaymentRecord> records = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
            int[] userIds = records.Select(p => p.UserId).Distinct().ToArray();
            int[] ipoIds = records.Select(p => p.IpoId).Distinct().ToArray();

            var users = await _context.Users.AsNoTracking().Where(u => userIds.Contains(u.User_id)).ToDictionaryAsync(u => u.User_id);
            var ipos = await _context.IPOs.AsNoTracking().Where(i => ipoIds.Contains(i.IPO_id)).ToDictionaryAsync(i => i.IPO_id);

            var sb = new StringBuilder();
            sb.AppendLine("PaymentRecordId,Date_IST,Investor_Name,Email,Demat,UPI_ID,IPO_Name,Application_Number,Lots,Shares,Bid_Price,Amount,Payment_Mode,Razorpay_Order_Id,Razorpay_Payment_Id,Status,Refund_Status,Unblocked_Amount,Refund_Reference_Id,Receipt_Number,Failure_Reason");

            foreach (var r in records)
            {
                var user = users.GetValueOrDefault(r.UserId);
                var ipo = ipos.GetValueOrDefault(r.IpoId);
                string investorName = user != null ? $"\"{user.First_name} {user.Last_name}\"" : "\"N/A\"";
                string email = user?.Email ?? "N/A";
                string demat = user?.DematAccountNumber ?? "N/A";
                string upi = user?.UpiId ?? "N/A";
                string ipoName = ipo != null ? $"\"{ipo.IPO_name}\"" : "\"N/A\"";
                string dateStr = r.CreatedAt.AddHours(5.5).ToString("yyyy-MM-dd HH:mm:ss");
                string failureReason = string.IsNullOrWhiteSpace(r.FailureReason) ? "" : $"\"{r.FailureReason.Replace("\"", "\"\"")}\"";

                sb.AppendLine($"{r.PaymentRecordId},{dateStr},{investorName},{email},{demat},{upi},{ipoName},{r.ApplicationNumber},{r.AppliedLots},{r.AppliedShares},{r.BidPrice:F2},{r.Amount:F2},{r.PaymentMode},{r.RazorpayOrderId},{r.RazorpayPaymentId},{r.Status},{r.RefundStatus},{r.UnblockedAmount:F2},{r.RefundReferenceId},{r.ReceiptNumber},{failureReason}");
            }

            string filename = $"payment-records-report-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", filename);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryWebhooks()
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            int count = await _paymentReconciliationService.ProcessPendingWebhookRetriesAsync();
            TempData["AdminMessage"] = $"Processed {count} failed webhook retry events.";
            return RedirectToAction(nameof(Applications));
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
                    (item.Status == "Approved" || item.Status == "Published" || item.Status == "Upcoming" || item.Status == "Open" || item.Status == "Listed" || item.Status == "Closed"));
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
                    .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Upcoming" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
                    .OrderBy(ipo => ipo.IPO_name)
                    .ToListAsync();
                return View(await BuildPortfolioAsync(userId.Value));
            }

            [HttpGet]
            public async Task<IActionResult> MarketDetails(int ipoId)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue)
                {
                    return RedirectToAction("Login", "Account");
                }

                MarketDataViewModel? market = await _stockMarketService.GetMarketDataAsync(ipoId);
                if (market is null)
                {
                    TempData["AdminMessage"] = "Stock or market data not found.";
                    TempData["CompanyMessage"] = "Stock or market data not found.";
                    TempData["InvestorMessage"] = "Stock or market data not found.";
                    return RedirectToAction(nameof(Index));
                }

                return View(market);
            }

            [HttpGet]
            public async Task<IActionResult> Trade(int ipoId, bool sell = false, int? investmentId = null)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue)
                {
                    return RedirectToAction("Login", "Account");
                }

                if (!IsInvestor())
                {
                    TempData["AdminMessage"] = "Trading operations are available exclusively for Investor accounts.";
                    TempData["CompanyMessage"] = "Trading operations are available exclusively for Investor accounts.";
                    return RedirectToAction(nameof(Index));
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

                if (!market.IsMarketOpen || !market.CanTrade)
                {
                    TempData["InvestorMessage"] = "Trading is unavailable because the market is closed. Live trading is active Monday to Friday, 09:30 AM to 03:30 PM IST (closed on weekends).";
                    return RedirectToAction(nameof(Portfolio));
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

                // 1. Prevent duplicate trade submissions
                string tradeKey = $"PORTFOLIO_BUY_{userId.Value}_{request.IpoId}_{exchange}_{request.Quantity}";
                if (_recentPortfolioTrades.TryGetValue(tradeKey, out DateTime lastTradeTime) && (DateTime.UtcNow - lastTradeTime).TotalSeconds < 3)
                {
                    TempData["InvestorMessage"] = "A trade with identical parameters was submitted moments ago. Please wait a moment before re-submitting.";
                    return RedirectToAction(nameof(Portfolio));
                }
                _recentPortfolioTrades[tradeKey] = DateTime.UtcNow;

                IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == request.IpoId);
                if (ipo is null)
                {
                    TempData["InvestorMessage"] = "Security or IPO not found.";
                    return RedirectToAction(nameof(Portfolio));
                }

                bool isListedSecurity = string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase) ||
                    (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= _clock.NowIst.Date);

                if (!isListedSecurity)
                {
                    TempData["InvestorMessage"] = $"Trading security policy: Secondary market trading is restricted to publicly listed stocks. '{ipo.IPO_name}' is currently in '{ipo.Status}' status.";
                    return RedirectToAction(nameof(Portfolio));
                }

                // 2. Validate the stock price again on the server
                MarketDataViewModel? marketData = await _stockMarketService.GetMarketDataAsync(ipo.IPO_id);
                if (marketData is null || !marketData.CanTrade || !marketData.IsMarketOpen || !marketData.CurrentPrice.HasValue)
                {
                    TempData["InvestorMessage"] = "Trading is unavailable because the market is closed. Live trading is active Monday to Friday, 09:30 AM to 03:30 PM IST (closed on weekends).";
                    return RedirectToAction(nameof(Portfolio));
                }

                decimal? rawTradePrice = exchange == "BSE" ? (marketData.BsePrice ?? marketData.CurrentPrice) : (marketData.NsePrice ?? marketData.CurrentPrice);
                if (!rawTradePrice.HasValue || rawTradePrice.Value <= 0m)
                {
                    TempData["InvestorMessage"] = "Invalid trade price received from market provider.";
                    return RedirectToAction(nameof(Portfolio));
                }
                decimal tradePrice = rawTradePrice.Value;

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

                // Slippage validation against request price
                if (request.Price > 0m)
                {
                    decimal slippagePercent = Math.Abs(tradePrice - request.Price) / request.Price * 100m;
                    if (slippagePercent > 5.0m)
                    {
                        TempData["InvestorMessage"] = $"Market price moved from ₹{request.Price:N2} to ₹{tradePrice:N2} ({slippagePercent:F2}% price movement exceeds 5% slippage limit). Order was cancelled for safety.";
                        return RedirectToAction(nameof(Portfolio));
                    }
                }

                // 3. Consistent timestamp captured once
                DateTime tradeTime = _clock.NowIst;
                decimal totalAmount = request.Quantity * tradePrice;

                // 4. Ensure Buy operation is an atomic database transaction
                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                try
                {
                    User? investor = await _context.Users.FirstOrDefaultAsync(user => user.User_id == userId.Value && user.Is_active);
                    if (investor is null)
                    {
                        await transaction.RollbackAsync();
                        TempData["InvestorMessage"] = "Active investor account record not found.";
                        return RedirectToAction(nameof(Portfolio));
                    }

                    // Prevent negative cash balances
                    if (investor.SimulatedCashBalance < totalAmount)
                    {
                        await transaction.RollbackAsync();
                        TempData["InvestorMessage"] = $"Insufficient simulated cash balance for this purchase. Required ₹{totalAmount:N2}, available ₹{investor.SimulatedCashBalance:N2}.";
                        return RedirectToAction(nameof(Portfolio));
                    }

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
                            Buy_date = tradeTime,
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

                    // Deduct cash balance and ensure non-negative
                    investor.SimulatedCashBalance -= totalAmount;
                    if (investor.SimulatedCashBalance < 0m)
                    {
                        throw new InvalidOperationException("Negative cash balance prevented. Transaction aborted.");
                    }

                    _context.InvestmentTransactions.Add(new InvestmentTransaction
                    {
                        Investment_id = investment.Investment_id,
                        Transaction_type = "BUY",
                        Quantity = request.Quantity,
                        Price = tradePrice,
                        Total_amount = totalAmount,
                        Transaction_date = tradeTime
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
                        CreatedAt = tradeTime
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
                            timestamp = tradeTime.ToString("hh:mm:ss tt")
                        });
                    }
                    catch
                    {
                        // Ignore SignalR dispatch failure if client disconnected
                    }

                    TempData["InvestorMessage"] = $"Bought {request.Quantity:N0} shares of {ipo.IPO_name} on {exchange} at ₹{tradePrice:N2} in simulated trading.";
                    return RedirectToAction(nameof(Portfolio));
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    TempData["InvestorMessage"] = "Trade execution failed. Your cash balance was not debited.";
                    return RedirectToAction(nameof(Portfolio));
                }
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

                string exchange = string.Equals(request.Exchange, "BSE", StringComparison.OrdinalIgnoreCase) ? "BSE" : "NSE";

                // 1. Prevent duplicate trade submissions
                string tradeKey = $"PORTFOLIO_SELL_{userId.Value}_{request.InvestmentId}_{exchange}_{request.Quantity}";
                if (_recentPortfolioTrades.TryGetValue(tradeKey, out DateTime lastTradeTime) && (DateTime.UtcNow - lastTradeTime).TotalSeconds < 3)
                {
                    TempData["InvestorMessage"] = "A trade with identical parameters was submitted moments ago. Please wait before submitting again.";
                    return RedirectToAction(nameof(Portfolio));
                }
                _recentPortfolioTrades[tradeKey] = DateTime.UtcNow;

                // 2. Verify that the investment belongs to the logged-in investor
                Investment? targetInvestment = await _context.Investments.FirstOrDefaultAsync(item => item.Investment_id == request.InvestmentId);
                if (targetInvestment is null)
                {
                    TempData["InvestorMessage"] = "Investment holding record not found.";
                    return RedirectToAction(nameof(Portfolio));
                }
                if (targetInvestment.User_id != userId.Value)
                {
                    return Forbid();
                }
                if (string.Equals(targetInvestment.Status, "Closed", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["InvestorMessage"] = "This holding has already been completely sold and closed.";
                    return RedirectToAction(nameof(Portfolio));
                }
                Investment investment = targetInvestment;

                // 3. Validate the stock price again on the server
                MarketDataViewModel? marketData = await _stockMarketService.GetMarketDataAsync(investment.IPO_id);
                if (marketData is null || !marketData.CanTrade || !marketData.IsMarketOpen || !marketData.CurrentPrice.HasValue)
                {
                    TempData["InvestorMessage"] = "Selling is unavailable because the market is closed. Live trading is active Monday to Friday, 09:30 AM to 03:30 PM IST (closed on weekends).";
                    return RedirectToAction(nameof(Portfolio));
                }

                decimal? rawTradePrice = exchange == "BSE" ? (marketData.BsePrice ?? marketData.CurrentPrice) : (marketData.NsePrice ?? marketData.CurrentPrice);
                if (!rawTradePrice.HasValue || rawTradePrice.Value <= 0m)
                {
                    TempData["InvestorMessage"] = "Invalid trade price received from market provider.";
                    return RedirectToAction(nameof(Portfolio));
                }
                decimal tradePrice = rawTradePrice.Value;

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

                // Slippage validation against request price
                if (request.Price > 0m)
                {
                    decimal slippagePercent = Math.Abs(tradePrice - request.Price) / request.Price * 100m;
                    if (slippagePercent > 5.0m)
                    {
                        TempData["InvestorMessage"] = $"Market price moved from ₹{request.Price:N2} to ₹{tradePrice:N2} ({slippagePercent:F2}% price movement exceeds 5% slippage limit). Order was cancelled for safety.";
                        return RedirectToAction(nameof(Portfolio));
                    }
                }

                // 4. Consistent timestamp captured once
                DateTime tradeTime = _clock.NowIst;
                decimal totalAmount = request.Quantity * tradePrice;

                // 5. Ensure Sell operation is an atomic database transaction
                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                    await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                try
                {
                    User? investor = await _context.Users.FirstOrDefaultAsync(user => user.User_id == userId.Value && user.Is_active);
                    if (investor is null)
                    {
                        await transaction.RollbackAsync();
                        TempData["InvestorMessage"] = "The investor account could not be loaded.";
                        return RedirectToAction(nameof(Portfolio));
                    }

                    // 6. Prevent selling more shares than the investor owns
                    int soldQuantity = await _context.InvestmentTransactions
                        .Where(t => t.Investment_id == investment.Investment_id &&
                            string.Equals(t.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase))
                        .SumAsync(t => (int?)t.Quantity) ?? 0;
                    int availableQuantity = Math.Max(0, investment.Quantity - soldQuantity);
                    if (availableQuantity <= 0)
                    {
                        await transaction.RollbackAsync();
                        TempData["InvestorMessage"] = "You have 0 shares available to sell in this holding.";
                        return RedirectToAction(nameof(Portfolio));
                    }

                    if (request.Quantity > availableQuantity)
                    {
                        await transaction.RollbackAsync();
                        TempData["InvestorMessage"] = $"Trade rejected: You requested to sell {request.Quantity:N0} shares, but only own {availableQuantity:N0} available shares in this holding.";
                        return RedirectToAction(nameof(Portfolio));
                    }

                    _context.InvestmentTransactions.Add(new InvestmentTransaction
                    {
                        Investment_id = investment.Investment_id,
                        Transaction_type = "SELL",
                        Quantity = request.Quantity,
                        Price = tradePrice,
                        Total_amount = totalAmount,
                        Transaction_date = tradeTime
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
                        CreatedAt = tradeTime
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
                            timestamp = tradeTime.ToString("hh:mm:ss tt")
                        });
                    }
                    catch
                    {
                        // Ignore SignalR dispatch failure if client disconnected
                    }

                    TempData["InvestorMessage"] = $"Sold {request.Quantity:N0} shares of {marketData.IpoName} on {exchange} at ₹{tradePrice:N2} in simulated trading.";
                    return RedirectToAction(nameof(Portfolio));
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    TempData["InvestorMessage"] = "Trade execution failed. No shares were sold and no balance was altered.";
                    return RedirectToAction(nameof(Portfolio));
                }
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> CreateSip(CreateStockSipRequest request)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var (success, message, plan) = await _stockSipService.CreateSipAsync(userId.Value, request);
                TempData["InvestorMessage"] = message;

                string? returnUrl = Request.Headers["Referer"].ToString();
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> PauseSip(int sipId)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var (success, message) = await _stockSipService.PauseSipAsync(userId.Value, sipId);
                TempData["InvestorMessage"] = message;

                string? returnUrl = Request.Headers["Referer"].ToString();
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> ResumeSip(int sipId)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var (success, message) = await _stockSipService.ResumeSipAsync(userId.Value, sipId);
                TempData["InvestorMessage"] = message;

                string? returnUrl = Request.Headers["Referer"].ToString();
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> CancelSip(int sipId)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var (success, message) = await _stockSipService.CancelSipAsync(userId.Value, sipId);
                TempData["InvestorMessage"] = message;

                string? returnUrl = Request.Headers["Referer"].ToString();
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
            }

            [HttpPost]
            [ValidateAntiForgeryToken]
            public async Task<IActionResult> ExecuteSipNow(int sipId)
            {
                int? userId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
                if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                var (success, message, shares, price) = await _stockSipService.ExecuteInstallmentAsync(sipId, requestingUserId: userId.Value, isManualTrigger: true);
                TempData["InvestorMessage"] = message;

                string? returnUrl = Request.Headers["Referer"].ToString();
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
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
            public async Task<IActionResult> WithdrawFunds(WalletWithdrawalRequest request)
            {
                ISession? session = HttpContext.Features.Get<ISessionFeature>()?.Session;
                int? userId = session?.GetInt32("User_id") ?? session?.GetInt32("UserId");
                string? role = session?.GetString("UserRole");
                if (!userId.HasValue || !string.Equals(role, "Investor", StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }

                WalletWithdrawalResult result = await _walletWithdrawalService.WithdrawAsync(
                    userId.Value,
                    request,
                    HttpContext.Connection.RemoteIpAddress?.ToString());

                if (result.Success)
                {
                    TempData["InvestorMessage"] = result.Message;
                }
                else
                {
                    TempData["InvestorError"] = result.Message;
                }

                if (string.Equals(request.ReturnTo, "Dashboard", StringComparison.OrdinalIgnoreCase))
                {
                    return RedirectToAction(nameof(Index));
                }

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
                DateTime now = _clock.NowIst;

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

                    await TrySendEventEmailAsync(
                        investor,
                        "Virtual Balance Adjusted",
                        $"Dear {investor.First_name},\n\nYour virtual trading balance was adjusted by the platform administrator.\n\n- Action: {typeCode}\n- Previous Balance: ₹{oldBalance:N2}\n- New Balance: ₹{newBalance:N2}\n- Reason: {reason.Trim()}\n\nBest regards,\nIPO Investment Management");

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
                        timestamp = _clock.NowIst.ToString("hh:mm:ss tt"),
                        date = _clock.NowIst.ToString("dd MMM yyyy")
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
                string fileName = $"Portfolio-Statement-{_clock.NowIst:yyyyMMdd-HHmm}.pdf";
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
                string fileName = $"Transaction-Report-{_clock.NowIst:yyyyMMdd-HHmm}.pdf";
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
                string fileName = $"IPO-Applications-Report-{_clock.NowIst:yyyyMMdd-HHmm}.pdf";
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
                string fileName = $"IPO-Allotment-Report-{_clock.NowIst:yyyyMMdd-HHmm}.pdf";
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
                         ipo.Status == "Upcoming" ||
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

            var auditLogs = await _context.AuditLogs
                .AsNoTracking()
                .Where(a => a.EntityType == "IPO" && a.EntityId == ipoId.ToString())
                .OrderByDescending(a => a.CreatedAt)
                .Take(50)
                .ToListAsync();

            var ipoFinancials = await _context.IPOFinancials
                .AsNoTracking()
                .Where(f => f.IPO_id == ipoId)
                .OrderByDescending(f => f.Financial_year)
                .ToListAsync();

            IPOAnalysis? analysis = await _context.IPOAnalyses
                .AsNoTracking()
                .Where(a => a.IPO_id == ipoId)
                .OrderByDescending(a => a.Updated_at)
                .FirstOrDefaultAsync();

            var derivedMetrics = ComputeFundamentalMetrics(ipoFinancials);

            return View(new AdminIpoDetailsViewModel 
            { 
                Ipo = ipo, 
                CompanyName = companyName,
                GmpHistory = gmpHistory,
                AuditHistory = auditLogs,
                Financial = ipoFinancials.FirstOrDefault(),
                Analysis = analysis,
                DerivedRoe = derivedMetrics.Roe,
                DerivedRevenueGrowth = derivedMetrics.RevenueGrowth,
                DerivedProfitGrowth = derivedMetrics.ProfitGrowth
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveIpoAnalysis(
            int ipoId,
            decimal? revenue,
            string? revenueUnit,
            decimal? profit,
            string? profitUnit,
            decimal? roe,
            string? roeUnit,
            decimal? revenueGrowth,
            string? revenueGrowthUnit,
            decimal? profitGrowth,
            string? profitGrowthUnit,
            string? analysisSummary)
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

            decimal? normalizedRevenue = NormalizeCurrencyToRupees(revenue, revenueUnit);
            decimal? normalizedProfit = NormalizeCurrencyToRupees(profit, profitUnit);
            decimal? normalizedRoe = NormalizeRateToPercent(roe, roeUnit);
            decimal? normalizedRevenueGrowth = NormalizeRateToPercent(revenueGrowth, revenueGrowthUnit);
            decimal? normalizedProfitGrowth = NormalizeRateToPercent(profitGrowth, profitGrowthUnit);

            string? trimmedSummary = string.IsNullOrWhiteSpace(analysisSummary) ? null : analysisSummary.Trim();
            if (!normalizedRoe.HasValue && !normalizedRevenueGrowth.HasValue && !normalizedProfitGrowth.HasValue &&
                !normalizedRevenue.HasValue && !normalizedProfit.HasValue && string.IsNullOrWhiteSpace(trimmedSummary))
            {
                TempData["AdminMessage"] = "Enter at least one scorecard metric (ROE, Growth, Revenue, Profit) or an Analyst Summary.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }

            if (IsOutOfRange(normalizedRevenue, 9999999999999999.99m) ||
                IsOutOfRange(normalizedProfit, 9999999999999999.99m) ||
                IsOutOfRange(normalizedRoe, 99999999.99m) ||
                IsOutOfRange(normalizedRevenueGrowth, 99999999.99m) ||
                IsOutOfRange(normalizedProfitGrowth, 99999999.99m))
            {
                TempData["AdminMessage"] = "One or more entered values exceed the maximum supported numeric precision.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }

            IPOAnalysis? existing = await _context.IPOAnalyses
                .Where(a => a.IPO_id == ipoId)
                .OrderByDescending(a => a.Updated_at)
                .FirstOrDefaultAsync();

            bool isNew = existing is null;
            DateTime nowUtc = DateTime.UtcNow;

            if (existing is null)
            {
                existing = new IPOAnalysis
                {
                    IPO_id = ipoId,
                    Revenue = normalizedRevenue,
                    Profit = normalizedProfit,
                    ROE = normalizedRoe,
                    Revenue_growth = normalizedRevenueGrowth,
                    Profit_growth = normalizedProfitGrowth,
                    Analysis_summary = trimmedSummary,
                    Updated_at = nowUtc
                };
                _context.IPOAnalyses.Add(existing);
            }
            else
            {
                existing.Revenue = normalizedRevenue;
                existing.Profit = normalizedProfit;
                existing.ROE = normalizedRoe;
                existing.Revenue_growth = normalizedRevenueGrowth;
                existing.Profit_growth = normalizedProfitGrowth;
                existing.Analysis_summary = trimmedSummary;
                existing.Updated_at = nowUtc;
            }

            int? adminId = HttpContext.Session.GetInt32("User_id") ?? HttpContext.Session.GetInt32("UserId");
            string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";

            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = adminId,
                Action = isNew ? "CreateIpoAnalysis" : "UpdateIpoAnalysis",
                EntityType = "IPO",
                EntityId = ipoId.ToString(),
                Details = $"Admin #{adminId} ({adminName}) {(isNew ? "published" : "updated")} fundamental scorecard for '{ipo.IPO_name}': ROE={normalizedRoe?.ToString("N2") ?? "-"}%, RevGrowth={normalizedRevenueGrowth?.ToString("N2") ?? "-"}%, ProfitGrowth={normalizedProfitGrowth?.ToString("N2") ?? "-"}%, Revenue=₹{normalizedRevenue?.ToString("N2") ?? "-"}, Profit=₹{normalizedProfit?.ToString("N2") ?? "-"}.",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = nowUtc
            });

            await _context.SaveChangesAsync();

            await NotifyCompanyOwnerAsync(
                ipo.Company_id,
                $"IPO Fundamental Scorecard {(isNew ? "Published" : "Updated")}: {ipo.IPO_name}",
                $"The platform equity analyst scorecard for '{ipo.IPO_name}' has been {(isNew ? "published" : "updated")} (ROE: {normalizedRoe?.ToString("N2") ?? "-"}%, Revenue Growth: {normalizedRevenueGrowth?.ToString("N2") ?? "-"}%, Profit Growth: {normalizedProfitGrowth?.ToString("N2") ?? "-"}%).");

            if (IsPublicIpoStatus(ipo.Status))
            {
                await NotifyActiveInvestorsAsync(
                    $"IPO Scorecard Updated: {ipo.IPO_name}",
                    $"Fundamental analysis & scorecard updated for {ipo.IPO_name} (ROE: {normalizedRoe?.ToString("N2") ?? "-"}%, Revenue Growth: {normalizedRevenueGrowth?.ToString("N2") ?? "-"}%, Profit Growth: {normalizedProfitGrowth?.ToString("N2") ?? "-"}%).");
            }

            TempData["AdminMessage"] = $"Fundamental scorecard {(isNew ? "published" : "updated")} for {ipo.IPO_name}.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        private static decimal? NormalizeCurrencyToRupees(decimal? value, string? unit)
        {
            if (!value.HasValue) return null;
            decimal multiplier = unit?.Trim() switch
            {
                "Cr" => 10000000m,
                "L" => 100000m,
                "K" => 1000m,
                _ => 1m
            };
            try
            {
                return Math.Round(checked(value.Value * multiplier), 2, MidpointRounding.AwayFromZero);
            }
            catch (OverflowException)
            {
                return decimal.MaxValue;
            }
        }

        private static decimal? NormalizeRateToPercent(decimal? value, string? unit)
        {
            if (!value.HasValue) return null;
            decimal multiplier = unit?.Trim() switch
            {
                "ratio" => 100m,
                "bps" => 0.01m,
                _ => 1m
            };
            try
            {
                return Math.Round(checked(value.Value * multiplier), 2, MidpointRounding.AwayFromZero);
            }
            catch (OverflowException)
            {
                return decimal.MaxValue;
            }
        }

        private static bool IsOutOfRange(decimal? value, decimal maxAbs)
        {
            return value.HasValue && Math.Abs(value.Value) > maxAbs;
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
                int? adminId = HttpContext.Session.GetInt32("User_id");
                IPO? ipo = await _context.IPOs.FirstOrDefaultAsync(i => i.IPO_id == ipoId);
                if (ipo == null)
                {
                    TempData["AdminMessage"] = "The selected IPO was not found.";
                    return RedirectToAction(nameof(Applications));
                }

                if (ipo.Allotment_finalized)
                {
                    TempData["AdminMessage"] = $"Allotment for {ipo.IPO_name} has already been finalized and locked.";
                    return RedirectToAction(nameof(AllotmentResults), new { ipoId });
                }

                AutomaticAllotmentPlan plan = await BuildAutomaticAllotmentAsync(ipoId);
                string[] statuses = await ProcessAllotmentsAsync(plan.Requests, false, ipoId);

                DateTime finalizedAt = GetIndiaStandardTime();
                ipo.Allotment_finalized = true;
                ipo.Allotment_finalized_at = finalizedAt;
                ipo.Status = "Allotted";

                long totalAppliedShares = plan.TotalAppliedShares;
                long totalAllottedShares = plan.AllocatedShares;
                decimal totalDebited = plan.Requests.Sum(r => (decimal)r.AllottedShares * r.AllotmentPrice);
                decimal totalBlocked = plan.Applications.Sum(a => a.Application_amount ?? ((decimal)a.Applied_shares * plan.AllotmentPrice));
                decimal totalUnblocked = Math.Max(0m, totalBlocked - totalDebited);

                IPOAllotmentHistory history = new()
                {
                    IPOId = ipoId,
                    FinalizedByUserId = adminId ?? 1,
                    FinalizedAt = finalizedAt,
                    TotalApplications = plan.Applications.Count,
                    AllottedApplications = plan.Requests.Count(r => r.AllottedLots == r.AppliedLots && r.AllottedLots > 0),
                    PartialAllotmentCount = plan.Requests.Count(r => r.AllottedLots > 0 && r.AllottedLots < r.AppliedLots),
                    UnallottedCount = plan.Requests.Count(r => r.AllottedLots == 0),
                    TotalAppliedShares = totalAppliedShares,
                    TotalAllottedShares = totalAllottedShares,
                    TotalDebitedAmount = totalDebited,
                    TotalUnblockedAmount = totalUnblocked,
                    OversubscriptionMultiple = plan.OverallSubscriptionMultiple,
                    CutOffPrice = plan.AllotmentPrice,
                    Notes = $"Category allocation: RII ({plan.CategorySummaries.FirstOrDefault(c=>c.CategoryCode=="RII")?.AllocatedLots} lots), NII ({plan.CategorySummaries.FirstOrDefault(c=>c.CategoryCode=="NII")?.AllocatedLots} lots), QIB ({plan.CategorySummaries.FirstOrDefault(c=>c.CategoryCode=="QIB")?.AllocatedLots} lots)."
                };

                _context.IPOAllotmentHistories.Add(history);
                _context.AuditLogs.Add(new AuditLog
                {
                    ActorUserId = adminId,
                    Action = "FINAL_ALLOTMENT_CONFIRMED",
                    EntityType = "IPO",
                    EntityId = ipoId.ToString(),
                    Details = $"Final allotment confirmed for {ipo.IPO_name}. {plan.Applications.Count} applications processed. Allotted: {history.AllottedApplications}, Partial: {history.PartialAllotmentCount}, Non-allotted: {history.UnallottedCount}. Total unblocked: ₹{totalUnblocked:N2}.",
                    CreatedAt = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                TempData["AdminMessage"] = $"Final allotment successfully confirmed and locked for {ipo.IPO_name}: {history.AllottedApplications} full allotment(s), {history.PartialAllotmentCount} partial allotment(s), {history.UnallottedCount} non-allotted. Unblocked ₹{totalUnblocked:N2}. Allotment advice is now available.";
                return RedirectToAction(nameof(AllotmentResults), new { ipoId });
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = exception.Message;
                return RedirectToAction(nameof(Applications), new { ipoId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmFinalAllotment(int ipoId)
        {
            return await ConfirmAutomaticAllotment(ipoId);
        }

        [HttpGet]
        public async Task<IActionResult> AllotmentResults(int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == ipoId);
            if (ipo == null)
            {
                return NotFound("IPO not found.");
            }

            IPOAllotmentHistory? history = await _context.IPOAllotmentHistories.AsNoTracking()
                .OrderByDescending(h => h.FinalizedAt)
                .FirstOrDefaultAsync(h => h.IPOId == ipoId);

            List<IPOApplication> applications = await _context.IPOApplications.AsNoTracking()
                .Where(a => a.IPO_id == ipoId && (a.Payment_status == "Verified" || a.Payment_status == "Captured"))
                .OrderBy(a => a.Application_date)
                .ToListAsync();

            int[] appIds = applications.Select(a => a.Application_id).ToArray();
            Dictionary<int, IPOAllotment> allotments = await _context.IPOAllotments.AsNoTracking()
                .Where(al => appIds.Contains(al.Application_id))
                .ToDictionaryAsync(al => al.Application_id);

            Dictionary<string, PaymentRecord> payments = await _context.PaymentRecords.AsNoTracking()
                .Where(p => p.IpoId == ipoId)
                .ToDictionaryAsync(p => p.RazorpayOrderId, StringComparer.OrdinalIgnoreCase);

            Dictionary<int, User> investors = await _context.Users.AsNoTracking()
                .Where(u => applications.Select(a => a.User_id).Contains(u.User_id))
                .ToDictionaryAsync(u => u.User_id);

            int lotSize = ipo.Lot_size ?? 1;
            decimal cutOffPrice = history?.CutOffPrice ?? ipo.Price_max ?? ipo.Price_min ?? 0m;

            List<AllotmentInvestorResultItem> allItems = new();
            foreach (var app in applications)
            {
                allotments.TryGetValue(app.Application_id, out var allotment);
                payments.TryGetValue(app.Razorpay_order_id ?? "", out var payment);
                investors.TryGetValue(app.User_id, out var investor);

                var cat = GetApplicationCategory(app, ipo);
                int allottedLots = allotment?.Allotted_lots ?? 0;
                int allottedShares = allotment?.Allotted_shares ?? 0;
                decimal allotPrice = allotment?.Allotment_price > 0 ? allotment.Allotment_price : cutOffPrice;
                decimal blocked = app.Application_amount ?? (app.Applied_shares * allotPrice);
                decimal debited = allottedShares * allotPrice;
                decimal unblocked = Math.Max(0m, blocked - debited);

                string status;
                if (allotment != null)
                {
                    status = allotment.Status;
                }
                else
                {
                    status = allottedLots == 0 ? "Not Allotted" : (allottedLots == app.Applied_lots ? "Allotted" : "Partially Allotted");
                }

                allItems.Add(new AllotmentInvestorResultItem
                {
                    ApplicationId = app.Application_id,
                    ApplicationNumber = app.Application_number ?? $"#{app.Application_id}",
                    UserId = app.User_id,
                    InvestorName = $"{investor?.First_name} {investor?.Last_name}".Trim(),
                    InvestorEmail = investor?.Email ?? "N/A",
                    DematAccountNumber = app.Demat_account_number ?? investor?.DematAccountNumber ?? "N/A",
                    UpiId = app.Upi_id ?? investor?.UpiId ?? "N/A",
                    CategoryCode = cat.Code,
                    CategoryName = cat.Name,
                    AppliedLots = app.Applied_lots,
                    AppliedShares = app.Applied_shares,
                    BidPrice = app.Bid_price ?? allotPrice,
                    AllottedLots = allottedLots,
                    AllottedShares = allottedShares,
                    AllotmentPrice = allotPrice,
                    BlockedAmount = blocked,
                    DebitedAmount = debited,
                    UnblockedAmount = unblocked,
                    Status = status,
                    AdviceNumber = allotment?.Advice_number ?? $"CAN-{app.Application_date:yyyyMMdd}-{app.Application_id:D5}",
                    RefundStatus = payment?.RefundStatus ?? (unblocked > 0 ? "Pending Unblock" : "Not Applicable"),
                    RefundReferenceId = payment?.RefundReferenceId
                });
            }

            var model = new AllotmentResultsViewModel
            {
                Ipo = ipo,
                History = history,
                AllotmentDate = history?.FinalizedAt ?? ipo.Allotment_date ?? DateTime.UtcNow,
                CutOffPrice = cutOffPrice,
                LotSize = lotSize,
                TotalApplications = allItems.Count,
                AllottedCount = allItems.Count(i => i.Status == "Allotted"),
                PartialAllotmentCount = allItems.Count(i => i.Status == "Partially Allotted"),
                UnallottedCount = allItems.Count(i => i.Status == "Not Allotted"),
                TotalAppliedShares = allItems.Sum(i => (long)i.AppliedShares),
                TotalAllottedShares = allItems.Sum(i => (long)i.AllottedShares),
                TotalDebitedAmount = allItems.Sum(i => i.DebitedAmount),
                TotalUnblockedAmount = allItems.Sum(i => i.UnblockedAmount),
                OversubscriptionMultiple = history?.OversubscriptionMultiple ?? (ipo.Total_shares > 0 ? Math.Round((decimal)allItems.Sum(i => (long)i.AppliedShares) / ipo.Total_shares.Value, 2) : 1m),
                AllApplicants = allItems,
                AllottedApplicants = allItems.Where(i => i.Status == "Allotted").ToList(),
                PartiallyAllottedApplicants = allItems.Where(i => i.Status == "Partially Allotted").ToList(),
                NonAllottedApplicants = allItems.Where(i => i.Status == "Not Allotted").ToList()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> AllotmentHistory()
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            List<IPOAllotmentHistory> histories = await _context.IPOAllotmentHistories
                .AsNoTracking()
                .OrderByDescending(h => h.FinalizedAt)
                .ToListAsync();

            int[] ipoIds = histories.Select(h => h.IPOId).Distinct().ToArray();
            Dictionary<int, IPO> ipos = await _context.IPOs.AsNoTracking()
                .Where(i => ipoIds.Contains(i.IPO_id))
                .ToDictionaryAsync(i => i.IPO_id);

            int[] adminIds = histories.Select(h => h.FinalizedByUserId).Distinct().ToArray();
            Dictionary<int, string> adminNames = await _context.Users.AsNoTracking()
                .Where(u => adminIds.Contains(u.User_id))
                .ToDictionaryAsync(u => u.User_id, u => $"{u.First_name} {u.Last_name}".Trim());

            AllotmentHistoryListViewModel model = new();
            foreach (var h in histories)
            {
                ipos.TryGetValue(h.IPOId, out var ipo);
                model.Items.Add(new AllotmentHistoryItemViewModel
                {
                    HistoryId = h.AllotmentHistoryId,
                    IpoId = h.IPOId,
                    IpoName = ipo?.IPO_name ?? $"IPO #{h.IPOId}",
                    CompanyName = ipo?.IPO_name ?? "Issuer",
                    FinalizedAt = h.FinalizedAt,
                    FinalizedByAdminName = adminNames.GetValueOrDefault(h.FinalizedByUserId, "Platform Admin"),
                    TotalApplications = h.TotalApplications,
                    AllottedApplications = h.AllottedApplications,
                    PartialAllotmentCount = h.PartialAllotmentCount,
                    UnallottedCount = h.UnallottedCount,
                    TotalAllottedShares = h.TotalAllottedShares,
                    TotalDebitedAmount = h.TotalDebitedAmount,
                    TotalUnblockedAmount = h.TotalUnblockedAmount,
                    OversubscriptionMultiple = h.OversubscriptionMultiple,
                    CutOffPrice = h.CutOffPrice
                });
            }

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> AllotmentAdvice(int applicationId)
        {
            int? currentUserId = HttpContext.Session.GetInt32("User_id");
            string? role = HttpContext.Session.GetString("UserRole");
            if (!currentUserId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            IPOApplication? app = await _context.IPOApplications.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Application_id == applicationId);

            if (app == null)
            {
                TempData["InvestorMessage"] = "Application not found.";
                return RedirectToAction(nameof(Index));
            }

            bool isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            if (!isAdmin && app.User_id != currentUserId.Value)
            {
                return Forbid();
            }

            User? user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == app.User_id);
            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == app.IPO_id);
            IPOAllotment? allotment = await _context.IPOAllotments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Application_id == applicationId);
            PaymentRecord? payment = await _context.PaymentRecords.AsNoTracking()
                .FirstOrDefaultAsync(p => p.RazorpayOrderId == app.Razorpay_order_id);
            IPOListing? listing = ipo != null ? await _context.IPOListings.AsNoTracking().FirstOrDefaultAsync(l => l.IPO_id == ipo.IPO_id) : null;

            int appliedShares = app.Applied_shares;
            int appliedLots = app.Applied_lots;
            int allottedShares = allotment?.Allotted_shares ?? 0;
            int allottedLots = allotment?.Allotted_lots ?? 0;
            decimal price = allotment != null && allotment.Allotment_price > 0 ? allotment.Allotment_price : (app.Bid_price ?? 0m);
            decimal blocked = app.Application_amount ?? (appliedShares * (app.Bid_price ?? 0m));
            decimal debited = allottedShares * price;
            decimal unblocked = Math.Max(0m, blocked - debited);
            string status = allotment?.Status ?? (allottedShares == 0 ? "Not Allotted" : (allottedShares == appliedShares ? "Allotted" : "Partially Allotted"));
            string adviceNo = allotment?.Advice_number ?? $"CAN-{DateTime.UtcNow:yyyyMMdd}-{app.Application_id:D5}";

            (string Code, string Name, string Rule) cat = ipo != null ? GetApplicationCategory(app, ipo) : (Code: "RII", Name: "Retail Individual Investor (RII)", Rule: "");

            AllotmentAdviceViewModel model = new()
            {
                AdviceNumber = adviceNo,
                ApplicationId = app.Application_id,
                ApplicationNumber = app.Application_number ?? $"#{app.Application_id}",
                ApplicationDate = app.Application_date,
                AllotmentDate = allotment?.Allotment_date ?? ipo?.Allotment_date ?? DateTime.UtcNow,
                UserId = app.User_id,
                InvestorName = $"{user?.First_name} {user?.Last_name}".Trim(),
                InvestorEmail = user?.Email ?? "N/A",
                InvestorPhone = user?.Phone ?? "N/A",
                DematAccountNumber = app.Demat_account_number ?? user?.DematAccountNumber ?? "N/A",
                UpiId = app.Upi_id ?? user?.UpiId ?? "N/A",
                IpoId = app.IPO_id,
                IpoName = ipo?.IPO_name ?? "IPO Issue",
                CompanyName = ipo?.IPO_name ?? "Issuer Company",
                StockSymbol = listing?.StockSymbol ?? "N/A",
                LotSize = ipo?.Lot_size ?? 1,
                CategoryName = cat.Name,
                CategoryCode = cat.Code,
                AppliedLots = appliedLots,
                AppliedShares = appliedShares,
                BidPrice = app.Bid_price ?? price,
                AllottedLots = allottedLots,
                AllottedShares = allottedShares,
                AllotmentPrice = price,
                BlockedAmount = blocked,
                DebitedAmount = debited,
                UnblockedAmount = unblocked,
                AmountInWords = RupeeWordsHelper.ToWords(debited > 0 ? debited : blocked),
                Status = status,
                RefundStatus = payment?.RefundStatus ?? (unblocked > 0 ? "Completed" : "Not Applicable"),
                RefundReferenceId = payment?.RefundReferenceId,
                RefundProcessedAt = payment?.RefundProcessedAt,
                ExpectedListingDate = ipo?.Listing_date,
                VerificationCode = adviceNo.GetHashCode().ToString("X8")
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> DownloadAllotmentAdvicePdf(int applicationId)
        {
            int? currentUserId = HttpContext.Session.GetInt32("User_id");
            string? role = HttpContext.Session.GetString("UserRole");
            if (!currentUserId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            IPOApplication? app = await _context.IPOApplications.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Application_id == applicationId);
            if (app == null) return NotFound("Application not found.");

            bool isAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            if (!isAdmin && app.User_id != currentUserId.Value)
            {
                return Forbid();
            }

            byte[] pdfBytes = await _investorReportService.GenerateAllotmentAdvicePdfAsync(applicationId);
            string fileName = $"Allotment-Advice-{app.Application_number ?? app.Application_id.ToString()}-{DateTime.UtcNow:yyyyMMdd}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        private sealed class AutomaticAllotmentPlan
        {
            public IPO Ipo { get; set; } = new();
            public List<AllotmentRequestViewModel> Requests { get; set; } = new();
            public List<IPOApplication> Applications { get; set; } = new();
            public int AvailableLots { get; set; }
            public decimal AllotmentPrice { get; set; }
            public long TotalAppliedShares => Applications.Sum(a => (long)a.Applied_shares);
            public long AllocatedShares => Requests.Sum(r => (long)r.AllottedShares);
            public decimal OverallSubscriptionMultiple => AvailableLots > 0 ? Math.Round((decimal)Applications.Sum(a => a.Applied_lots) / AvailableLots, 2) : 0m;
            public List<CategoryAllotmentSummaryViewModel> CategorySummaries { get; set; } = new();
            public Dictionary<int, (string Code, string Name)> ApplicationCategories { get; set; } = new();
            public bool IsClosed { get; set; }
            public bool IsAllotmentDateReached { get; set; }
        }

        private static (bool IsClosed, bool HasAllotmentDate, bool IsAllotmentDateReached, bool AllotmentWindowOpen, string StatusMessage) CheckAllotmentTiming(IPO ipo, DateTime today)
        {
            if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                return (IsClosed: true, HasAllotmentDate: false, IsAllotmentDateReached: false, AllotmentWindowOpen: false, StatusMessage: $"IPO has been {ipo.Status?.ToLowerInvariant() ?? "prohibited"} - allotment prohibited.");
            }

            if (ipo.Allotment_finalized)
            {
                return (IsClosed: true, HasAllotmentDate: true, IsAllotmentDateReached: true, AllotmentWindowOpen: false, StatusMessage: "Allotment finalized and locked");
            }

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

            if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Rejected and cancelled IPOs must not receive allotments. Current IPO status: '{ipo.Status}'.");
            }

            if (ipo.Allotment_finalized)
            {
                throw new InvalidOperationException($"Allotment for '{ipo.IPO_name}' has already been finalized and locked. Normal allotment generation is disabled.");
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

            decimal cutOffPrice = ipo.Price_max ?? ipo.Price_min ?? 0m;
            List<AllotmentRequestViewModel> requests = applications.Select(application =>
            {
                int lots = allottedLotsMap.GetValueOrDefault(application.Application_id, 0);
                return new AllotmentRequestViewModel
                {
                    ApplicationId = application.Application_id,
                    AppliedLots = application.Applied_lots,
                    AppliedShares = application.Applied_shares,
                    AllottedLots = lots,
                    AllottedShares = checked(lots * ipo.Lot_size.Value),
                    AllotmentPrice = cutOffPrice
                };
            }).ToList();

            return new AutomaticAllotmentPlan
            {
                Ipo = ipo,
                Requests = requests,
                Applications = applications,
                AvailableLots = availableLots,
                AllotmentPrice = cutOffPrice,
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

            if (allottedShares <= 0 && allottedLots > 0 && ipo.Lot_size > 0)
            {
                allottedShares = allottedLots * ipo.Lot_size.Value;
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

            await CompleteIpoRefundAsync(payment, GetIndiaStandardTime());
            await _context.SaveChangesAsync();
            await TrySendRefundEmailAsync(payment);
            TempData["AdminMessage"] = $"₹{payment.UnblockedAmount:N2} was marked as unblocked and credited back to the investor's wallet.";
            return RedirectToAction(nameof(ApplicationDetails), new { applicationId });
        }

        [HttpGet]
        public async Task<IActionResult> SearchIpos(
            string? adminIpoQuery = null,
            string? adminIpoStatus = null,
            string? adminIpoType = null)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            var ipoItems = await GetAdminSearchIposListAsync(adminIpoQuery, adminIpoStatus, adminIpoType);
            var model = new AdminSearchIposViewModel
            {
                UserName = HttpContext.Session.GetString("UserName") ?? "Admin",
                AdminIpoSearchQuery = adminIpoQuery,
                AdminIpoSearchStatus = adminIpoStatus,
                AdminIpoSearchType = adminIpoType,
                AllAdminIpos = ipoItems
            };

            return View(model);
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
                GetIndiaStandardTime() >= _ipoStatusService.GetClosingDateTime(ipo) ||
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

            PaymentRecord? paymentRecord = await _context.PaymentRecords
                .FirstOrDefaultAsync(record => record.RazorpayOrderId == application.Razorpay_order_id);
            if (paymentRecord != null)
            {
                paymentRecord.UnblockedAmount = application.Payment_amount ?? 0m;
                paymentRecord.RefundStatus = "Pending Unblock";
                paymentRecord.RefundReferenceId = $"UNBLK-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpper();
                paymentRecord.UpdatedAt = DateTime.UtcNow;
            }

            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = application.User_id,
                Title = "IPO Application Cancelled & Refund Initiated",
                Message = $"Your application #{application.Application_id} for {ipo.IPO_name} was cancelled by administrator (Reason: {application.Cancellation_reason}). Full refund of ₹{application.Payment_amount:N2} has been initiated to your UPI ID {application.Upi_id}. Refund Ref: {paymentRecord?.RefundReferenceId ?? "N/A"}.",
                Created_at = cancellationDate,
                Is_read = false
            });

            await _context.SaveChangesAsync();

            User? cancelledApplicant = await _context.Users.FindAsync(application.User_id);
            if (cancelledApplicant != null)
            {
                await TrySendEventEmailAsync(
                    cancelledApplicant,
                    $"IPO Application Cancelled: {ipo.IPO_name}",
                    $"Dear {cancelledApplicant.First_name},\n\nYour IPO application #{application.Application_id} for {ipo.IPO_name} has been cancelled by the platform administrator.\n\nCancellation Details:\n- Reason: {application.Cancellation_reason}\n- Refund Amount: ₹{application.Payment_amount:N2}\n- Refund Method: UPI ID {application.Upi_id}\n\nThe funds will be refunded back to your registered source account.\n\nBest regards,\nIPO Investment Management");
            }

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
                IPOApplication? app = await _context.IPOApplications.AsNoTracking().FirstOrDefaultAsync(a => a.Application_id == applicationId);
                IPO? ipo = app != null ? await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == app.IPO_id) : null;
                if (allottedShares <= 0 && allottedLots > 0 && ipo?.Lot_size > 0)
                {
                    allottedShares = allottedLots * ipo.Lot_size.Value;
                }

                string[] statuses = await ProcessAllotmentsAsync(new[]
                {
                    new AllotmentRequestViewModel
                    {
                        ApplicationId = applicationId,
                        AppliedLots = app?.Applied_lots ?? 0,
                        AppliedShares = app?.Applied_shares ?? 0,
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

                // Rule: Finalized allotments should not be editable without an authorized correction process
                if (ipo.Allotment_finalized)
                {
                    throw new InvalidOperationException($"Allotment for '{ipo.IPO_name}' has already been finalized and locked. Finalized allotments cannot be edited without an authorized correction process.");
                }

                // Rule: Rejected and cancelled IPOs must not receive allotments
                if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Rejected and cancelled IPOs must not receive allotments. Current IPO status: '{ipo.Status}'.");
                }

                // Rule: Rejected and cancelled applications must not receive allotments
                if (applications.Any(a => string.Equals(a.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(a.Status, "Rejected", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Rejected and cancelled applications must not receive allotments.");
                }

                if (applications.Any(a => !string.Equals(a.Status, "Submitted", StringComparison.OrdinalIgnoreCase)))
                {
                    var nonSubmitted = applications.Where(a => !string.Equals(a.Status, "Submitted", StringComparison.OrdinalIgnoreCase)).Select(a => $"#{a.Application_id} ({a.Status})");
                    throw new InvalidOperationException($"Allotment can only be processed for applications in 'Submitted' status. Invalid applications: {string.Join(", ", nonSubmitted)}.");
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

                // Rule: Allotment must not happen twice for the same application
                int[] alreadyProcessedIds = await _context.IPOAllotments
                    .Where(allotment => applicationIds.Contains(allotment.Application_id))
                    .Select(allotment => allotment.Application_id)
                    .ToArrayAsync();
                if (alreadyProcessedIds.Length > 0)
                {
                    throw new InvalidOperationException($"Allotment must not happen twice for the same application. Application(s) already processed: {string.Join(", ", alreadyProcessedIds)}.");
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

                    if (allottedShares == 0 && allottedLots > 0 && ipo.Lot_size > 0)
                    {
                        allottedShares = checked(allottedLots * ipo.Lot_size.Value);
                    }

                    // Rule: Allotted shares must never exceed applied shares
                    if (allottedShares > application.Applied_shares || allottedLots > application.Applied_lots)
                    {
                        throw new InvalidOperationException($"Allotted shares ({allottedShares}) must never exceed applied shares ({application.Applied_shares}) for application #{application.Application_id}.");
                    }

                    // Rule: Allotment quantity must respect lot size
                    if (allottedShares != checked(allottedLots * ipo.Lot_size.Value) || (allottedShares % ipo.Lot_size.Value != 0))
                    {
                        throw new InvalidOperationException($"Allotment quantity must respect lot size ({ipo.Lot_size.Value} shares/lot). Got {allottedShares} shares for application #{application.Application_id}.");
                    }

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
                        IPOAllotment? allotment = await _context.IPOAllotments
                            .AsNoTracking()
                            .FirstOrDefaultAsync(item => item.Application_id == application.Application_id);
                        int allottedResultShares = allotment?.Allotted_shares ?? 0;
                        int allottedResultLots = allotment?.Allotted_lots ?? 0;
                        decimal allotmentResultPrice = allotment?.Allotment_price ?? 0m;
                        decimal debitedAmt = allotment?.Debited_amount ?? (allottedResultShares * allotmentResultPrice);
                        decimal unblockedAmt = allotment?.Unblocked_amount ?? Math.Max(0m, (application.Application_amount ?? 0m) - debitedAmt);
                        string adviceNo = allotment?.Advice_number ?? $"CAN-{allotmentDate:yyyyMMdd}-{application.Application_id:D5}";
                        string listingDateStr = ipo.Listing_date.HasValue ? ipo.Listing_date.Value.ToString("dd MMM yyyy") : "To be announced";

                        string subject;
                        string message;

                        if (string.Equals(application.Status, "Allotted", StringComparison.OrdinalIgnoreCase))
                        {
                            subject = $"{ipo.IPO_name} - Allotment Confirmed (CAN: {adviceNo})";
                            message = $"Dear {investor.First_name},\n\nCongratulations! Your application #{application.Application_number ?? application.Application_id.ToString()} for {ipo.IPO_name} has received FULL ALLOTMENT.\n\nAllotment Summary:\n- CAN / Advice Reference: {adviceNo}\n- Applied Lots / Shares: {application.Applied_lots} lot(s) / {application.Applied_shares:N0} shares\n- Allotted Lots / Shares: {allottedResultLots} lot(s) / {allottedResultShares:N0} shares\n- Allotment Price: ₹{allotmentResultPrice:N2} per share\n- Total Debited Amount: ₹{debitedAmt:N2}\n- Designated Demat Account: {application.Demat_account_number}\n- Expected Listing Date on NSE/BSE: {listingDateStr}\n\nYou can download your official Allotment Advice (CAN) from your Investor Dashboard.\n\nBest regards,\nIPO Investment Management";
                        }
                        else if (string.Equals(application.Status, "Partially Allotted", StringComparison.OrdinalIgnoreCase))
                        {
                            subject = $"{ipo.IPO_name} - Partial Allotment Confirmed (CAN: {adviceNo})";
                            message = $"Dear {investor.First_name},\n\nYour application #{application.Application_number ?? application.Application_id.ToString()} for {ipo.IPO_name} has received PARTIAL ALLOTMENT due to oversubscription.\n\nAllotment & Refund Summary:\n- CAN / Advice Reference: {adviceNo}\n- Applied Lots / Shares: {application.Applied_lots} lot(s) / {application.Applied_shares:N0} shares\n- Allotted Lots / Shares: {allottedResultLots} lot(s) / {allottedResultShares:N0} shares\n- Allotment Price: ₹{allotmentResultPrice:N2} per share\n- Total Debited Amount: ₹{debitedAmt:N2}\n- Unblocked / Refunded Amount: ₹{unblockedAmt:N2}\n- Refund Destination: UPI ID {application.Upi_id}\n- Designated Demat Account: {application.Demat_account_number}\n- Expected Listing Date: {listingDateStr}\n\nYou can download your official Allotment Advice (CAN) from your Investor Dashboard.\n\nBest regards,\nIPO Investment Management";
                        }
                        else
                        {
                            subject = $"{ipo.IPO_name} - Allotment Result: Not Allotted";
                            message = $"Dear {investor.First_name},\n\nThank you for bidding in the {ipo.IPO_name} public issue. Due to heavy oversubscription, no shares could be allotted for your application #{application.Application_number ?? application.Application_id.ToString()}.\n\nSettlement Summary:\n- CAN Reference: {adviceNo}\n- Applied Shares: {application.Applied_shares:N0}\n- Allotted Shares: 0\n- Full Refund / Unblock Amount: ₹{unblockedAmt:N2}\n- Mandate Release UPI ID: {application.Upi_id}\n\nThe blocked amount has been released back to your bank account. You can download your official Allotment Advice from your dashboard.\n\nBest regards,\nIPO Investment Management";
                        }

                        await TrySendEventEmailAsync(investor, subject, message);
                    }
                }

                try
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                    {
                        category = "ALLOTMENT RESULTS",
                        title = $"Allotment Finalized: {ipo.IPO_name}",
                        message = $"Allotment results for {ipo.IPO_name} have been finalized and published. Check 'My Applications' on your Investor Dashboard.",
                        timestamp = GetIndiaStandardTime().ToString("hh:mm tt")
                    });
                }
                catch { }

            return applications.Select(application => application.Status ?? "Not Allotted").ToArray();
        }

        private void ValidateAllotmentRequest(IPOApplication application, IPO ipo, int allottedLots, int allottedShares, decimal allotmentPrice)
        {
            if (ipo.Allotment_finalized)
            {
                ModelState.AddModelError(string.Empty, $"Allotment for '{ipo.IPO_name}' has already been finalized and locked. Finalized allotments cannot be modified without an authorized correction process.");
            }

            if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(string.Empty, $"Rejected and cancelled IPOs must not receive allotments. Current IPO status: '{ipo.Status}'.");
            }

            if (string.Equals(application.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(application.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(string.Empty, $"Rejected and cancelled applications must not receive allotments. Current application status: '{application.Status}'.");
            }

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

            if (!ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0)
            {
                ModelState.AddModelError(string.Empty, "IPO lot size is invalid or not configured.");
            }
            else
            {
                int lotSize = ipo.Lot_size.Value;
                if (allottedLots < 0 || allottedShares < 0)
                {
                    ModelState.AddModelError(string.Empty, "Allotted lots and shares cannot be negative.");
                }
                if (allottedLots > application.Applied_lots || allottedShares > application.Applied_shares)
                {
                    ModelState.AddModelError(string.Empty, $"Allotted shares ({allottedShares}) must never exceed applied shares ({application.Applied_shares}), and allotted lots ({allottedLots}) must never exceed applied lots ({application.Applied_lots}).");
                }
                if (allottedShares != checked(allottedLots * lotSize) || (allottedShares % lotSize != 0))
                {
                    ModelState.AddModelError(string.Empty, $"Allotment quantity must respect lot size. Allotted shares ({allottedShares}) must equal allotted lots ({allottedLots}) × lot size ({lotSize}) with no fractional shares.");
                }
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
            if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Rejected and cancelled IPOs must not receive allotments. IPO '{ipo.IPO_name}' is in '{ipo.Status}' status.");
            }

            if (string.Equals(application.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(application.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Rejected and cancelled applications must not receive allotments. Application #{application.Application_id} is in '{application.Status}' status.");
            }

            if (_context.IPOAllotments.Local.Any(a => a.Application_id == application.Application_id) ||
                await _context.IPOAllotments.AnyAsync(a => a.Application_id == application.Application_id))
            {
                throw new InvalidOperationException($"Allotment must not happen twice for the same application. Application #{application.Application_id} already has an allotment record.");
            }

            if (allottedShares > application.Applied_shares || allottedLots > application.Applied_lots)
            {
                throw new InvalidOperationException($"Allotted shares ({allottedShares}) must never exceed applied shares ({application.Applied_shares}) for application #{application.Application_id}.");
            }

            if (!ipo.Lot_size.HasValue || ipo.Lot_size.Value <= 0 ||
                allottedShares != checked(allottedLots * ipo.Lot_size.Value) ||
                (allottedShares % ipo.Lot_size.Value != 0))
            {
                throw new InvalidOperationException($"Allotment quantity must respect lot size ({ipo.Lot_size?.ToString() ?? "0"}). Got {allottedShares} shares for {allottedLots} lots.");
            }

            string status = allottedShares == 0
                ? "Not Allotted"
                : allottedShares == application.Applied_shares
                    ? "Allotted"
                    : "Partially Allotted";

            string adviceNumber = $"CAN-{allotmentDate:yyyyMMdd}-{application.Application_id:D5}";

            PaymentRecord? paymentRecord = await _context.PaymentRecords
                .FirstOrDefaultAsync(record => record.RazorpayOrderId == application.Razorpay_order_id);

            decimal totalPaid = paymentRecord?.Amount ?? application.Payment_amount ?? application.Application_amount ?? ((decimal)application.Applied_shares * price);
            decimal debitedAmount = Math.Round(allottedShares * price, 2);
            decimal unblockedAmount = Math.Max(0m, totalPaid - debitedAmount);

            _context.IPOAllotments.Add(new IPOAllotment
            {
                Application_id = application.Application_id,
                User_id = application.User_id,
                IPO_id = application.IPO_id,
                Allotted_lots = allottedLots,
                Allotted_shares = allottedShares,
                Allotment_price = price,
                Debited_amount = debitedAmount,
                Unblocked_amount = unblockedAmount,
                Advice_number = adviceNumber,
                Status = status,
                Allotment_date = allotmentDate
            });
            application.Status = status;

            // Invariant: Refund/unblock amount must match the payment record
            if (paymentRecord is not null)
            {
                paymentRecord.UnblockedAmount = unblockedAmount;
                paymentRecord.RefundStatus = unblockedAmount > 0m ? "Pending Unblock" : "Not Applicable";
                if (unblockedAmount > 0m && string.IsNullOrWhiteSpace(paymentRecord.RefundReferenceId))
                {
                    paymentRecord.RefundReferenceId = $"UNBLK-{allotmentDate:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpper();
                }
                paymentRecord.UpdatedAt = allotmentDate;
            }

            if (allottedShares == 0)
            {
                _context.SystemNotifications.Add(new SystemNotification
                {
                    User_id = application.User_id,
                    Title = $"IPO Allotment Result: {ipo.IPO_name}",
                    Message = $"{ipo.IPO_name}: No shares were allotted for application #{application.Application_number ?? application.Application_id.ToString()}. Full blocked amount of ₹{unblockedAmount:N2} has been unblocked/refunded to your UPI account ({application.Upi_id}). Allotment Advice: {adviceNumber}.",
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
                Transaction_type = "BUY",
                Quantity = allottedShares,
                Price = price,
                Total_amount = debitedAmount,
                Transaction_date = allotmentDate
            });
            string expectedListing = ipo.Listing_date.HasValue ? ipo.Listing_date.Value.ToString("dd MMM yyyy") : "To be announced";
            string notifTitle = status == "Allotted"
                ? $"IPO Allotment Confirmed: {ipo.IPO_name}"
                : $"IPO Partial Allotment: {ipo.IPO_name}";
            string notifMessage = status == "Allotted"
                ? $"{ipo.IPO_name}: Full allotment confirmed! {allottedShares:N0} share(s) ({allottedLots} lot(s)) allotted at ₹{price:N2}. Total debited: ₹{debitedAmount:N2}. Shares credited to Demat: {application.Demat_account_number}. Expected listing date: {expectedListing}. Advice Ref: {adviceNumber}."
                : $"{ipo.IPO_name}: Partial allotment confirmed. {allottedShares:N0} share(s) ({allottedLots} lot(s)) out of {application.Applied_shares:N0} shares applied were allotted at ₹{price:N2}. Total debited: ₹{debitedAmount:N2}. Balance ₹{unblockedAmount:N2} released back to UPI ({application.Upi_id}). Advice Ref: {adviceNumber}.";

            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = application.User_id,
                Title = notifTitle,
                Message = notifMessage,
                Created_at = allotmentDate,
                Is_read = false
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AuthorizeAllotmentCorrection(AllotmentCorrectionRequestViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            int? adminId = HttpContext.Session.GetInt32("User_id");
            if (!adminId.HasValue)
            {
                return Forbid();
            }

            if (!ModelState.IsValid)
            {
                TempData["AdminMessage"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
            }

            if (string.IsNullOrWhiteSpace(model.CorrectionReason) || model.CorrectionReason.Trim().Length < 10)
            {
                TempData["AdminMessage"] = "A detailed auditable justification of at least 10 characters is mandatory for authorized corrections.";
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
            }

            if (!string.Equals(model.AuthorizationPasscode?.Trim(), "CONFIRM-CORRECTION", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "Authorization failed. Please enter the valid confirmation passcode 'CONFIRM-CORRECTION' to execute an authorized allotment correction.";
                return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                IPOApplication? application = await _context.IPOApplications
                    .FirstOrDefaultAsync(a => a.Application_id == model.ApplicationId);
                if (application is null)
                {
                    TempData["AdminMessage"] = "Application not found.";
                    return RedirectToAction(nameof(Applications));
                }

                IPO? ipo = await _context.IPOs.FirstOrDefaultAsync(i => i.IPO_id == application.IPO_id);
                if (ipo is null)
                {
                    TempData["AdminMessage"] = "IPO issue record not found.";
                    return RedirectToAction(nameof(Applications));
                }

                // Rule: Rejected and cancelled IPOs must not receive allotments
                if (string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Rejected and cancelled IPOs must not receive allotments. Current IPO status: '{ipo.Status}'.");
                }

                // Rule: Rejected and cancelled applications must not receive allotments
                if (string.Equals(application.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(application.Status, "Rejected", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Rejected and cancelled applications must not receive allotments. Current application status: '{application.Status}'.");
                }

                IPOAllotment? allotment = await _context.IPOAllotments
                    .FirstOrDefaultAsync(a => a.Application_id == model.ApplicationId);
                if (allotment is null)
                {
                    throw new InvalidOperationException("No existing allotment record found for this application. Normal allotment processing should be used.");
                }

                int lotSize = ipo.Lot_size ?? 1;

                // Rule: Allotted shares must never exceed applied shares
                if (model.CorrectedLots < 0 || model.CorrectedLots > application.Applied_lots)
                {
                    throw new InvalidOperationException($"Corrected lots ({model.CorrectedLots}) cannot be negative or exceed applied lots ({application.Applied_lots}).");
                }

                int correctedShares = checked(model.CorrectedLots * lotSize);
                if (correctedShares > application.Applied_shares)
                {
                    throw new InvalidOperationException($"Corrected shares ({correctedShares}) must never exceed applied shares ({application.Applied_shares}).");
                }

                // Rule: Allotment quantity must respect lot size
                if (correctedShares != model.CorrectedLots * lotSize || (correctedShares % lotSize != 0))
                {
                    throw new InvalidOperationException($"Allotment quantity must respect lot size ({lotSize} shares/lot). Fractional lots are prohibited.");
                }

                decimal price = model.CorrectedPrice ?? allotment.Allotment_price;
                if (price <= 0m || price < (ipo.Price_min ?? 0m) || price > (ipo.Price_max ?? decimal.MaxValue))
                {
                    throw new InvalidOperationException($"Allotment price (₹{price:N2}) must be within the valid issue price band (₹{ipo.Price_min:N2} - ₹{ipo.Price_max:N2}).");
                }

                // Rule: Refund/unblock amount must match the payment record
                PaymentRecord? paymentRecord = await _context.PaymentRecords
                    .FirstOrDefaultAsync(r => r.RazorpayOrderId == application.Razorpay_order_id);

                decimal totalPaid = paymentRecord?.Amount ?? application.Payment_amount ?? application.Application_amount ?? ((decimal)application.Applied_shares * price);
                decimal debitedAmount = Math.Round(correctedShares * price, 2);
                decimal unblockedAmount = Math.Max(0m, totalPaid - debitedAmount);

                DateTime correctionDate = GetIndiaStandardTime();
                int previousShares = allotment.Allotted_shares;
                int previousLots = allotment.Allotted_lots;
                decimal previousDebited = allotment.Debited_amount;
                string previousStatus = allotment.Status;

                string newStatus = correctedShares == 0
                    ? "Not Allotted"
                    : (correctedShares == application.Applied_shares ? "Allotted" : "Partially Allotted");

                // Update Allotment
                allotment.Allotted_lots = model.CorrectedLots;
                allotment.Allotted_shares = correctedShares;
                allotment.Allotment_price = price;
                allotment.Debited_amount = debitedAmount;
                allotment.Unblocked_amount = unblockedAmount;
                allotment.Status = newStatus;
                allotment.Allotment_date = correctionDate;

                application.Status = newStatus;

                // Synchronize Payment Record
                if (paymentRecord != null)
                {
                    paymentRecord.UnblockedAmount = unblockedAmount;
                    paymentRecord.RefundStatus = unblockedAmount > 0m ? "Pending Unblock" : "Not Applicable";
                    if (unblockedAmount > 0m && string.IsNullOrWhiteSpace(paymentRecord.RefundReferenceId))
                    {
                        paymentRecord.RefundReferenceId = $"UNBLK-{correctionDate:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpper();
                    }
                    paymentRecord.UpdatedAt = correctionDate;
                }

                // Synchronize Demat Investment Holding
                var investment = await _context.Investments.FirstOrDefaultAsync(item =>
                    item.User_id == application.User_id && item.IPO_id == application.IPO_id && item.Status != "Closed");

                if (correctedShares > 0)
                {
                    if (investment is null)
                    {
                        investment = new Investment
                        {
                            User_id = application.User_id,
                            IPO_id = application.IPO_id,
                            Quantity = correctedShares,
                            Buy_price = price,
                            Buy_date = correctionDate,
                            Current_price = price,
                            Status = "Holding"
                        };
                        _context.Investments.Add(investment);
                    }
                    else
                    {
                        int adjustedQty = Math.Max(0, investment.Quantity - previousShares + correctedShares);
                        investment.Quantity = adjustedQty;
                        investment.Buy_price = price;
                        investment.Current_price = price;
                        investment.Status = adjustedQty > 0 ? "Holding" : "Closed";
                    }
                }
                else
                {
                    if (investment is not null)
                    {
                        int adjustedQty = Math.Max(0, investment.Quantity - previousShares);
                        investment.Quantity = adjustedQty;
                        investment.Status = adjustedQty > 0 ? "Holding" : "Closed";
                    }
                }

                _context.InvestmentTransactions.Add(new InvestmentTransaction
                {
                    Investment = investment,
                    Transaction_type = "ALLOTMENT_CORRECTION",
                    Quantity = correctedShares - previousShares,
                    Price = price,
                    Total_amount = debitedAmount - previousDebited,
                    Transaction_date = correctionDate
                });

                // Record Auditable Log
                _context.AuditLogs.Add(new AuditLog
                {
                    ActorUserId = adminId.Value,
                    Action = "AUTHORIZED_ALLOTMENT_CORRECTION",
                    EntityType = "IPOAllotment",
                    EntityId = allotment.Allotment_id.ToString(),
                    Details = $"Authorized allotment correction on {ipo.IPO_name} for Application #{application.Application_id}. Lots: {previousLots}->{model.CorrectedLots}, Shares: {previousShares}->{correctedShares}, Debited: ₹{previousDebited:N2}->₹{debitedAmount:N2}, Unblocked: ₹{allotment.Unblocked_amount:N2}->₹{unblockedAmount:N2}. Reason: {model.CorrectionReason.Trim()}.",
                    CreatedAt = correctionDate
                });

                // Disclose correction to Investor
                _context.SystemNotifications.Add(new SystemNotification
                {
                    User_id = application.User_id,
                    Title = $"Allotment Notice: Authorized Correction for {ipo.IPO_name}",
                    Message = $"An authorized administrative adjustment was processed for your {ipo.IPO_name} application #{application.Application_number ?? application.Application_id.ToString()}. Allotted: {correctedShares:N0} share(s) ({model.CorrectedLots} lot(s)). Debited: ₹{debitedAmount:N2}, Unblocked: ₹{unblockedAmount:N2}. Justification: {model.CorrectionReason.Trim()}.",
                    Created_at = correctionDate,
                    Is_read = false
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                User? correctedInvestor = await _context.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.User_id == application.User_id);
                if (correctedInvestor != null)
                {
                    await TrySendEventEmailAsync(
                        correctedInvestor,
                        $"Allotment Notice: Authorized Correction for {ipo.IPO_name}",
                        $"Dear {correctedInvestor.First_name},\n\nAn authorized administrative adjustment was processed for your {ipo.IPO_name} application #{application.Application_number ?? application.Application_id.ToString()}.\n\n- Allotted: {correctedShares:N0} share(s) ({model.CorrectedLots} lot(s))\n- Debited: ₹{debitedAmount:N2}\n- Unblocked: ₹{unblockedAmount:N2}\n- Justification: {model.CorrectionReason.Trim()}\n\nBest regards,\nIPO Investment Management");
                }

                TempData["AdminMessage"] = $"Authorized correction successfully executed and audited for Application #{model.ApplicationId}. Status updated to '{newStatus}' ({correctedShares} shares, Debited: ₹{debitedAmount:N2}, Unblocked: ₹{unblockedAmount:N2}).";
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                TempData["AdminMessage"] = $"Authorized correction failed: {ex.Message}";
            }

            return RedirectToAction(nameof(ApplicationDetails), new { applicationId = model.ApplicationId });
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

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                {
                    category = "ANNOUNCEMENT",
                    title = model.Title.Trim(),
                    message = model.Message.Trim(),
                    timestamp = GetIndiaStandardTime().ToString("hh:mm tt")
                });
            }
            catch { }

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

            await NotifyUserAndEmailAsync(
                user,
                "Account Deactivated",
                $"Your {user.Role ?? "user"} account was deactivated by the platform administrator.",
                "Account Deactivated",
                $"Dear {user.First_name},\n\nYour {user.Role ?? "user"} account on IPO Investment Management has been deactivated by the platform administrator.\n\nIf you believe this is an error, please contact the administrator.\n\nBest regards,\nIPO Investment Management");

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
            await NotifyUserAndEmailAsync(
                user,
                "Company Registration Approved",
                $"{company.Company_name} registration has been approved. You can now access the Company Dashboard.",
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

            User? companyUser = await _context.Users.FindAsync(company.User_id);
            if (companyUser is not null)
            {
                await NotifyUserAndEmailAsync(
                    companyUser,
                    "Company Registration Denied",
                    $"{company.Company_name} registration request was denied by the administrator.",
                    "Company Registration Denied",
                    $"Dear {companyUser.First_name},\n\nYour registration request for {company.Company_name} was denied by the platform administrator.\n\nPlease contact the administrator for more information.\n\nBest regards,\nIPO Investment Management");
            }

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

            DateTime nowIst = GetIndiaStandardTime();
            string targetStatus = _ipoStatusService.CalculateStatus(ipo, nowIst);
            ipo.Status = targetStatus;

            int? adminId = HttpContext.Session.GetInt32("User_id");
            string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";

            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = adminId,
                Action = "PublishIpo",
                EntityType = "IPO",
                EntityId = ipo.IPO_id.ToString(),
                Details = $"Admin #{adminId} ({adminName}) published '{ipo.IPO_name}'. Live status set to '{targetStatus}' at {nowIst:dd MMM yyyy, hh:mm:ss tt} IST. (Bidding window: {_ipoStatusService.GetOpeningDateTime(ipo):dd MMM yyyy, hh:mm tt} to {_ipoStatusService.GetClosingDateTime(ipo):dd MMM yyyy, hh:mm tt} IST).",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = nowIst
            });

            await _context.SaveChangesAsync();

            DateTime openingDt = _ipoStatusService.GetOpeningDateTime(ipo);
            DateTime closingDt = _ipoStatusService.GetClosingDateTime(ipo);

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveIpoStatusChange", new
                {
                    ipoId = ipo.IPO_id,
                    ipoName = ipo.IPO_name,
                    previousStatus = "Approved",
                    status = targetStatus,
                    openingDateTime = openingDt.ToString("dd MMM yyyy, hh:mm tt"),
                    closingDateTime = closingDt.ToString("dd MMM yyyy, hh:mm tt"),
                    timestamp = nowIst.ToString("hh:mm:ss tt"),
                    istNow = nowIst.ToString("dd MMM yyyy, hh:mm:ss tt")
                });
            }
            catch { }

            string notifTitle = string.Equals(targetStatus, "Open", StringComparison.OrdinalIgnoreCase)
                ? $"IPO Bidding Open: {ipo.IPO_name}"
                : $"Upcoming IPO Announced: {ipo.IPO_name}";

            string notifMessage = string.Equals(targetStatus, "Open", StringComparison.OrdinalIgnoreCase)
                ? $"Bidding is now OPEN for {ipo.IPO_name} (Price Band: ₹{ipo.Price_min:N2} - ₹{ipo.Price_max:N2}, Lot size: {ipo.Lot_size} shares). The bidding window closes on {closingDt:dd MMM yyyy, hh:mm tt} IST. Submit your bid now."
                : $"{ipo.IPO_name} has been published. Bidding opens on {openingDt:dd MMM yyyy, hh:mm tt} IST (Price Band: ₹{ipo.Price_min:N2} - ₹{ipo.Price_max:N2}). Check offering details and add to watchlist.";

            User[] investors = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .ToArrayAsync();
            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(investors.Select(investor => new SystemNotification
            {
                User_id = investor.User_id,
                Title = notifTitle,
                Message = notifMessage,
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
            foreach (User investor in investors)
            {
                await TrySendEventEmailAsync(
                    investor,
                    notifTitle,
                    $"{notifMessage}\n\nLog in to your investor dashboard to review the offering.");
            }

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                {
                    category = string.Equals(targetStatus, "Open", StringComparison.OrdinalIgnoreCase) ? "IPO OPENING" : "IPO ANNOUNCEMENT",
                    title = notifTitle,
                    message = notifMessage,
                    timestamp = nowIst.ToString("hh:mm tt")
                });
            }
            catch { }

            await NotifyCompanyOwnerAsync(
                ipo.Company_id,
                $"IPO Published: {ipo.IPO_name}",
                $"{ipo.IPO_name} has been published by Admin. Live status is now '{targetStatus}' (Bidding window: {openingDt:dd MMM yyyy, hh:mm tt} to {closingDt:dd MMM yyyy, hh:mm tt} IST).");

            TempData["AdminMessage"] = $"{ipo.IPO_name} was published and status is set to '{targetStatus}'.";
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
            await NotifyCompanyOwnerAsync(
                ipo.Company_id,
                $"IPO Rejected: {ipo.IPO_name}",
                $"Your IPO submission '{ipo.IPO_name}' was rejected by the administrator and is no longer available.");
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

            string previousStatus = ipo.Status ?? "Unknown";
            ipo.Status = "Cancelled";

            int? adminId = HttpContext.Session.GetInt32("User_id");
            string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";

            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = adminId,
                Action = "CancelIpo",
                EntityType = "IPO",
                EntityId = ipo.IPO_id.ToString(),
                Details = $"Admin #{adminId} ({adminName}) manually cancelled IPO '{ipo.IPO_name}' (Previous status: '{previousStatus}').",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveIpoStatusChange", new
                {
                    ipoId = ipo.IPO_id,
                    ipoName = ipo.IPO_name,
                    previousStatus = previousStatus,
                    status = "Cancelled",
                    timestamp = _clock.NowIst.ToString("hh:mm:ss tt")
                });
            }
            catch { }

            await NotifyCompanyOwnerAsync(
                ipo.Company_id,
                $"IPO Cancelled: {ipo.IPO_name}",
                $"Your IPO '{ipo.IPO_name}' was cancelled by the administrator (Previous status: '{previousStatus}').");

            await NotifyIpoApplicantsAsync(
                ipo.IPO_id,
                $"IPO Cancelled: {ipo.IPO_name}",
                $"{ipo.IPO_name} has been cancelled by the administrator. Please check your application details for refund and status updates.",
                sendEmail: true);

            TempData["AdminMessage"] = $"{ipo.IPO_name} was cancelled.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateIpoSchedule(
            int ipoId,
            DateTime openDate,
            string? openTime,
            DateTime closeDate,
            string? closeTime)
        {
            if (!IsAdmin()) return Forbid();

            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();

            TimeSpan ot = TimeSpan.TryParse(openTime, out var parsedOt) ? parsedOt : _ipoStatusService.GetConfiguredOpeningTime();
            TimeSpan ct = TimeSpan.TryParse(closeTime, out var parsedCt) ? parsedCt : _ipoStatusService.GetConfiguredClosingTime();

            DateTime openingDateTime = openDate.Date.Add(ot);
            DateTime closingDateTime = closeDate.Date.Add(ct);

            if (closingDateTime <= openingDateTime)
            {
                TempData["AdminMessage"] = "Closing date and time must be after opening date and time.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }

            ipo.Open_date = openDate.Date;
            ipo.Opening_time = ot;
            ipo.Close_date = closeDate.Date;
            ipo.Closing_time = ct;

            DateTime nowIst = GetIndiaStandardTime();
            string oldStatus = ipo.Status ?? "Upcoming";
            string newStatus = _ipoStatusService.CalculateStatus(ipo, nowIst);

            if (!string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase))
            {
                ipo.Status = newStatus;
            }

            int? adminId = HttpContext.Session.GetInt32("User_id");
            string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";

            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = adminId,
                Action = "UpdateIpoSchedule",
                EntityType = "IPO",
                EntityId = ipo.IPO_id.ToString(),
                Details = $"Admin #{adminId} ({adminName}) updated schedule for '{ipo.IPO_name}': Open: {openingDateTime:dd MMM yyyy, hh:mm tt} IST, Close: {closingDateTime:dd MMM yyyy, hh:mm tt} IST. Status: '{ipo.Status}'.",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = nowIst
            });

            await _context.SaveChangesAsync();

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveIpoStatusChange", new
                {
                    ipoId = ipo.IPO_id,
                    ipoName = ipo.IPO_name,
                    previousStatus = oldStatus,
                    status = ipo.Status,
                    openingDateTime = openingDateTime.ToString("dd MMM yyyy, hh:mm tt"),
                    closingDateTime = closingDateTime.ToString("dd MMM yyyy, hh:mm tt"),
                    timestamp = nowIst.ToString("hh:mm:ss tt"),
                    istNow = nowIst.ToString("dd MMM yyyy, hh:mm:ss tt")
                });
            }
            catch { }

            await NotifyCompanyOwnerAsync(
                ipo.Company_id,
                $"IPO Schedule Updated: {ipo.IPO_name}",
                $"The administrator updated the schedule for '{ipo.IPO_name}'. Opening: {openingDateTime:dd MMM yyyy, hh:mm tt} IST, Closing: {closingDateTime:dd MMM yyyy, hh:mm tt} IST. Status: '{ipo.Status}'.");

            if (IsPublicIpoStatus(ipo.Status))
            {
                await NotifyActiveInvestorsAsync(
                    $"IPO Schedule Updated: {ipo.IPO_name}",
                    $"{ipo.IPO_name} schedule was updated by Admin. Bidding window: {openingDateTime:dd MMM yyyy, hh:mm tt} to {closingDateTime:dd MMM yyyy, hh:mm tt} IST (Status: {ipo.Status}).");
            }

            TempData["AdminMessage"] = $"Schedule updated for {ipo.IPO_name}. Status is now '{ipo.Status}'.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeIpoStatus(int ipoId, string status)
        {
            if (!IsAdmin()) return Forbid();
            string[] validStatuses = { "Pending", "Approved", "Upcoming", "Open", "Closed", "Listed", "Rejected", "Cancelled" };
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

            int? adminId = HttpContext.Session.GetInt32("User_id");
            string adminName = HttpContext.Session.GetString("UserName") ?? "Admin";

            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = adminId,
                Action = "ChangeIpoStatus",
                EntityType = "IPO",
                EntityId = ipo.IPO_id.ToString(),
                Details = $"Admin #{adminId} ({adminName}) manually changed status for '{ipo.IPO_name}' from '{previousStatus}' to '{selectedStatus}'.",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveIpoStatusChange", new
                {
                    ipoId = ipo.IPO_id,
                    ipoName = ipo.IPO_name,
                    previousStatus = previousStatus,
                    status = selectedStatus,
                    timestamp = _clock.NowIst.ToString("hh:mm:ss tt")
                });
            }
            catch { }

            if (!string.Equals(selectedStatus, previousStatus, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(selectedStatus, "Approved", StringComparison.OrdinalIgnoreCase))
                {
                    await NotifyCompanyIpoApprovedAsync(ipo);
                }
                else
                {
                    await NotifyCompanyOwnerAsync(
                        ipo.Company_id,
                        $"IPO Status Updated: {ipo.IPO_name}",
                        $"The administrator updated '{ipo.IPO_name}' status from '{previousStatus ?? "Unknown"}' to '{selectedStatus}'.");
                }

                if (string.Equals(selectedStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    await NotifyIpoApplicantsAsync(
                        ipo.IPO_id,
                        $"IPO Status Updated: {ipo.IPO_name}",
                        $"{ipo.IPO_name} status was changed to '{selectedStatus}' by the administrator.",
                        sendEmail: true);
                }
                else if (IsPublicIpoStatus(selectedStatus))
                {
                    await NotifyActiveInvestorsAsync(
                        $"IPO Status Updated: {ipo.IPO_name}",
                        $"{ipo.IPO_name} is now '{selectedStatus}' (updated by administrator).");
                }
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
                Opening_time = _ipoStatusService.GetConfiguredOpeningTime(),
                Closing_time = _ipoStatusService.GetConfiguredClosingTime(),
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
                Created_at = _clock.NowIst
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

            // Immediately send email notification to the Admin and broadcast update
            await NotifyAdminsNewIpoSubmissionAsync(ipo, company!, submission);

            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveBroadcastMessage", new
                {
                    title = "New IPO Submission",
                    message = $"{company!.Company_name} has submitted a new IPO request for '{ipo.IPO_name}'.",
                    category = "IPO",
                    timestamp = _clock.NowIst.ToString("hh:mm:ss tt")
                });
            }
            catch { }

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
            model.SipDashboard = await _stockSipService.GetSipDashboardAsync(userId);
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
            model.SimulatedCashBalance = investor?.SimulatedCashBalance ?? 0m;
            model.WithdrawalHistory = await _context.WalletWithdrawals
                .AsNoTracking()
                .Where(w => w.UserId == userId)
                .OrderByDescending(w => w.CreatedAt)
                .ThenByDescending(w => w.WithdrawalId)
                .Take(15)
                .ToListAsync();
            model.TotalWithdrawnAmount = await _context.WalletWithdrawals
                .AsNoTracking()
                .Where(w => w.UserId == userId && w.Status == "Completed")
                .SumAsync(w => (decimal?)w.Amount) ?? 0m;
            DateTime indiaNow = GetIndiaStandardTime();
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

            // Calculate Today's Profit / Loss
            decimal todayProfitLoss = 0m;
            decimal prevDayBasis = 0m;
            foreach (var holding in portfolio.Holdings)
            {
                var marketRow = model.Market.Listings.FirstOrDefault(row => row.IpoId == holding.IpoId);
                decimal priceChange = 0m;
                if (marketRow?.PriceChange.HasValue == true)
                {
                    priceChange = marketRow.PriceChange.Value;
                }
                else if (marketRow?.PreviousClose.HasValue == true && marketRow.PreviousClose.Value > 0)
                {
                    priceChange = holding.CurrentPrice - marketRow.PreviousClose.Value;
                }
                else
                {
                    priceChange = holding.CurrentPrice - holding.AverageBuyPrice;
                }

                todayProfitLoss += holding.Quantity * priceChange;
                decimal prevHoldingVal = (holding.CurrentPrice - priceChange) * holding.Quantity;
                prevDayBasis += prevHoldingVal > 0 ? prevHoldingVal : holding.InvestedAmount;
            }

            DateTime todayIst = indiaNow.Date;
            var todaySales = portfolio.Transactions
                .Where(t => t.TransactionDate.Date == todayIst && string.Equals(t.TransactionType, "SELL", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var sale in todaySales)
            {
                var holding = portfolio.Holdings.FirstOrDefault(h => h.IpoName == sale.IpoName);
                decimal avgBuy = holding?.AverageBuyPrice ?? sale.Price;
                todayProfitLoss += sale.Quantity * (sale.Price - avgBuy);
            }

            model.TodayProfitLoss = todayProfitLoss;
            model.TodayProfitLossPercentage = prevDayBasis > 0
                ? Math.Round((todayProfitLoss / prevDayBasis) * 100m, 2)
                : (model.InvestmentTotal > 0 ? Math.Round((todayProfitLoss / model.InvestmentTotal) * 100m, 2) : 0m);
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

            List<string> appOrderIds = model.RecentApplications
                .Where(a => !string.IsNullOrEmpty(a.Razorpay_order_id))
                .Select(a => a.Razorpay_order_id!)
                .Distinct()
                .ToList();
            if (appOrderIds.Count > 0)
            {
                var payments = await _context.PaymentRecords
                    .AsNoTracking()
                    .Where(p => appOrderIds.Contains(p.RazorpayOrderId))
                    .ToListAsync();
                model.ApplicationPayments = payments
                    .GroupBy(p => p.RazorpayOrderId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            List<IPO> publishedIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Upcoming" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
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
                ipo => _ipoStatusService.CalculateStatus(ipo, indiaNow));
            model.OpenIpoCount = publishedIpos.Count(ipo => model.MarketStatuses.GetValueOrDefault(ipo.IPO_id) == "Open");

            IEnumerable<IPO> marketIpos = publishedIpos;
            if (!string.IsNullOrWhiteSpace(marketStatus))
            {
                marketIpos = marketIpos.Where(ipo =>
                    string.Equals(model.MarketStatuses.GetValueOrDefault(ipo.IPO_id), marketStatus.Trim(), StringComparison.OrdinalIgnoreCase));
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

        private string GetInvestorMarketStatus(IPO ipo, DateTime now)
        {
            return _ipoStatusService.CalculateStatus(ipo, now);
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
            List<WalletWithdrawal> withdrawalHistory = await _context.WalletWithdrawals
                .AsNoTracking()
                .Where(w => w.UserId == userId)
                .OrderByDescending(w => w.CreatedAt)
                .ThenByDescending(w => w.WithdrawalId)
                .Take(15)
                .ToListAsync();
            decimal totalWithdrawnAmount = await _context.WalletWithdrawals
                .AsNoTracking()
                .Where(w => w.UserId == userId && w.Status == "Completed")
                .SumAsync(w => (decimal?)w.Amount) ?? 0m;

            return new PortfolioViewModel
            {
                InvestedAmount = totalInvested,
                CurrentValue = totalCurrentValue,
                RealizedProfitLoss = realizedProfitLoss,
                UnrealizedProfitLoss = unrealizedProfitLoss,
                TotalReturn = totalReturn,
                ReturnPercentage = totalInvested == 0m ? 0m : totalReturn / totalInvested * 100m,
                SimulatedCashBalance = investorUser?.SimulatedCashBalance ?? 100000m,
                InvestorUpiId = investorUser?.UpiId ?? string.Empty,
                TotalWithdrawnAmount = totalWithdrawnAmount,
                WithdrawalHistory = withdrawalHistory,
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
                PopulateIpoFundamentalMetrics(detail, financials);
                return detail;
            }).ToList();
        }

        private static (decimal? Roe, decimal? RevenueGrowth, decimal? ProfitGrowth) ComputeFundamentalMetrics(
            IReadOnlyList<IPOFinancial> financials)
        {
            IPOFinancial? latestFinancial = financials.FirstOrDefault();
            IPOFinancial? previousFinancial = latestFinancial is null
                ? null
                : financials
                    .Where(financial => financial.Financial_year < latestFinancial.Financial_year)
                    .OrderByDescending(financial => financial.Financial_year)
                    .FirstOrDefault();

            decimal? roe = latestFinancial?.Profit.HasValue == true &&
                           latestFinancial.Net_worth.HasValue &&
                           latestFinancial.Net_worth.Value > 0m
                ? latestFinancial.Profit.Value / latestFinancial.Net_worth.Value * 100m
                : null;
            decimal? revenueGrowth = CalculateGrowth(latestFinancial?.Revenue, previousFinancial?.Revenue);
            decimal? profitGrowth = CalculateGrowth(latestFinancial?.Profit, previousFinancial?.Profit);
            return (roe, revenueGrowth, profitGrowth);
        }

        private static void PopulateIpoFundamentalMetrics(
            CompanyIpoDetailsViewModel detail,
            IReadOnlyList<IPOFinancial> allIpoFinancials)
        {
            List<IPOFinancial> ipoFinancials = allIpoFinancials
                .Where(financial => financial.IPO_id == detail.Ipo.IPO_id)
                .OrderByDescending(financial => financial.Financial_year)
                .ToList();

            var derived = ComputeFundamentalMetrics(ipoFinancials);

            decimal? listingPrice = detail.Listing?.NSE_listing_price ?? detail.Listing?.BSE_listing_price;
            decimal? listingPerformance = detail.Listing?.Listing_gain_percentage;
            if (!listingPerformance.HasValue &&
                detail.Listing?.Issue_price > 0 &&
                listingPrice.HasValue)
            {
                listingPerformance = (listingPrice.Value - detail.Listing.Issue_price.Value) /
                    detail.Listing.Issue_price.Value * 100m;
            }

            detail.ComparisonRoe = detail.Analysis?.ROE ?? derived.Roe;
            detail.ComparisonRevenueGrowth = detail.Analysis?.Revenue_growth ?? derived.RevenueGrowth;
            detail.ComparisonProfitGrowth = detail.Analysis?.Profit_growth ?? derived.ProfitGrowth;
            detail.ComparisonListingPerformance = listingPerformance;
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
            model.Market = await _stockMarketService.GetDashboardAsync();
            model.UserCount = await _context.Users.CountAsync(user =>
                user.Is_active &&
                user.Role == "Investor");
            model.CompanyCount = await _context.Companies.CountAsync();
            model.IpoCount = await _context.IPOs.CountAsync();
            model.PendingCompanyCount = await _context.Companies.CountAsync(company => company.Approval_status == "Pending");
            model.PendingApplicationCount = await _context.IPOApplications.CountAsync(application =>
                application.Status == "Submitted");
            model.PendingIpoCount = await _context.IPOs.CountAsync(ipo => ipo.Status == "Pending");
            DateTime today = GetIndiaStandardTime().Date;
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

            // Always populate AllAdminIpos for Admin dashboard to view all available IPOs (Listed, Upcoming, Open, Closed)
            model.AllAdminIpos = await GetAdminSearchIposListAsync(adminIpoQuery, adminIpoStatus, adminIpoType);
            model.AdminIpoSearchQuery = adminIpoQuery;
            model.AdminIpoSearchStatus = adminIpoStatus;
            model.AdminIpoSearchType = adminIpoType;

            // Recalculate distinct status counts based on effective statuses
            model.OpenIpoCount = model.AllAdminIpos.Count(i => i.EffectiveStatus == "Open");
            model.UpcomingIpoCount = model.AllAdminIpos.Count(i => i.EffectiveStatus == "Upcoming");
            model.ClosedIpoCount = model.AllAdminIpos.Count(i => i.EffectiveStatus == "Closed");
            model.ListedIpoCount = model.AllAdminIpos.Count(i => i.EffectiveStatus == "Listed");
            model.ActiveIpoCount = model.OpenIpoCount;

            // Populate Admin Payment Management data
            List<PaymentRecord> adminPayments = await _context.PaymentRecords
                .AsNoTracking()
                .OrderByDescending(record => record.CreatedAt)
                .ToListAsync();

            model.AdminPayments = adminPayments;

            int[] paymentUserIds = adminPayments.Select(p => p.UserId).Distinct().ToArray();
            int[] paymentIpoIds = adminPayments.Select(p => p.IpoId).Distinct().ToArray();
            string[] paymentOrderIds = adminPayments.Select(p => p.RazorpayOrderId).Where(o => !string.IsNullOrEmpty(o)).Distinct().ToArray();

            var paymentUsers = await _context.Users
                .AsNoTracking()
                .Where(u => paymentUserIds.Contains(u.User_id))
                .Select(u => new { u.User_id, FullName = (u.First_name + " " + u.Last_name).Trim(), u.Email, u.DematAccountNumber, u.UpiId })
                .ToListAsync();

            model.PaymentUserNames = paymentUsers.ToDictionary(u => u.User_id, u => u.FullName);
            model.PaymentUserEmails = paymentUsers.ToDictionary(u => u.User_id, u => u.Email ?? "N/A");
            model.PaymentUserDemats = paymentUsers.ToDictionary(u => u.User_id, u => u.DematAccountNumber ?? "N/A");
            model.PaymentUserUpis = paymentUsers.ToDictionary(u => u.User_id, u => u.UpiId ?? "N/A");

            var paymentIpos = await _context.IPOs
                .AsNoTracking()
                .Where(i => paymentIpoIds.Contains(i.IPO_id))
                .Select(i => new { i.IPO_id, i.IPO_name })
                .ToListAsync();

            foreach (var ipo in paymentIpos)
            {
                model.PaymentIpoNames[ipo.IPO_id] = ipo.IPO_name;
            }

            var paymentApps = await _context.IPOApplications
                .AsNoTracking()
                .Where(a => paymentOrderIds.Contains(a.Razorpay_order_id))
                .Select(a => new { a.Application_id, a.Razorpay_order_id, a.Application_number })
                .ToListAsync();

            foreach (var app in paymentApps)
            {
                if (!string.IsNullOrEmpty(app.Razorpay_order_id))
                {
                    model.PaymentApplicationIds[app.Razorpay_order_id] = app.Application_id;
                }
                if (!string.IsNullOrEmpty(app.Application_number))
                {
                    model.PaymentApplicationIds[app.Application_number] = app.Application_id;
                }
            }

            model.SuccessfulPaymentCount = adminPayments.Count(p => p.Status == "Captured" || p.Status == "Verified");
            model.FailedPaymentCount = adminPayments.Count(p => p.Status == "Failed");
            model.PendingPaymentCount = adminPayments.Count(p => p.Status == "Created" || p.Status == "Pending");
            model.RefundedPaymentCount = adminPayments.Count(p => p.RefundStatus == "Completed" || p.RefundStatus == "Pending Unblock" || p.UnblockedAmount > 0m || p.Status == "Refunded");

            model.SuccessfulPaymentAmount = adminPayments.Where(p => p.Status == "Captured" || p.Status == "Verified").Sum(p => p.Amount);
            model.FailedPaymentAmount = adminPayments.Where(p => p.Status == "Failed").Sum(p => p.Amount);
            model.PendingPaymentAmount = adminPayments.Where(p => p.Status == "Created" || p.Status == "Pending").Sum(p => p.Amount);
            model.RefundedPaymentAmount = adminPayments.Sum(p => p.UnblockedAmount > 0m ? p.UnblockedAmount : (p.RefundStatus == "Completed" ? p.Amount : 0m));
            model.TotalPaymentVolume = adminPayments.Sum(p => p.Amount);

            await LoadMarketDataAsync(model);
        }

        private async Task<List<AdminSearchIpoItem>> GetAdminSearchIposListAsync(
            string? adminIpoQuery = null,
            string? adminIpoStatus = null,
            string? adminIpoType = null)
        {
            var allIposQuery = _context.IPOs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(adminIpoType))
            {
                allIposQuery = allIposQuery.Where(i => i.IPO_type == adminIpoType);
            }
            var allIposList = await allIposQuery.OrderByDescending(i => i.Created_at).ToListAsync();

            int[] allIpoIds = allIposList.Select(i => i.IPO_id).ToArray();
            int[] allCompanyIds = allIposList.Select(i => i.Company_id).Distinct().ToArray();

            var allCompanies = await _context.Companies
                .AsNoTracking()
                .Where(c => allCompanyIds.Contains(c.Company_id))
                .ToDictionaryAsync(c => c.Company_id);

            var ipoAppStats = await _context.IPOApplications
                .AsNoTracking()
                .Where(a => allIpoIds.Contains(a.IPO_id) && a.Status != "Rejected" && a.Status != "Cancelled")
                .GroupBy(a => a.IPO_id)
                .Select(g => new
                {
                    IpoId = g.Key,
                    Count = g.Count(),
                    AppliedShares = g.Sum(a => (long)a.Applied_shares)
                })
                .ToDictionaryAsync(g => g.IpoId);

            var latestGmpQuotes = await _context.IPOGMPHistories
                .AsNoTracking()
                .Where(g => allIpoIds.Contains(g.IPO_id))
                .OrderBy(g => g.RecordedDate)
                .ThenBy(g => g.Gmp_id)
                .ToListAsync();

            var latestGmpMap = latestGmpQuotes
                .GroupBy(g => g.IPO_id)
                .ToDictionary(g => g.Key, g => g.Last());

            var allListings = await _context.IPOListings
                .AsNoTracking()
                .Where(l => allIpoIds.Contains(l.IPO_id))
                .ToDictionaryAsync(l => l.IPO_id);

            var allFinancials = await _context.IPOFinancials
                .AsNoTracking()
                .Where(f => allIpoIds.Contains(f.IPO_id))
                .OrderByDescending(f => f.Financial_year)
                .ToListAsync();
            var latestFinancialMap = allFinancials.GroupBy(f => f.IPO_id).ToDictionary(g => g.Key, g => g.First());

            var allAnalyses = await _context.IPOAnalyses
                .AsNoTracking()
                .Where(a => allIpoIds.Contains(a.IPO_id))
                .OrderByDescending(a => a.Updated_at)
                .ToListAsync();
            var latestAnalysisMap = allAnalyses.GroupBy(a => a.IPO_id).ToDictionary(g => g.Key, g => g.First());

            DateTime istNow = GetIndiaStandardTime();
            DateTime todayDate = istNow.Date;

            var items = allIposList.Select(ipo =>
            {
                var comp = allCompanies.GetValueOrDefault(ipo.Company_id);
                string compName = comp?.Company_name ?? "Company";
                var gmp = latestGmpMap.GetValueOrDefault(ipo.IPO_id);
                decimal basePrice = ipo.Price_max ?? ipo.Price_min ?? 0m;
                decimal? currentGmp = gmp?.GMPPrice;
                decimal? estPrice = gmp?.EstimatedListingPrice ?? (currentGmp.HasValue ? basePrice + currentGmp.Value : (decimal?)null);
                decimal? gainPct = gmp?.ExpectedListingGainPercentage ?? (basePrice > 0 && currentGmp.HasValue ? Math.Round((currentGmp.Value / basePrice) * 100m, 2) : (decimal?)null);

                var timing = CheckAllotmentTiming(ipo, todayDate);
                var appStat = ipoAppStats.GetValueOrDefault(ipo.IPO_id);
                int appCount = appStat?.Count ?? 0;
                long appliedShares = appStat?.AppliedShares ?? 0L;
                long offeredShares = ipo.Total_shares ?? 0L;
                decimal subMultiple = offeredShares > 0L ? Math.Round((decimal)appliedShares / offeredShares, 2) : 0m;

                string effectiveStatus = _ipoStatusService.CalculateStatus(ipo, istNow);
                if (string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveStatus = "Listed";
                }

                return new AdminSearchIpoItem
                {
                    Ipo = ipo,
                    CompanyName = compName,
                    Company = comp,
                    ApplicationCount = appCount,
                    AppliedShares = appliedShares,
                    SubscriptionMultiple = subMultiple,
                    CurrentGmp = currentGmp,
                    EstimatedListingPrice = estPrice,
                    ExpectedGainPercentage = gainPct,
                    LatestGmpRecord = gmp,
                    Listing = allListings.GetValueOrDefault(ipo.IPO_id),
                    Financial = latestFinancialMap.GetValueOrDefault(ipo.IPO_id),
                    Analysis = latestAnalysisMap.GetValueOrDefault(ipo.IPO_id),
                    AllotmentWindowOpen = timing.AllotmentWindowOpen,
                    EffectiveStatus = effectiveStatus
                };
            }).ToList();

            if (!string.IsNullOrWhiteSpace(adminIpoStatus))
            {
                items = items.Where(item => 
                    string.Equals(item.EffectiveStatus, adminIpoStatus, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Ipo.Status, adminIpoStatus, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }

            if (!string.IsNullOrWhiteSpace(adminIpoQuery))
            {
                string q = adminIpoQuery.Trim().ToLower();
                items = items
                    .Where(item => item.Ipo.IPO_name.ToLower().Contains(q) || 
                                   item.CompanyName.ToLower().Contains(q) ||
                                   (item.Listing?.StockSymbol != null && item.Listing.StockSymbol.ToLower().Contains(q)))
                    .ToList();
            }

            return items;
        }

        private bool IsAdmin()
        {
            return string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsInvestor()
        {
            return string.Equals(HttpContext.Session.GetString("UserRole"), "Investor", StringComparison.OrdinalIgnoreCase);
        }


        private DateTime GetIndiaStandardTime()
        {
            return _clock.NowIst;
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
            model.Market = await _stockMarketService.GetDashboardAsync();
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
            model.CompanyIpoDetails = model.CompanyIpos.Select(ipo =>
            {
                var detail = new CompanyIpoDetailsViewModel
                {
                    Ipo = ipo,
                    ApplicationCount = applicationCounts.GetValueOrDefault(ipo.IPO_id),
                    Financial = financials.FirstOrDefault(financial => financial.IPO_id == ipo.IPO_id),
                    Analysis = analyses.FirstOrDefault(analysis => analysis.IPO_id == ipo.IPO_id),
                    Listing = listings.FirstOrDefault(listing => listing.IPO_id == ipo.IPO_id)
                };
                PopulateIpoFundamentalMetrics(detail, financials);
                return detail;
            }).ToList();
            model.CompanyCount = model.Companies.Count;
            model.IpoCount = model.CompanyIpos.Count;
            DateTime today = GetIndiaStandardTime().Date;
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
                .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Upcoming" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
                .OrderByDescending(ipo => ipo.Created_at)
                .ToListAsync();

            model.PublishedIpoDetails = await LoadIpoDetailsAsync(publishedIpos);
            DateTime indiaNow = GetIndiaStandardTime();
            model.MarketStatuses = publishedIpos.ToDictionary(
                ipo => ipo.IPO_id,
                ipo => GetInvestorMarketStatus(ipo, indiaNow));
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
                .Take(15)
                .ToListAsync();
        }

        private static bool IsPublicIpoStatus(string? status)
        {
            return string.Equals(status, "Upcoming", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Listed", StringComparison.OrdinalIgnoreCase);
        }

        private async Task NotifyUserAndEmailAsync(
            User user,
            string title,
            string message,
            string? emailSubject = null,
            string? emailBody = null)
        {
            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = user.User_id,
                Title = title,
                Message = message,
                Created_at = DateTime.UtcNow,
                Is_read = false
            });
            await _context.SaveChangesAsync();
            await TrySendEventEmailAsync(
                user,
                emailSubject ?? title,
                emailBody ?? message);
        }

        private async Task NotifyCompanyOwnerAsync(
            int companyId,
            string title,
            string message,
            string? emailSubject = null,
            string? emailBody = null)
        {
            User? companyUser = await _context.Companies
                .Where(company => company.Company_id == companyId)
                .Join(_context.Users, company => company.User_id, user => user.User_id, (_, user) => user)
                .FirstOrDefaultAsync();
            if (companyUser is null)
            {
                return;
            }

            await NotifyUserAndEmailAsync(companyUser, title, message, emailSubject, emailBody);
        }

        private async Task NotifyActiveInvestorsAsync(string title, string message)
        {
            int[] investorIds = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .Select(user => user.User_id)
                .ToArrayAsync();
            if (investorIds.Length == 0)
            {
                return;
            }

            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(investorIds.Select(investorId => new SystemNotification
            {
                User_id = investorId,
                Title = title,
                Message = message,
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
        }

        private async Task NotifyIpoApplicantsAsync(int ipoId, string title, string message, bool sendEmail = false)
        {
            int[] applicantUserIds = await _context.IPOApplications
                .AsNoTracking()
                .Where(app => app.IPO_id == ipoId &&
                    app.Status != "Cancelled" &&
                    app.Status != "Rejected")
                .Select(app => app.User_id)
                .Distinct()
                .ToArrayAsync();
            if (applicantUserIds.Length == 0)
            {
                return;
            }

            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(applicantUserIds.Select(applicantId => new SystemNotification
            {
                User_id = applicantId,
                Title = title,
                Message = message,
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();

            if (!sendEmail)
            {
                return;
            }

            User[] applicants = await _context.Users
                .AsNoTracking()
                .Where(user => applicantUserIds.Contains(user.User_id) && user.Is_active)
                .ToArrayAsync();
            foreach (User applicant in applicants)
            {
                await TrySendEventEmailAsync(applicant, title, message);
            }
        }

        private async Task TrySendRefundEmailAsync(PaymentRecord payment)
        {
            User? investor = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.User_id == payment.UserId);
            if (investor is null)
            {
                return;
            }

            await TrySendEventEmailAsync(
                investor,
                "Mandate Amount Unblocked / Refunded",
                $"Dear {investor.First_name},\n\n₹{payment.UnblockedAmount:N2} for application #{payment.ApplicationNumber} has been released and credited back to your investor wallet.\n\nRefund Reference: {payment.RefundReferenceId}\n\nBest regards,\nIPO Investment Management");
        }

        private async Task NotifyCompanyIpoApprovedAsync(IPO ipo)
        {
            await NotifyCompanyOwnerAsync(
                ipo.Company_id,
                "IPO Approved",
                $"{ipo.IPO_name} has been approved and can now be published.",
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

        private async Task NotifyAdminsNewIpoSubmissionAsync(IPO ipo, Company company, IpoSubmissionViewModel? submission = null)
        {
            List<User> adminUsers = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Admin" && user.Is_active)
                .ToListAsync();

            string subject = $"New IPO Submission Request: {ipo.IPO_name} ({company.Company_name})";

            string issueSizeText = ipo.Issue_size.HasValue ? $"₹{ipo.Issue_size.Value:N2}" : "Not specified";
            string priceBandText = (ipo.Price_min.HasValue && ipo.Price_max.HasValue)
                ? $"₹{ipo.Price_min.Value:N2} - ₹{ipo.Price_max.Value:N2}"
                : (ipo.Price_min.HasValue ? $"₹{ipo.Price_min.Value:N2}" : "Not specified");
            string lotSizeText = ipo.Lot_size.HasValue ? $"{ipo.Lot_size.Value} shares" : "Not specified";
            string totalSharesText = ipo.Total_shares.HasValue ? $"{ipo.Total_shares.Value:N0}" : "Not specified";
            string allotmentDateText = ipo.Allotment_date.HasValue ? ipo.Allotment_date.Value.ToString("dd MMM yyyy") : "TBD";
            string fundsUnblockDateText = ipo.Funds_unblock_date.HasValue ? ipo.Funds_unblock_date.Value.ToString("dd MMM yyyy") : "TBD";
            string listingDateText = ipo.Listing_date.HasValue ? ipo.Listing_date.Value.ToString("dd MMM yyyy") : "TBD";

            var sb = new StringBuilder();
            sb.AppendLine($"A new IPO submission request has been submitted by {company.Company_name} and is waiting for your review and approval.");
            sb.AppendLine();
            sb.AppendLine("IPO Offering Details:");
            sb.AppendLine("--------------------------------------------------");
            sb.AppendLine($"Company Name     : {company.Company_name}");
            if (!string.IsNullOrWhiteSpace(company.CIN))
            {
                sb.AppendLine($"CIN              : {company.CIN}");
            }
            if (!string.IsNullOrWhiteSpace(company.Industry))
            {
                sb.AppendLine($"Industry         : {company.Industry}");
            }
            sb.AppendLine($"IPO Name         : {ipo.IPO_name}");
            sb.AppendLine($"Issue Size       : {issueSizeText}");
            sb.AppendLine($"Price Band       : {priceBandText}");
            sb.AppendLine($"Lot Size         : {lotSizeText}");
            sb.AppendLine($"Total Shares     : {totalSharesText}");
            sb.AppendLine($"Opening Date     : {ipo.Open_date:dd MMM yyyy}");
            sb.AppendLine($"Closing Date     : {ipo.Close_date:dd MMM yyyy}");
            sb.AppendLine($"Allotment Date   : {allotmentDateText}");
            sb.AppendLine($"Funds Unblock    : {fundsUnblockDateText}");
            sb.AppendLine($"Listing Date     : {listingDateText}");
            sb.AppendLine($"Status           : Pending Admin Review");
            sb.AppendLine($"Submission Date  : {_clock.NowIst:dd MMM yyyy hh:mm tt}");
            sb.AppendLine("--------------------------------------------------");

            if (submission != null && submission.FinancialYear.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine($"Financial Information (FY {submission.FinancialYear.Value}):");
                sb.AppendLine("--------------------------------------------------");
                sb.AppendLine($"Revenue          : {(submission.Revenue.HasValue ? $"₹{submission.Revenue.Value:N2}" : "Not specified")}");
                sb.AppendLine($"Profit           : {(submission.Profit.HasValue ? $"₹{submission.Profit.Value:N2}" : "Not specified")}");
                sb.AppendLine($"Net Worth        : {(submission.NetWorth.HasValue ? $"₹{submission.NetWorth.Value:N2}" : "Not specified")}");
                sb.AppendLine("--------------------------------------------------");
            }

            if (!string.IsNullOrWhiteSpace(ipo.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                sb.AppendLine(ipo.Description.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("Please log in to the Admin Dashboard to review and approve or reject this IPO submission request.");

            string body = sb.ToString();

            var recipients = new List<(string Email, string Name)>();
            foreach (var admin in adminUsers)
            {
                if (!string.IsNullOrWhiteSpace(admin.Email))
                {
                    recipients.Add((admin.Email.Trim(), $"{admin.First_name} {admin.Last_name}".Trim()));
                }
            }

            string? configuredAdminEmail = _configuration["Smtp:AdminEmail"]
                ?? _configuration["AdminSettings:NotificationEmail"];
            if (!string.IsNullOrWhiteSpace(configuredAdminEmail) &&
                !recipients.Any(r => string.Equals(r.Email, configuredAdminEmail.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                recipients.Add((configuredAdminEmail.Trim(), "Administrator"));
            }

            if (recipients.Count == 0)
            {
                string? fromEmail = _configuration["Smtp:FromEmail"];
                if (!string.IsNullOrWhiteSpace(fromEmail))
                {
                    recipients.Add((fromEmail.Trim(), "Administrator"));
                }
            }

            foreach (var recipient in recipients)
            {
                try
                {
                    await _emailNotificationService.SendAsync(
                        recipient.Email,
                        string.IsNullOrWhiteSpace(recipient.Name) ? "Admin" : recipient.Name,
                        subject,
                        body);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError($"Failed to send email to admin {recipient.Email}: {exception.Message}");
                }
            }

            if (adminUsers.Count > 0)
            {
                try
                {
                    DateTime nowUtc = DateTime.UtcNow;
                    _context.SystemNotifications.AddRange(adminUsers.Select(admin => new SystemNotification
                    {
                        User_id = admin.User_id,
                        Title = "New IPO Submission Request",
                        Message = $"{company.Company_name} submitted a new IPO request for '{ipo.IPO_name}'.",
                        Created_at = nowUtc,
                        Is_read = false
                    }));
                    await _context.SaveChangesAsync();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.TraceError($"Failed to save SystemNotification for admins: {exception.Message}");
                }
            }

            try
            {
                _context.SystemNotifications.Add(new SystemNotification
                {
                    User_id = company.User_id,
                    Title = "IPO Submission Sent",
                    Message = $"Your IPO submission request for '{ipo.IPO_name}' has been sent to Admin for review.",
                    Created_at = DateTime.UtcNow,
                    Is_read = false
                });
                await _context.SaveChangesAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError($"Failed to save SystemNotification for company: {exception.Message}");
            }
        }
    }
}