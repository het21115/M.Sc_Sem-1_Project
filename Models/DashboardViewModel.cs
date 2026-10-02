namespace IPOInvestmentManagement.Models
{
    public class DashboardViewModel
    {
        public string UserName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public int OpenIpoCount { get; set; }
        public int ApplicationCount { get; set; }
        public int WatchlistCount { get; set; }
        public int InvestmentCount { get; set; }
        public int UserCount { get; set; }
        public int ActiveIpoCount { get; set; }
        public int ListedIpoCount { get; set; }
        public int CompanyCount { get; set; }
        public int IpoCount { get; set; }
        public int TotalCompanyIpoCount { get; set; }
        public int ActiveCompanyIpoCount { get; set; }
        public int CompanyApplicationCount { get; set; }
        public decimal TotalCompanyIssueSize { get; set; }
        public int ListedCompanyIpoCount { get; set; }
        public int CompanyNotificationCount { get; set; }
        public int PendingCompanyCount { get; set; }
        public int PendingApplicationCount { get; set; }
        public int PendingIpoCount { get; set; }
        public List<IPO> RecentIpos { get; set; } = new();
        public List<CompanyIpoDetailsViewModel> PublishedIpoDetails { get; set; } = new();
        public List<IPO> CompanyIpos { get; set; } = new();
        public List<CompanyIpoDetailsViewModel> CompanyIpoDetails { get; set; } = new();
        public List<Company> Companies { get; set; } = new();
        public List<Company> PendingCompanies { get; set; } = new();
        public List<IPO> PendingIpos { get; set; } = new();
        public List<User> RecentUsers { get; set; } = new();
        public List<User> RecentInvestors { get; set; } = new();
        public List<User> RecentCompanies { get; set; } = new();
        public List<IPOApplication> RecentApplications { get; set; } = new();
        public List<IPOApplication> PendingApplications { get; set; } = new();
        public List<Investment> Investments { get; set; } = new();
        public List<SystemNotification> Notifications { get; set; } = new();
        public Dictionary<int, string> CompanyNames { get; set; } = new();
        public Dictionary<int, string> PendingIpoCompanyNames { get; set; } = new();
        public Dictionary<int, string> PendingApplicationIpoNames { get; set; } = new();
        public Dictionary<int, string> PendingApplicationUserNames { get; set; } = new();
        public IpoSubmissionViewModel IpoSubmission { get; set; } = new();
    }
}