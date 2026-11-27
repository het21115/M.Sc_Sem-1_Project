namespace IPOInvestmentManagement.Models
{
    public class DashboardViewModel
    {
        public string UserName { get; set; } = string.Empty;
        public string InvestorDematAccountNumber { get; set; } = string.Empty;
        public string InvestorUpiId { get; set; } = string.Empty;
        public decimal SimulatedCashBalance { get; set; }
        public decimal TotalWithdrawnAmount { get; set; }
        public List<WalletWithdrawal> WithdrawalHistory { get; set; } = new();
        public MarketDashboardViewModel Market { get; set; } = new();
        public StockSipDashboardViewModel SipDashboard { get; set; } = new();
        public string Role { get; set; } = string.Empty;
        public int OpenIpoCount { get; set; }
        public int ApplicationCount { get; set; }
        public int WatchlistCount { get; set; }
        public int InvestmentCount { get; set; }
        public decimal InvestmentTotal { get; set; }
        public decimal ProfitLoss { get; set; }
        public decimal TodayProfitLoss { get; set; }
        public decimal TodayProfitLossPercentage { get; set; }
        public int UserCount { get; set; }
        public int ActiveIpoCount { get; set; }
        public int UpcomingIpoCount { get; set; }
        public int ClosedIpoCount { get; set; }
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
        public List<AdminSubscriptionStatisticsViewModel> SubscriptionStatistics { get; set; } = new();
        public List<IPO> RecentIpos { get; set; } = new();
        public List<IPO> MarketIpos { get; set; } = new();
        public List<string> MarketIpoTypes { get; set; } = new();
        public List<string> MarketCompanies { get; set; } = new();
        public Dictionary<int, string> MarketStatuses { get; set; } = new();
        public string? MarketStatus { get; set; }
        public string? MarketIpoType { get; set; }
        public string? MarketIpoName { get; set; }
        public string? MarketCompany { get; set; }
        public List<CompanyIpoDetailsViewModel> PublishedIpoDetails { get; set; } = new();
        public List<CompanyIpoDetailsViewModel> WatchlistDetails { get; set; } = new();
        public List<CompanyIpoDetailsViewModel> CalendarIpoDetails { get; set; } = new();
        public int CalendarMonth { get; set; }
        public int CalendarYear { get; set; }
        public List<CompanyIpoDetailsViewModel> ComparisonIpoDetails { get; set; } = new();
        public int[] ComparisonIpoIds { get; set; } = Array.Empty<int>();
        public Dictionary<int, decimal> ComparisonSubscriptionMultiples { get; set; } = new();
        public List<IPO> CompanyIpos { get; set; } = new();
        public List<CompanyIpoDetailsViewModel> CompanyIpoDetails { get; set; } = new();
        public List<Company> Companies { get; set; } = new();
        public List<Company> PendingCompanies { get; set; } = new();
        public List<IPO> PendingIpos { get; set; } = new();
        public List<IPO> ApprovedIpos { get; set; } = new();
        public List<User> RecentUsers { get; set; } = new();
        public List<User> RecentInvestors { get; set; } = new();
        public List<User> RecentCompanies { get; set; } = new();
        public List<IPOApplication> RecentApplications { get; set; } = new();
        public Dictionary<int, IPOAllotment> ApplicationAllotments { get; set; } = new();
        public Dictionary<string, PaymentRecord> ApplicationPayments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<IPOApplication> PendingApplications { get; set; } = new();
        public List<Investment> Investments { get; set; } = new();
        public List<PortfolioPerformancePoint> PortfolioPerformance { get; set; } = new();
        public List<SystemNotification> Notifications { get; set; } = new();
        public List<PaymentRecord> PaymentHistory { get; set; } = new();
        public Dictionary<int, string> CompanyNames { get; set; } = new();
        public Dictionary<int, string> ApplicationIpoNames { get; set; } = new();
        public Dictionary<int, string> PaymentIpoNames { get; set; } = new();
        public Dictionary<int, string> InvestmentIpoNames { get; set; } = new();
        public Dictionary<int, string> PendingIpoCompanyNames { get; set; } = new();
        public Dictionary<int, string> ApprovedIpoCompanyNames { get; set; } = new();
        public Dictionary<int, string> PendingApplicationIpoNames { get; set; } = new();
        public Dictionary<int, string> PendingApplicationUserNames { get; set; } = new();
        public IpoSubmissionViewModel IpoSubmission { get; set; } = new();

        public List<AdminSearchIpoItem> AllAdminIpos { get; set; } = new();
        public string? AdminIpoSearchQuery { get; set; }
        public string? AdminIpoSearchStatus { get; set; }
        public string? AdminIpoSearchType { get; set; }

        // Admin Payment Management
        public List<PaymentRecord> AdminPayments { get; set; } = new();
        public Dictionary<int, string> PaymentUserNames { get; set; } = new();
        public Dictionary<int, string> PaymentUserEmails { get; set; } = new();
        public Dictionary<int, string> PaymentUserDemats { get; set; } = new();
        public Dictionary<int, string> PaymentUserUpis { get; set; } = new();
        public Dictionary<string, int> PaymentApplicationIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int SuccessfulPaymentCount { get; set; }
        public int FailedPaymentCount { get; set; }
        public int PendingPaymentCount { get; set; }
        public int RefundedPaymentCount { get; set; }

        public decimal SuccessfulPaymentAmount { get; set; }
        public decimal FailedPaymentAmount { get; set; }
        public decimal PendingPaymentAmount { get; set; }
        public decimal RefundedPaymentAmount { get; set; }
        public decimal TotalPaymentVolume { get; set; }

        public string TimeGreeting => GetTimeBasedGreeting();

        public static string GetTimeBasedGreeting()
        {
            DateTime now = IPOInvestmentManagement.Services.IpoClock.Shared.NowIst;

            int hour = now.Hour;
            if (hour >= 5 && hour < 12)
            {
                return "Good morning";
            }
            else if (hour >= 12 && hour < 17)
            {
                return "Good afternoon";
            }
            else
            {
                return "Good evening";
            }
        }
    }
}