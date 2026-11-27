using IPOInvestmentManagement.Models;

namespace IPOInvestmentManagement.Services
{
    public interface IWalletWithdrawalService
    {
        Task<WalletWithdrawalResult> WithdrawAsync(
            int userId,
            WalletWithdrawalRequest request,
            string? ipAddress = null,
            CancellationToken cancellationToken = default);
    }
}
