using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace IPOInvestmentManagement.Services
{
    public sealed class WalletWithdrawalService : IWalletWithdrawalService
    {
        private const decimal MinimumWithdrawalAmount = 1.00m;
        private const decimal MaximumWithdrawalAmount = 10_000_000.00m;
        private static readonly ConcurrentDictionary<int, SemaphoreSlim> _userWithdrawalLocks = new();
        private static readonly Regex UpiPattern = new(@"^[a-zA-Z0-9._-]{2,64}@[a-zA-Z0-9]{2,64}$", RegexOptions.Compiled);
        private static readonly Regex AccountNumberPattern = new(@"^\d{9,18}$", RegexOptions.Compiled);
        private static readonly Regex IfscPattern = new(@"^[A-Z]{4}0[A-Z0-9]{6}$", RegexOptions.Compiled);

        private readonly ApplicationDbContext _context;
        private readonly IIpoClock _clock;
        private readonly ILogger<WalletWithdrawalService> _logger;

        public WalletWithdrawalService(
            ApplicationDbContext context,
            IIpoClock clock,
            ILogger<WalletWithdrawalService> logger)
        {
            _context = context;
            _clock = clock;
            _logger = logger;
        }

        public async Task<WalletWithdrawalResult> WithdrawAsync(
            int userId,
            WalletWithdrawalRequest request,
            string? ipAddress = null,
            CancellationToken cancellationToken = default)
        {
            if (request.Amount < MinimumWithdrawalAmount || request.Amount > MaximumWithdrawalAmount)
            {
                return new WalletWithdrawalResult
                {
                    Success = false,
                    Message = $"Withdrawal amount must be between ₹{MinimumWithdrawalAmount:N2} and ₹{MaximumWithdrawalAmount:N2}."
                };
            }

            if (decimal.Round(request.Amount, 2) != request.Amount)
            {
                return new WalletWithdrawalResult
                {
                    Success = false,
                    Message = "Withdrawal amount cannot have more than 2 decimal places."
                };
            }

            var (isValidMethod, payoutMethod, destinationAccount, validationError) = ValidateDestination(request);
            if (!isValidMethod)
            {
                return new WalletWithdrawalResult
                {
                    Success = false,
                    Message = validationError
                };
            }

            string? trimmedRemarks = string.IsNullOrWhiteSpace(request.Remarks)
                ? null
                : request.Remarks.Trim();
            if (trimmedRemarks is { Length: > 300 })
            {
                trimmedRemarks = trimmedRemarks[..300];
            }

            SemaphoreSlim userLock = _userWithdrawalLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
            await userLock.WaitAsync(cancellationToken);

            try
            {
                IDbContextTransaction? transaction = _context.Database.IsRelational()
                    ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    : null;

                try
                {
                    User? investor = await _context.Users
                        .FirstOrDefaultAsync(u => u.User_id == userId && u.Is_active, cancellationToken);

                    if (investor is null || !string.Equals(investor.Role, "Investor", StringComparison.OrdinalIgnoreCase))
                    {
                        if (transaction is not null)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                        }

                        return new WalletWithdrawalResult
                        {
                            Success = false,
                            Message = "Withdrawals are only available for active Investor accounts."
                        };
                    }

                    if (investor.SimulatedCashBalance < request.Amount)
                    {
                        if (transaction is not null)
                        {
                            await transaction.RollbackAsync(cancellationToken);
                        }

                        return new WalletWithdrawalResult
                        {
                            Success = false,
                            NewCashBalance = investor.SimulatedCashBalance,
                            Message = $"Insufficient wallet balance for withdrawal. Requested ₹{request.Amount:N2}, available ₹{investor.SimulatedCashBalance:N2}."
                        };
                    }

                    decimal balanceBefore = investor.SimulatedCashBalance;
                    decimal balanceAfter = balanceBefore - request.Amount;
                    if (balanceAfter < 0m)
                    {
                        throw new InvalidOperationException("Negative wallet balance prevented.");
                    }

                    investor.SimulatedCashBalance = balanceAfter;

                    DateTime nowIst = _clock.NowIst;
                    string referenceNumber = $"WD-{nowIst:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";

                    var withdrawal = new WalletWithdrawal
                    {
                        UserId = userId,
                        ReferenceNumber = referenceNumber,
                        Amount = request.Amount,
                        BalanceBefore = balanceBefore,
                        BalanceAfter = balanceAfter,
                        PayoutMethod = payoutMethod,
                        DestinationAccount = destinationAccount,
                        Status = "Completed",
                        Remarks = trimmedRemarks,
                        CreatedAt = nowIst
                    };
                    _context.WalletWithdrawals.Add(withdrawal);

                    string reasonText = string.IsNullOrWhiteSpace(trimmedRemarks)
                        ? $"Investor withdrawal {referenceNumber} via {payoutMethod} to {destinationAccount}"
                        : $"Investor withdrawal {referenceNumber} via {payoutMethod} to {destinationAccount} ({trimmedRemarks})";
                    if (reasonText.Length > 500)
                    {
                        reasonText = reasonText[..500];
                    }

                    _context.VirtualBalanceLedgers.Add(new VirtualBalanceLedger
                    {
                        UserId = userId,
                        AdminUserId = null,
                        TransactionType = "INVESTOR_WITHDRAWAL",
                        Amount = -request.Amount,
                        BalanceBefore = balanceBefore,
                        BalanceAfter = balanceAfter,
                        Reason = reasonText,
                        CreatedAt = nowIst
                    });

                    _context.AuditLogs.Add(new AuditLog
                    {
                        ActorUserId = userId,
                        Action = "InvestorWithdrawal",
                        EntityType = "User",
                        EntityId = userId.ToString(),
                        Details = $"Investor #{userId} withdrew ₹{request.Amount:N2} via {payoutMethod} ({destinationAccount}). Ref: {referenceNumber}. Balance: ₹{balanceBefore:N2} -> ₹{balanceAfter:N2}.",
                        IpAddress = ipAddress,
                        CreatedAt = DateTime.UtcNow
                    });

                    _context.SystemNotifications.Add(new SystemNotification
                    {
                        User_id = userId,
                        Title = "Withdrawal Processed",
                        Message = $"₹{request.Amount:N2} has been withdrawn from your wallet via {payoutMethod} ({destinationAccount}). Reference: {referenceNumber}. Remaining balance: ₹{balanceAfter:N2}.",
                        Created_at = nowIst,
                        Is_read = false
                    });

                    await _context.SaveChangesAsync(cancellationToken);

                    if (transaction is not null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    _logger.LogInformation(
                        "Processed investor withdrawal {ReferenceNumber} of ₹{Amount} for User {UserId}. New balance: ₹{BalanceAfter}.",
                        referenceNumber, request.Amount, userId, balanceAfter);

                    return new WalletWithdrawalResult
                    {
                        Success = true,
                        NewCashBalance = balanceAfter,
                        Withdrawal = withdrawal,
                        Message = $"Withdrawal of ₹{request.Amount:N2} processed successfully via {payoutMethod} ({destinationAccount}). Reference: {referenceNumber}."
                    };
                }
                catch (Exception ex)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    _logger.LogError(ex, "Withdrawal failed for User {UserId}.", userId);
                    return new WalletWithdrawalResult
                    {
                        Success = false,
                        Message = "Withdrawal transaction could not be completed. No funds were deducted."
                    };
                }
                finally
                {
                    if (transaction is not null)
                    {
                        await transaction.DisposeAsync();
                    }
                }
            }
            finally
            {
                userLock.Release();
            }
        }

        private static (bool IsValid, string PayoutMethod, string DestinationAccount, string ErrorMessage) ValidateDestination(WalletWithdrawalRequest request)
        {
            string normalizedMethod = request.PayoutMethod?.Trim() ?? string.Empty;

            if (string.Equals(normalizedMethod, "UPI", StringComparison.OrdinalIgnoreCase))
            {
                string upi = request.UpiId?.Trim() ?? string.Empty;
                if (!UpiPattern.IsMatch(upi))
                {
                    return (false, string.Empty, string.Empty, "Please enter a valid UPI ID (for example: name@bank).");
                }

                return (true, "UPI", upi, string.Empty);
            }

            if (string.Equals(normalizedMethod, "Bank Transfer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedMethod, "Bank", StringComparison.OrdinalIgnoreCase))
            {
                string rawAccount = Regex.Replace(request.BankAccountNumber ?? string.Empty, @"\s+", string.Empty);
                string ifsc = (request.IfscCode ?? string.Empty).Trim().ToUpperInvariant();

                if (!AccountNumberPattern.IsMatch(rawAccount))
                {
                    return (false, string.Empty, string.Empty, "Bank account number must contain 9 to 18 digits.");
                }

                if (!IfscPattern.IsMatch(ifsc))
                {
                    return (false, string.Empty, string.Empty, "Please enter a valid 11-character IFSC code (for example: HDFC0001234).");
                }

                string maskedAccount = $"A/C ****{rawAccount[^4..]} ({ifsc})";
                return (true, "Bank Transfer", maskedAccount, string.Empty);
            }

            return (false, string.Empty, string.Empty, "Invalid payout method selected. Choose UPI or Bank Transfer.");
        }
    }
}
