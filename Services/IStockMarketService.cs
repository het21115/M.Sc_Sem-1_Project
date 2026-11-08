using IPOInvestmentManagement.Models;

namespace IPOInvestmentManagement.Services
{
    public interface IStockMarketService
    {
        Task<MarketDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);
        Task<MarketDataViewModel?> GetMarketDataAsync(int ipoId, CancellationToken cancellationToken = default);
    }
}
