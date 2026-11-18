namespace IPOInvestmentManagement.Models
{
    public class PaymentReconciliationReport
    {
        public DateTime RunAt { get; set; } = DateTime.UtcNow;
        public int TotalRecordsChecked { get; set; }
        public int ReconciledCaptured { get; set; }
        public int ReconciledFailed { get; set; }
        public int ReconciledUnblocks { get; set; }
        public int ApplicationsAutoCreated { get; set; }
        public int WebhooksRetried { get; set; }
        public int DiscrepanciesFound { get; set; }
        public List<string> Details { get; set; } = new();
    }
}
