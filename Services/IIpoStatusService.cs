using IPOInvestmentManagement.Models;

namespace IPOInvestmentManagement.Services
{
    public class IpoStatusChangeDetail
    {
        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string PreviousStatus { get; set; } = string.Empty;
        public string NewStatus { get; set; } = string.Empty;
        public DateTime OpeningDateTime { get; set; }
        public DateTime ClosingDateTime { get; set; }
    }

    public class IpoStatusSyncResult
    {
        public int TotalEvaluated { get; set; }
        public int TotalUpdated { get; set; }
        public DateTime TimestampIst { get; set; }
        public List<IpoStatusChangeDetail> Changes { get; set; } = new();
    }

    public interface IIpoStatusService
    {
        string CalculateStatus(IPO ipo, DateTime? istNow = null);
        (bool CanApply, string? ErrorReason) CanApply(IPO ipo, DateTime? istNow = null);
        DateTime GetOpeningDateTime(IPO ipo);
        DateTime GetClosingDateTime(IPO ipo);
        TimeSpan GetConfiguredOpeningTime();
        TimeSpan GetConfiguredClosingTime();
        Task<IpoStatusSyncResult> SynchronizeIpoStatusesAsync(CancellationToken cancellationToken = default);
        Task<bool> SynchronizeSingleIpoStatusAsync(int ipoId, CancellationToken cancellationToken = default);
    }
}
