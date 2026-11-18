namespace IPOInvestmentManagement.Models
{
    public class PaymentReceiptViewModel
    {
        public string ReceiptNumber { get; set; } = string.Empty;
        public int PaymentRecordId { get; set; }
        public int? ApplicationId { get; set; }
        public string ApplicationNumber { get; set; } = string.Empty;

        public int UserId { get; set; }
        public string InvestorName { get; set; } = string.Empty;
        public string InvestorEmail { get; set; } = string.Empty;
        public string InvestorPhone { get; set; } = string.Empty;
        public string DematAccountNumber { get; set; } = string.Empty;
        public string UpiId { get; set; } = string.Empty;

        public int IpoId { get; set; }
        public string IpoName { get; set; } = string.Empty;
        public string CompanySymbol { get; set; } = string.Empty;
        public string CategoryName { get; set; } = "Retail Individual Investor (RII)";

        public int AppliedLots { get; set; }
        public int AppliedShares { get; set; }
        public decimal BidPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public string AmountInWords { get; set; } = string.Empty;

        public string RazorpayOrderId { get; set; } = string.Empty;
        public string RazorpayPaymentId { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = "Captured";
        public DateTime PaymentDate { get; set; }

        public bool IsSimulated { get; set; } = true;
        public string PaymentMode { get; set; } = "SIMULATED_MANDATE";

        public string RefundStatus { get; set; } = "Not Applicable";
        public decimal UnblockedAmount { get; set; }
        public string? RefundReferenceId { get; set; }
        public DateTime? RefundProcessedAt { get; set; }

        public string? ReconciliationStatus { get; set; }
        public DateTime? ReconciledAt { get; set; }
    }
}
