using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Controllers
{
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DashboardController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            string role = HttpContext.Session.GetString("UserRole") ?? "Investor";
            var model = new DashboardViewModel
            {
                UserName = HttpContext.Session.GetString("UserName") ?? "User",
                Role = role
            };

            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                await LoadAdminDashboardAsync(model);
                return View("Admin", model);
            }

            if (string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase))
            {
                await LoadCompanyDashboardAsync(model, userId.Value);
                return View("Company", model);
            }

            await LoadInvestorDashboardAsync(model, userId.Value);
            return View("Investor", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveCompany(int companyId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            Company? company = await _context.Companies.FindAsync(companyId);
            if (company is null || !string.Equals(company.Approval_status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That Company request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }

            User? user = await _context.Users.FindAsync(company.User_id);
            if (user is null)
            {
                TempData["AdminMessage"] = "The user linked to this Company request could not be found.";
                return RedirectToAction(nameof(Index));
            }

            company.Approval_status = "Approved";
            user.Role = "Company";
            await _context.SaveChangesAsync();

            TempData["AdminMessage"] = $"{company.Company_name} was approved. The user can now access the Company Dashboard.";
            return RedirectToAction(nameof(Index));
        }

        private async Task LoadInvestorDashboardAsync(DashboardViewModel model, int userId)
        {
            DateTime today = DateTime.UtcNow.Date;
            model.OpenIpoCount = await _context.IPOs.CountAsync(ipo =>
                ipo.Open_date.Date <= today && ipo.Close_date.Date >= today);
            model.ApplicationCount = await _context.IPOApplications.CountAsync(application => application.User_id == userId);
            model.WatchlistCount = await _context.IPOWatchlists.CountAsync(item => item.User_id == userId);
            model.InvestmentCount = await _context.Investments.CountAsync(investment => investment.User_id == userId);
            model.RecentIpos = await _context.IPOs
                .AsNoTracking()
                .OrderByDescending(ipo => ipo.Created_at)
                .Take(5)
                .ToListAsync();
            model.Investments = await _context.Investments
                .AsNoTracking()
                .Where(investment => investment.User_id == userId)
                .OrderByDescending(investment => investment.Buy_date)
                .Take(5)
                .ToListAsync();
            await LoadCompanyNamesAsync(model, model.RecentIpos.Select(ipo => ipo.Company_id));
        }

        private async Task LoadAdminDashboardAsync(DashboardViewModel model)
        {
            model.UserCount = await _context.Users.CountAsync(user =>
                user.Is_active &&
                (user.Role == null || user.Role != "Admin") &&
                !_context.Companies.Any(company =>
                    company.User_id == user.User_id &&
                    company.Approval_status == "Pending"));
            model.CompanyCount = await _context.Companies.CountAsync();
            model.IpoCount = await _context.IPOs.CountAsync();
            model.PendingCompanyCount = await _context.Companies.CountAsync(company => company.Approval_status == "Pending");
            model.PendingApplicationCount = await _context.IPOApplications.CountAsync(application => application.Status == "Pending");
            model.PendingIpoCount = await _context.IPOs.CountAsync(ipo => ipo.Status == "Pending");
            model.PendingCompanies = await _context.Companies
                .AsNoTracking()
                .Where(company => company.Approval_status == "Pending")
                .OrderBy(company => company.Created_at)
                .Take(10)
                .ToListAsync();
            model.RecentUsers = await _context.Users
                .AsNoTracking()
                .Where(user =>
                    user.Is_active &&
                    (user.Role == null || user.Role != "Admin") &&
                    !_context.Companies.Any(company =>
                        company.User_id == user.User_id &&
                        company.Approval_status == "Pending"))
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToListAsync();
            model.RecentApplications = await _context.IPOApplications
                .AsNoTracking()
                .OrderByDescending(application => application.Application_date)
                .Take(5)
                .ToListAsync();
        }

        private async Task LoadCompanyDashboardAsync(DashboardViewModel model, int userId)
        {
            model.Companies = await _context.Companies
                .AsNoTracking()
                .Where(company => company.User_id == userId)
                .OrderByDescending(company => company.Created_at)
                .ToListAsync();

            int[] companyIds = model.Companies.Select(company => company.Company_id).ToArray();
            model.CompanyIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => companyIds.Contains(ipo.Company_id))
                .OrderByDescending(ipo => ipo.Created_at)
                .ToListAsync();
            model.CompanyCount = model.Companies.Count;
            model.IpoCount = model.CompanyIpos.Count;
            model.PendingCompanyCount = model.Companies.Count(company => company.Approval_status == "Pending");
            model.PendingIpoCount = model.CompanyIpos.Count(ipo => ipo.Status == "Pending");
        }

        private async Task LoadCompanyNamesAsync(DashboardViewModel model, IEnumerable<int> companyIds)
        {
            int[] ids = companyIds.Distinct().ToArray();
            model.CompanyNames = await _context.Companies
                .AsNoTracking()
                .Where(company => ids.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
        }
    }
}