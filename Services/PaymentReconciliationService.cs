using System.Data;
using System.Text.Json;
using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Services
{
    public class PaymentReconciliationService : IPaymentReconciliationService
    {
        private readonly ApplicationDbContext _context;
        private readonly IRazorpayPaymentService _razorpayPaymentService;
        private readonly IIpoStatusService _ipoStatusService;
        private readonly IEmailNotificationService _emailNotificationService;
        private readonly ILogger<PaymentReconciliationService> _logger;

        public PaymentReconciliationService(
            ApplicationDbContext context,
            IRazorpayPaymentService razorpayPaymentService,
            IIpoStatusService ipoStatusService,
            IEmailNotificationService emailNotificationService,
            ILogger<PaymentReconciliationService> logger)
        {
            _context = context;
            _razorpayPaymentService = razorpayPaymentService;
            _ipoStatusService = ipoStatusService;
            _emailNotificationService = emailNotificationService;
            _logger = logger;
        }

        public async Task<PaymentReconciliationReport> ReconcileAllPaymentsAsync(CancellationToken cancellationToken = default)
        {
            PaymentReconciliationReport report = new()
            {
                RunAt = DateTime.UtcNow
            };

            DateTime nowUtc = DateTime.UtcNow;
            DateTime fiveMinutesAgo = nowUtc.AddMinutes(-3);
            DateTime thirtyMinutesAgo = nowUtc.AddMinutes(-30);

            // 1. Process Pending / Created payment records
            List<PaymentRecord> pendingRecords = await _context.PaymentRecords
                .Where(r => (r.Status == "Created" || r.Status == "Pending") && r.CreatedAt <= fiveMinutesAgo)
                .OrderBy(r => r.CreatedAt)
                .Take(50)
                .ToListAsync(cancellationToken);

            report.TotalRecordsChecked += pendingRecords.Count;

            foreach (PaymentRecord record in pendingRecords)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    bool reconciled = await ReconcilePaymentRecordInternalAsync(record, thirtyMinutesAgo, report, cancellationToken);
                    if (reconciled)
                    {
                        await _context.SaveChangesAsync(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reconciling payment record #{RecordId}", record.PaymentRecordId);
                    report.DiscrepanciesFound++;
                    report.Details.Add($"Record #{record.PaymentRecordId} error: {ex.Message}");
                }
            }

            // 2. Process orphaned Captured payments (captured payment without submitted application)
            List<PaymentRecord> capturedRecords = await _context.PaymentRecords
                .Where(r => r.Status == "Captured")
                .OrderByDescending(r => r.UpdatedAt)
                .Take(50)
                .ToListAsync(cancellationToken);

            foreach (PaymentRecord record in capturedRecords)
            {
                if (cancellationToken.IsCancellationRequested) break;

                bool appExists = await _context.IPOApplications
                    .AnyAsync(a => a.Razorpay_order_id == record.RazorpayOrderId ||
                                  (record.RazorpayPaymentId != null && a.Razorpay_payment_id == record.RazorpayPaymentId),
                              cancellationToken);

                if (!appExists)
                {
                    _logger.LogWarning("Orphaned captured payment detected for order {OrderId}. Healing application...", record.RazorpayOrderId);
                    await AutoHealMissingApplicationAsync(record, report, cancellationToken);
                }
            }

            // 3. Process Pending Unblocks / Refunds
            int unblocksProcessed = await ReconcilePendingUnblocksAsync(cancellationToken);
            report.ReconciledUnblocks = unblocksProcessed;

            // 4. Retry failed webhooks
            int webhooksRetried = await ProcessPendingWebhookRetriesAsync(cancellationToken);
            report.WebhooksRetried = webhooksRetried;

            await _context.SaveChangesAsync(cancellationToken);
            return report;
        }

        public async Task<bool> ReconcilePaymentRecordAsync(int paymentRecordId, CancellationToken cancellationToken = default)
        {
            PaymentRecord? record = await _context.PaymentRecords
                .FirstOrDefaultAsync(r => r.PaymentRecordId == paymentRecordId, cancellationToken);

            if (record is null)
            {
                return false;
            }

            PaymentReconciliationReport dummyReport = new();
            bool result = await ReconcilePaymentRecordInternalAsync(record, DateTime.UtcNow.AddMinutes(-30), dummyReport, cancellationToken);
            if (result)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            return result;
        }

        private async Task<bool> ReconcilePaymentRecordInternalAsync(
            PaymentRecord record,
            DateTime expirationThreshold,
            PaymentReconciliationReport report,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(record.RazorpayOrderId))
            {
                return false;
            }

            // Query Razorpay order payments
            IReadOnlyList<RazorpayPaymentDetails> orderPayments =
                await _razorpayPaymentService.GetOrderPaymentsAsync(record.RazorpayOrderId);

            RazorpayPaymentDetails? capturedPayment = orderPayments.FirstOrDefault(p =>
                string.Equals(p.Status, "captured", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Status, "authorized", StringComparison.OrdinalIgnoreCase));

            if (capturedPayment != null)
            {
                record.RazorpayPaymentId = capturedPayment.PaymentId;
                record.Amount = capturedPayment.Amount;
                record.Status = "Captured";
                record.ReconciledAt = DateTime.UtcNow;
                record.ReconciliationStatus = "Reconciled-Captured";
                record.UpdatedAt = DateTime.UtcNow;

                if (string.IsNullOrWhiteSpace(record.ReceiptNumber))
                {
                    record.ReceiptNumber = $"RCP-{DateTime.UtcNow:yyyyMMdd}-{record.PaymentRecordId:D5}";
                }

                report.ReconciledCaptured++;
                report.Details.Add($"Payment #{record.PaymentRecordId} (Order: {record.RazorpayOrderId}) reconciled as CAPTURED (Ref: {capturedPayment.PaymentId}).");

                // Ensure application exists
                bool appExists = await _context.IPOApplications
                    .AnyAsync(a => a.Razorpay_order_id == record.RazorpayOrderId, cancellationToken);

                if (!appExists)
                {
                    await AutoHealMissingApplicationAsync(record, report, cancellationToken);
                }

                return true;
            }

            // Check if all payments failed or expired
            bool hasOnlyFailedPayments = orderPayments.Count > 0 && orderPayments.All(p =>
                string.Equals(p.Status, "failed", StringComparison.OrdinalIgnoreCase));

            if (hasOnlyFailedPayments || record.CreatedAt < expirationThreshold)
            {
                record.Status = "Failed";
                record.FailureReason = hasOnlyFailedPayments ? "Gateway marked payment attempts as failed." : "Payment window expired without completion.";
                record.ReconciledAt = DateTime.UtcNow;
                record.ReconciliationStatus = "Reconciled-Failed";
                record.UpdatedAt = DateTime.UtcNow;

                report.ReconciledFailed++;
                report.Details.Add($"Payment #{record.PaymentRecordId} (Order: {record.RazorpayOrderId}) reconciled as FAILED ({record.FailureReason}).");
                return true;
            }

            return false;
        }

        private async Task AutoHealMissingApplicationAsync(
            PaymentRecord record,
            PaymentReconciliationReport report,
            CancellationToken cancellationToken)
        {
            IPO? ipo = await _context.IPOs.AsNoTracking()
                .FirstOrDefaultAsync(i => i.IPO_id == record.IpoId, cancellationToken);
            User? investor = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.User_id == record.UserId, cancellationToken);

            if (ipo == null || investor == null)
            {
                return;
            }

            // Prevent duplicate creation
            bool alreadyApplied = await _context.IPOApplications.AnyAsync(a =>
                a.User_id == record.UserId && a.IPO_id == record.IpoId &&
                a.Status != "Rejected" && a.Status != "Cancelled", cancellationToken);

            if (alreadyApplied)
            {
                return;
            }

            string receiptNumber = record.ReceiptNumber ?? $"RCP-{DateTime.UtcNow:yyyyMMdd}-{record.PaymentRecordId:D5}";
            record.ReceiptNumber = receiptNumber;

            IPOApplication application = new()
            {
                User_id = record.UserId,
                IPO_id = record.IpoId,
                Application_number = record.ApplicationNumber,
                Applied_lots = record.AppliedLots,
                Applied_shares = record.AppliedShares,
                Bid_price = record.BidPrice,
                Application_amount = record.Amount,
                Demat_account_number = investor.DematAccountNumber ?? string.Empty,
                Upi_id = investor.UpiId ?? string.Empty,
                Payment_reference = record.RazorpayPaymentId ?? record.RazorpayOrderId,
                Razorpay_order_id = record.RazorpayOrderId,
                Razorpay_payment_id = record.RazorpayPaymentId,
                Payment_amount = record.Amount,
                Payment_status = "Verified",
                Payment_mode = record.PaymentMode,
                Is_simulated_payment = record.IsSimulated,
                Receipt_number = receiptNumber,
                Application_date = DateTime.UtcNow,
                Status = "Submitted"
            };

            _context.IPOApplications.Add(application);

            bool ledgerExists = await _context.VirtualBalanceLedgers.AnyAsync(l =>
                l.UserId == record.UserId &&
                l.TransactionType == "IPO_APPLICATION" &&
                l.Reason.Contains(record.ApplicationNumber), cancellationToken);
            if (!ledgerExists)
            {
                User? trackedInvestor = await _context.Users
                    .FirstOrDefaultAsync(u => u.User_id == record.UserId && u.Is_active, cancellationToken);
                if (trackedInvestor != null && trackedInvestor.SimulatedCashBalance >= record.Amount)
                {
                    decimal balanceBefore = trackedInvestor.SimulatedCashBalance;
                    trackedInvestor.SimulatedCashBalance = balanceBefore - record.Amount;
                    _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                    {
                        UserId = record.UserId,
                        AdminUserId = null,
                        TransactionType = "IPO_APPLICATION",
                        Amount = -record.Amount,
                        BalanceBefore = balanceBefore,
                        BalanceAfter = trackedInvestor.SimulatedCashBalance,
                        Reason = $"IPO application #{record.ApplicationNumber} for {ipo.IPO_name} ({record.AppliedLots} lot(s), {record.AppliedShares} shares @ ₹{record.BidPrice:N2})",
                        CreatedAt = IpoClock.Shared.NowIst
                    });
                }
            }

            _context.SystemNotifications.Add(new SystemNotification
            {
                User_id = record.UserId,
                Title = $"Payment & Bid Reconciled - {ipo.IPO_name}",
                Message = $"Automated payment reconciliation matched and confirmed your bid of ₹{record.Amount:N2} for application #{record.ApplicationNumber}. Receipt: {receiptNumber}.",
                Created_at = DateTime.UtcNow,
                Is_read = false
            });

            report.ApplicationsAutoCreated++;
            report.Details.Add($"Auto-created missing IPOApplication #{record.ApplicationNumber} for user {investor.Email}.");

            try
            {
                string subject = $"Payment Reconciled & Application Confirmed: {ipo.IPO_name}";
                string body = $"Dear {investor.First_name},\n\nYour payment for {ipo.IPO_name} (Application #{record.ApplicationNumber}) was automatically reconciled and confirmed.\n\nReceipt Number: {receiptNumber}\nAmount: ₹{record.Amount:N2}\nPayment Ref: {record.RazorpayPaymentId}\n\nYour application is now officially submitted.\n\nThank you,\nIPO Investment Management";
                await _emailNotificationService.SendAsync(investor.Email, $"{investor.First_name} {investor.Last_name}".Trim(), subject, body);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send reconciliation email to {Email}", investor.Email);
            }
        }

        public async Task<int> ReconcilePendingUnblocksAsync(CancellationToken cancellationToken = default)
        {
            List<PaymentRecord> pendingUnblocks = await _context.PaymentRecords
                .Where(r => r.RefundStatus == "Pending Unblock" && r.UnblockedAmount > 0m)
                .OrderBy(r => r.UpdatedAt)
                .Take(25)
                .ToListAsync(cancellationToken);

            int count = 0;
            foreach (PaymentRecord record in pendingUnblocks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    if (string.IsNullOrWhiteSpace(record.RefundReferenceId))
                    {
                        record.RefundReferenceId = $"UNBLK-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..25].ToUpper();
                    }

                    record.RefundStatus = "Completed";
                    record.RefundProcessedAt = DateTime.UtcNow;
                    record.UpdatedAt = DateTime.UtcNow;

                    await CreditRefundToWalletIfEligibleAsync(record, cancellationToken);

                    IPO? ipo = await _context.IPOs.AsNoTracking()
                        .FirstOrDefaultAsync(i => i.IPO_id == record.IpoId, cancellationToken);

                    _context.SystemNotifications.Add(new SystemNotification
                    {
                        User_id = record.UserId,
                        Title = $"Mandate Amount Unblocked / Refunded",
                        Message = $"₹{record.UnblockedAmount:N2} has been unblocked/refunded to your investor wallet for {ipo?.IPO_name ?? "IPO"} application #{record.ApplicationNumber}. Ref: {record.RefundReferenceId}.",
                        Created_at = DateTime.UtcNow,
                        Is_read = false
                    });

                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reconcile unblock for payment #{PaymentId}", record.PaymentRecordId);
                }
            }

            return count;
        }

        public async Task<int> ProcessPendingWebhookRetriesAsync(CancellationToken cancellationToken = default)
        {
            List<PaymentWebhookLog> failedLogs = await _context.PaymentWebhookLogs
                .Where(l => l.Status == "Failed" && l.RetryCount < 5)
                .OrderBy(l => l.ReceivedAt)
                .Take(20)
                .ToListAsync(cancellationToken);

            int retriedSuccess = 0;
            foreach (PaymentWebhookLog log in failedLogs)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    log.RetryCount++;
                    bool processed = await ProcessWebhookPayloadInternalAsync(log.EventType, log.Payload, cancellationToken);
                    if (processed)
                    {
                        log.Status = "Retried-Processed";
                        log.ProcessedAt = DateTime.UtcNow;
                        log.ErrorMessage = null;
                        retriedSuccess++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Webhook retry #{RetryCount} failed for Log #{LogId}", log.RetryCount, log.WebhookLogId);
                    log.ErrorMessage = ex.Message;
                }
            }

            return retriedSuccess;
        }

        public async Task<PaymentWebhookLog> LogAndProcessWebhookAsync(
            string eventId,
            string eventType,
            string? orderId,
            string? paymentId,
            string payload)
        {
            // Idempotency check: check if already processed
            PaymentWebhookLog? existing = await _context.PaymentWebhookLogs
                .FirstOrDefaultAsync(l => l.EventId == eventId && l.Status == "Processed");

            if (existing != null)
            {
                _logger.LogInformation("Webhook event {EventId} was already processed. Skipping duplicate.", eventId);
                return existing;
            }

            PaymentWebhookLog log = new()
            {
                EventId = eventId,
                EventType = eventType,
                RazorpayOrderId = orderId,
                RazorpayPaymentId = paymentId,
                Payload = payload,
                Status = "Received",
                ReceivedAt = DateTime.UtcNow
            };

            _context.PaymentWebhookLogs.Add(log);
            await _context.SaveChangesAsync();

            try
            {
                bool handled = await ProcessWebhookPayloadInternalAsync(eventType, payload);
                log.Status = handled ? "Processed" : "Ignored";
                log.ProcessedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing webhook {EventId} ({EventType})", eventId, eventType);
                log.Status = "Failed";
                log.ErrorMessage = ex.Message;
            }

            await _context.SaveChangesAsync();
            return log;
        }

        private async Task<bool> ProcessWebhookPayloadInternalAsync(
            string eventType,
            string payload,
            CancellationToken cancellationToken = default)
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("payload", out JsonElement payloadElem))
            {
                return false;
            }

            if (payloadElem.TryGetProperty("payment", out JsonElement paymentElem) &&
                paymentElem.TryGetProperty("entity", out JsonElement paymentEntity))
            {
                string orderId = paymentEntity.TryGetProperty("order_id", out var oId) ? oId.GetString() ?? string.Empty : string.Empty;
                string paymentId = paymentEntity.TryGetProperty("id", out var pId) ? pId.GetString() ?? string.Empty : string.Empty;
                decimal amount = paymentEntity.TryGetProperty("amount", out var amt) ? amt.GetInt64() / 100m : 0m;

                PaymentRecord? record = await _context.PaymentRecords
                    .FirstOrDefaultAsync(r => r.RazorpayOrderId == orderId, cancellationToken);

                if (record is null)
                {
                    return false;
                }

                if (eventType == "payment.captured")
                {
                    if (record.Status != "Captured")
                    {
                        record.RazorpayPaymentId = paymentId;
                        record.Amount = amount;
                        record.Status = "Captured";
                        record.ReconciledAt = DateTime.UtcNow;
                        record.ReconciliationStatus = "Webhook-Captured";
                        record.UpdatedAt = DateTime.UtcNow;

                        if (string.IsNullOrWhiteSpace(record.ReceiptNumber))
                        {
                            record.ReceiptNumber = $"RCP-{DateTime.UtcNow:yyyyMMdd}-{record.PaymentRecordId:D5}";
                        }
                    }
                    return true;
                }
                else if (eventType == "payment.failed")
                {
                    if (record.Status != "Captured")
                    {
                        record.Status = "Failed";
                        record.FailureReason = paymentEntity.TryGetProperty("error_description", out var ed)
                            ? ed.GetString() ?? "Payment failed on gateway"
                            : "Payment failed on gateway";
                        record.UpdatedAt = DateTime.UtcNow;
                    }
                    return true;
                }
            }

            if (payloadElem.TryGetProperty("refund", out JsonElement refundElem) &&
                refundElem.TryGetProperty("entity", out JsonElement refundEntity))
            {
                string paymentId = refundEntity.TryGetProperty("payment_id", out var pId) ? pId.GetString() ?? string.Empty : string.Empty;
                decimal refundAmount = refundEntity.TryGetProperty("amount", out var rAmt) ? rAmt.GetInt64() / 100m : 0m;
                string refundId = refundEntity.TryGetProperty("id", out var rId) ? rId.GetString() ?? string.Empty : string.Empty;

                PaymentRecord? record = await _context.PaymentRecords
                    .FirstOrDefaultAsync(r => r.RazorpayPaymentId == paymentId, cancellationToken);

                if (record is not null)
                {
                    record.RefundStatus = "Completed";
                    record.RefundProcessedAt = DateTime.UtcNow;
                    record.RefundReferenceId = refundId;
                    record.UnblockedAmount = refundAmount > 0m ? refundAmount : record.UnblockedAmount;
                    record.UpdatedAt = DateTime.UtcNow;
                    await CreditRefundToWalletIfEligibleAsync(record, cancellationToken);
                    return true;
                }
            }

            return false;
        }

        private async Task CreditRefundToWalletIfEligibleAsync(PaymentRecord record, CancellationToken cancellationToken)
        {
            if (record.UnblockedAmount <= 0m)
            {
                return;
            }

            bool alreadyRefundedToWallet = await _context.VirtualBalanceLedgers.AnyAsync(l =>
                l.UserId == record.UserId &&
                l.TransactionType == "IPO_REFUND" &&
                l.Reason.Contains(record.ApplicationNumber), cancellationToken);

            if (alreadyRefundedToWallet)
            {
                return;
            }

            User? investor = await _context.Users.FirstOrDefaultAsync(u => u.User_id == record.UserId, cancellationToken);
            if (investor is null)
            {
                return;
            }

            decimal before = investor.SimulatedCashBalance;
            investor.SimulatedCashBalance = before + record.UnblockedAmount;
            _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
            {
                UserId = record.UserId,
                AdminUserId = null,
                TransactionType = "IPO_REFUND",
                Amount = record.UnblockedAmount,
                BalanceBefore = before,
                BalanceAfter = investor.SimulatedCashBalance,
                Reason = $"IPO mandate unblock/refund for #{record.ApplicationNumber} (Ref: {record.RefundReferenceId})",
                CreatedAt = IpoClock.Shared.NowIst
            });
        }
    }
}
