namespace IPOInvestmentManagement.Models
{
    public class AdminSearchViewModel
    {
        public string? UserQuery { get; set; }
        public string? UserRole { get; set; }
        public string? UserStatus { get; set; }
        public string? IpoQuery { get; set; }
        public string? IpoCompanyQuery { get; set; }
        public string? IpoStatus { get; set; }
        public DateTime? IpoDate { get; set; }
        public string? IpoCategory { get; set; }
        public string? CompanyQuery { get; set; }
        public string? CompanyCin { get; set; }
        public string? CompanyIndustry { get; set; }
        public string? CompanyStatus { get; set; }
        public List<User> Users { get; set; } = new();
        public List<AdminSearchIpoItem> Ipos { get; set; } = new();
        public List<Company> Companies { get; set; } = new();
    }

    public class AdminSearchIpoItem
    {
        public IPO Ipo { get; set; } = new();
        public string CompanyName { get; set; } = "Company";
    }
}
