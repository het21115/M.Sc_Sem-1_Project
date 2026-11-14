using System.Threading;
using System.Threading.Tasks;

namespace IPOInvestmentManagement.Services
{
    public interface IInvestorReportService
    {
        Task<byte[]> GeneratePortfolioStatementPdfAsync(int userId, CancellationToken cancellationToken = default);
        Task<byte[]> GenerateTransactionReportPdfAsync(int userId, CancellationToken cancellationToken = default);
        Task<byte[]> GenerateIpoApplicationReportPdfAsync(int userId, CancellationToken cancellationToken = default);
        Task<byte[]> GenerateAllotmentReportPdfAsync(int userId, CancellationToken cancellationToken = default);
    }
}
