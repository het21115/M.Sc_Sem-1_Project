namespace IPOInvestmentManagement.Models
{
    public sealed class PortfolioViewModel
    {
        public decimal InvestedAmount { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal RealizedProfitLoss { get; set; }
        public decimal UnrealizedProfitLoss { get; set; }
        public List<PortfolioHoldingViewModel> Holdings { get; set; } = new();
        public List<PortfolioTransactionViewModel> Transactions { get; set; } = new();
        public List<PortfolioAllocationViewModel> Allocation { get; set; } = new();
        public List<PortfolioPerformancePoint> Performance { get; set; } = new();
    }

    public sealed class PortfolioHoldingViewModel
    {
        public int InvestmentId { get; set; }
        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal AverageBuyPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal InvestedAmount { get; set; }
        public decimal CurrentValue { get; set; }
        public decimal UnrealizedProfitLoss { get; set; }
        public decimal AllocationPercentage { get; set; }
    }

    public sealed class PortfolioTransactionViewModel
    {
        public string IpoName { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal TotalAmount { get; set; }
        public DateTime TransactionDate { get; set; }
    }

    public sealed class PortfolioAllocationViewModel
    {
        public string Label { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public decimal Percentage { get; set; }
    }

    public sealed class PortfolioPerformancePoint
    {
        public string Label { get; set; } = string.Empty;
        public decimal Value { get; set; }
    }

    public sealed class PortfolioTradeRequest
    {
        public int IpoId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }

    public sealed class PortfolioSellRequest
    {
        public int InvestmentId { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
    }
}
