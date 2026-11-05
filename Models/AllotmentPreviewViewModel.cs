namespace IPOInvestmentManagement.Models
{
    public class AllotmentPreviewViewModel
    {
        public IPOApplication Application { get; set; } = new();
        public IPO Ipo { get; set; } = new();
        public int AllottedLots { get; set; }
        public int AllottedShares { get; set; }
        public decimal AllotmentPrice { get; set; }
        public decimal AllocatedAmount => AllottedShares * AllotmentPrice;
        public decimal UnblockedAmount => Math.Max(0m, (Application.Application_amount ?? 0m) - AllocatedAmount);
    }
}
