using System;
using System.Collections.Generic;

namespace IPOInvestmentManagement.Models
{
    public class AllotmentResultsViewModel
    {
        public IPO Ipo { get; set; } = new();
        public IPOAllotmentHistory? History { get; set; }
        public DateTime AllotmentDate { get; set; }
        public decimal CutOffPrice { get; set; }
        public int LotSize { get; set; }

        public int TotalApplications { get; set; }
        public int AllottedCount { get; set; }
        public int PartialAllotmentCount { get; set; }
        public int UnallottedCount { get; set; }

        public long TotalAppliedShares { get; set; }
        public long TotalAllottedShares { get; set; }
        public decimal TotalDebitedAmount { get; set; }
        public decimal TotalUnblockedAmount { get; set; }
        public decimal OversubscriptionMultiple { get; set; }

        public List<CategoryAllotmentSummaryViewModel> CategorySummaries { get; set; } = new();
        public List<AllotmentInvestorResultItem> AllApplicants { get; set; } = new();
        public List<AllotmentInvestorResultItem> AllottedApplicants { get; set; } = new();
        public List<AllotmentInvestorResultItem> PartiallyAllottedApplicants { get; set; } = new();
        public List<AllotmentInvestorResultItem> NonAllottedApplicants { get; set; } = new();
    }

    public class AllotmentInvestorResultItem
    {
        public int ApplicationId { get; set; }
        public string ApplicationNumber { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string InvestorName { get; set; } = string.Empty;
        public string InvestorEmail { get; set; } = string.Empty;
        public string DematAccountNumber { get; set; } = string.Empty;
        public string UpiId { get; set; } = string.Empty;
        public string CategoryCode { get; set; } = "RII";
        public string CategoryName { get; set; } = "Retail Individual Investor (RII)";

        public int AppliedLots { get; set; }
        public int AppliedShares { get; set; }
        public decimal BidPrice { get; set; }

        public int AllottedLots { get; set; }
        public int AllottedShares { get; set; }
        public decimal AllotmentPrice { get; set; }

        public decimal BlockedAmount { get; set; }
        public decimal DebitedAmount { get; set; }
        public decimal UnblockedAmount { get; set; }

        public string Status { get; set; } = "Not Allotted";
        public string? AdviceNumber { get; set; }
        public string RefundStatus { get; set; } = "Not Applicable";
        public string? RefundReferenceId { get; set; }
    }

    public class AllotmentHistoryListViewModel
    {
        public List<AllotmentHistoryItemViewModel> Items { get; set; } = new();
    }

    public class AllotmentHistoryItemViewModel
    {
        public int HistoryId { get; set; }
        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public DateTime FinalizedAt { get; set; }
        public string FinalizedByAdminName { get; set; } = "Platform Admin";
        public int TotalApplications { get; set; }
        public int AllottedApplications { get; set; }
        public int PartialAllotmentCount { get; set; }
        public int UnallottedCount { get; set; }
        public long TotalAllottedShares { get; set; }
        public decimal TotalDebitedAmount { get; set; }
        public decimal TotalUnblockedAmount { get; set; }
        public decimal OversubscriptionMultiple { get; set; }
        public decimal CutOffPrice { get; set; }
    }
}
