namespace IPOInvestmentManagement.Models
{
    public class CreateStockSipRequest
    {
        public int IpoId { get; set; }
        public string Exchange { get; set; } = "NSE";
        public decimal InstallmentAmount { get; set; }
        public string Frequency { get; set; } = "Monthly"; // Daily, Weekly, Monthly
        public int InstallmentDay { get; set; } = 5;
        public int? TotalMonths { get; set; } // Optional duration, e.g. 6, 12, 24, 36, or null for ongoing
    }

    public class StockSipDashboardViewModel
    {
        public List<StockSipPlan> ActivePlans { get; set; } = new();
        public List<StockSipPlan> PastPlans { get; set; } = new();
        public List<StockSipPlan> Plans => ActivePlans.Concat(PastPlans).ToList();
        public decimal TotalMonthlyCommitment { get; set; }
        public decimal MonthlyCommitment => TotalMonthlyCommitment;
        public decimal TotalSipInvested { get; set; }
        public decimal TotalInvested => TotalSipInvested;
        public int TotalActiveSips { get; set; }
        public int ActiveSipCount => TotalActiveSips;
        public int TotalInstallmentsExecuted { get; set; }
        public int TotalSharesAccumulated { get; set; }
    }
}
