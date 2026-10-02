namespace IPOInvestmentManagement.Models
{
    public class AdminApplicationsViewModel
    {
        public int? SelectedIpoId { get; set; }
        public string? SelectedStatus { get; set; }
        public int TotalApplications { get; set; }
        public long TotalSharesApplied { get; set; }
        public decimal TotalAmount { get; set; }
        public List<AdminApplicationListItem> Applications { get; set; } = new();
        public List<IPO> AvailableIpos { get; set; } = new();
    }

    public class AdminApplicationListItem
    {
        public IPOApplication Application { get; set; } = new();
        public string InvestorName { get; set; } = "Investor";
        public string InvestorEmail { get; set; } = string.Empty;
        public string IpoName { get; set; } = "IPO";
    }
}
