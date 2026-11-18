namespace IPOInvestmentManagement.Models
{
    public class AdminPaymentManagementViewModel
    {
        public List<PaymentRecord> Payments { get; set; } = new();
        public Dictionary<int, string> UserNames { get; set; } = new();
        public Dictionary<int, string> UserEmails { get; set; } = new();
        public Dictionary<int, string> UserDemats { get; set; } = new();
        public Dictionary<int, string> UserUpis { get; set; } = new();
        public Dictionary<int, string> IpoNames { get; set; } = new();
        public Dictionary<string, int> ApplicationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<IPO> AllIpos { get; set; } = new();

        public string? SelectedStatus { get; set; }
        public string? SearchQuery { get; set; }
        public int? SelectedIpoId { get; set; }

        public int TotalTransactions { get; set; }
        public int SuccessfulCount { get; set; }
        public int FailedCount { get; set; }
        public int PendingCount { get; set; }
        public int RefundedCount { get; set; }

        public decimal TotalVolume { get; set; }
        public decimal SuccessfulVolume { get; set; }
        public decimal FailedVolume { get; set; }
        public decimal PendingVolume { get; set; }
        public decimal RefundedVolume { get; set; }
    }
}
