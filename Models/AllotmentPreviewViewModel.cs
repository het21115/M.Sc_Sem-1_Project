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
        public string CategoryName { get; set; } = "Retail Individual Investor (RII)";
        public string CategoryCode { get; set; } = "RII";
    }

    public class AutomaticAllotmentPreviewViewModel
    {
        public IPO Ipo { get; set; } = new();
        public List<AutomaticAllotmentPreviewRow> Rows { get; set; } = new();
        public List<CategoryAllotmentSummaryViewModel> CategorySummaries { get; set; } = new();
        public int AvailableLots { get; set; }
        public int AllocatedLots { get; set; }
        public int LotSize { get; set; }
        public decimal AllotmentPrice { get; set; }
        public long AvailableShares => (long)AvailableLots * LotSize;
        public long AllocatedShares => (long)AllocatedLots * LotSize;
        public bool IsClosed { get; set; }
        public DateTime? CloseDate { get; set; }
        public bool IsAllotmentDateReached { get; set; }
        public DateTime? AllotmentDate { get; set; }
        public int TotalAppliedLots => Rows.Sum(row => row.Application.Applied_lots);
        public long TotalAppliedShares => Rows.Sum(row => (long)row.Application.Applied_shares);
        public decimal OverallSubscriptionMultiple => AvailableLots > 0 ? Math.Round((decimal)TotalAppliedLots / AvailableLots, 2) : 0m;
    }

    public class CategoryAllotmentSummaryViewModel
    {
        public string CategoryCode { get; set; } = string.Empty; // "RII", "NII", "QIB"
        public string CategoryName { get; set; } = string.Empty; // "Retail Individual Investors (RII)"
        public string MaxApplicationRule { get; set; } = string.Empty; // "<= ₹2,00,000"
        public decimal QuotaPercentage { get; set; } // 35%, 15%, 50%
        public int InitialQuotaLots { get; set; }
        public int EffectiveQuotaLots { get; set; }
        public long EffectiveQuotaShares { get; set; }
        public int ApplicationCount { get; set; }
        public int AppliedLots { get; set; }
        public long AppliedShares { get; set; }
        public int AllocatedLots { get; set; }
        public long AllocatedShares { get; set; }
        public decimal SubscriptionMultiple => EffectiveQuotaLots > 0 ? Math.Round((decimal)AppliedLots / EffectiveQuotaLots, 2) : 0m;
        public string AllocationMethod { get; set; } = string.Empty;
        public string SpilloverNote { get; set; } = string.Empty;
    }

    public class AutomaticAllotmentPreviewRow
    {
        public IPOApplication Application { get; set; } = new();
        public string InvestorName { get; set; } = "Investor";
        public string Category { get; set; } = "Retail Individual Investor (RII)";
        public string CategoryCode { get; set; } = "RII";
        public int AllottedLots { get; set; }
        public int AllottedShares { get; set; }
        public decimal AllotmentPrice { get; set; }
        public decimal ApplicationAmount => Application.Application_amount ?? (Application.Applied_shares * AllotmentPrice);
        public decimal AllocatedAmount => AllottedShares * AllotmentPrice;
        public decimal UnblockedAmount => Math.Max(0m, ApplicationAmount - AllocatedAmount);
        public string Result => AllottedLots == 0
            ? "Not allotted"
            : AllottedLots == Application.Applied_lots
                ? "Full allotment"
                : "Partial allotment";
    }

    public class AllotmentCorrectionRequestViewModel
    {
        public int ApplicationId { get; set; }
        public int CorrectedLots { get; set; }
        public decimal? CorrectedPrice { get; set; }

        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "An auditable justification is mandatory.")]
        [System.ComponentModel.DataAnnotations.MinLength(10, ErrorMessage = "A detailed auditable justification of at least 10 characters is mandatory for authorized corrections.")]
        public string CorrectionReason { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Authorization confirmation is required.")]
        public string AuthorizationPasscode { get; set; } = string.Empty;
    }
}
