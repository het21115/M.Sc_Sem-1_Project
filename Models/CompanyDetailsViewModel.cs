namespace IPOInvestmentManagement.Models
{
    public class CompanyDetailsViewModel
    {
        public Company Company { get; set; } = new();
        public List<IPO> Ipos { get; set; } = new();
    }
}
