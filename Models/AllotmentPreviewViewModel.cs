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

    public class AutomaticAllotmentPreviewViewModel
    {
        public IPO Ipo { get; set; } = new();
        public List<AutomaticAllotmentPreviewRow> Rows { get; set; } = new();
        public int AvailableLots { get; set; }
        public int AllocatedLots { get; set; }
        public int LotSize { get; set; }
        public decimal AllotmentPrice { get; set; }
        public long AvailableShares => (long)AvailableLots * LotSize;
        public long AllocatedShares => (long)AllocatedLots * LotSize;
    }

    public class AutomaticAllotmentPreviewRow
    {
        public IPOApplication Application { get; set; } = new();
        public string InvestorName { get; set; } = "Investor";
        public int AllottedLots { get; set; }
        public int AllottedShares { get; set; }
        public string Result => AllottedLots == 0
            ? "Not allotted"
            : AllottedLots == Application.Applied_lots
                ? "Full allotment"
                : "Partial allotment";
    }
}
