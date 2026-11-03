namespace IPOInvestmentManagement.Models
{
    public class AllotmentRequestViewModel
    {
        public int ApplicationId { get; set; }
        public int AllottedLots { get; set; }
        public int AllottedShares { get; set; }
        public decimal AllotmentPrice { get; set; }
    }

    public class BulkAllotmentRequestViewModel
    {
        public int IpoId { get; set; }
        public int[] ApplicationIds { get; set; } = [];
    }
}
