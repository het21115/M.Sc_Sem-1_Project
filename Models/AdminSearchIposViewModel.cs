namespace IPOInvestmentManagement.Models
{
    public class AdminSearchIposViewModel
    {
        public string UserName { get; set; } = string.Empty;
        public string? AdminIpoSearchQuery { get; set; }
        public string? AdminIpoSearchStatus { get; set; }
        public string? AdminIpoSearchType { get; set; }
        public List<AdminSearchIpoItem> AllAdminIpos { get; set; } = new();
    }
}
