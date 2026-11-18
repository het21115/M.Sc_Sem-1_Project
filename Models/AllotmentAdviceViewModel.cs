using System;

namespace IPOInvestmentManagement.Models
{
    public class AllotmentAdviceViewModel
    {
        public string AdviceNumber { get; set; } = string.Empty;
        public int ApplicationId { get; set; }
        public string ApplicationNumber { get; set; } = string.Empty;
        public DateTime ApplicationDate { get; set; }
        public DateTime AllotmentDate { get; set; }

        public int UserId { get; set; }
        public string InvestorName { get; set; } = string.Empty;
        public string InvestorEmail { get; set; } = string.Empty;
        public string InvestorPhone { get; set; } = string.Empty;
        public string DematAccountNumber { get; set; } = string.Empty;
        public string UpiId { get; set; } = string.Empty;

        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string StockSymbol { get; set; } = string.Empty;
        public int LotSize { get; set; }
        public string CategoryName { get; set; } = "Retail Individual Investor (RII)";
        public string CategoryCode { get; set; } = "RII";

        public int AppliedLots { get; set; }
        public int AppliedShares { get; set; }
        public decimal BidPrice { get; set; }

        public int AllottedLots { get; set; }
        public int AllottedShares { get; set; }
        public decimal AllotmentPrice { get; set; }

        public decimal BlockedAmount { get; set; }
        public decimal DebitedAmount { get; set; }
        public decimal UnblockedAmount { get; set; }
        public string AmountInWords { get; set; } = string.Empty;

        public string Status { get; set; } = "Allotted"; // Allotted, Partially Allotted, Not Allotted
        public string RefundStatus { get; set; } = "Not Applicable";
        public string? RefundReferenceId { get; set; }
        public DateTime? RefundProcessedAt { get; set; }

        public DateTime? ExpectedListingDate { get; set; }
        public string RegistrarName { get; set; } = "Link Intime / KFin Technologies (Registrar to Issue)";
        public string VerificationCode { get; set; } = string.Empty;
    }
}
