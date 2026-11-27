using IPOInvestmentManagement.Models;

namespace IPOInvestmentManagement.Services
{
    public interface IStockSipService
    {
        Task<List<StockSipPlan>> GetUserSipsAsync(int userId, CancellationToken cancellationToken = default);
        Task<StockSipDashboardViewModel> GetSipDashboardAsync(int userId, CancellationToken cancellationToken = default);
        Task<(bool Success, string Message, StockSipPlan? Plan)> CreateSipAsync(int userId, CreateStockSipRequest request, CancellationToken cancellationToken = default);
        Task<(bool Success, string Message)> PauseSipAsync(int userId, int sipId, CancellationToken cancellationToken = default);
        Task<(bool Success, string Message)> ResumeSipAsync(int userId, int sipId, CancellationToken cancellationToken = default);
        Task<(bool Success, string Message)> CancelSipAsync(int userId, int sipId, CancellationToken cancellationToken = default);
        Task<(bool Success, string Message, int? SharesBought, decimal? ExecutedPrice)> ExecuteInstallmentAsync(
            int sipId,
            int? requestingUserId = null,
            bool isManualTrigger = true,
            CancellationToken cancellationToken = default);
        Task ProcessDueSipInstallmentsAsync(CancellationToken cancellationToken = default);
    }
}
