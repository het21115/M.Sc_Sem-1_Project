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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DenyCompany(int companyId)
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

            company.Approval_status = "Denied";
            await _context.SaveChangesAsync();

            TempData["AdminMessage"] = $"{company.Company_name} was denied. The request is no longer in the approval queue.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveIpo(int ipoId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null || !string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That IPO request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }

            ipo.Status = "Approved";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} was approved and can now be published publicly.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DenyIpo(int ipoId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null || !string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That IPO request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }

            ipo.Status = "Rejected";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} was denied and will not be published publicly.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitIpo([Bind(Prefix = "IpoSubmission")] IpoSubmissionViewModel submission)
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue || !string.Equals(HttpContext.Session.GetString("UserRole"), "Company", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            if (submission.OpeningDate.HasValue && submission.ClosingDate.HasValue && submission.ClosingDate < submission.OpeningDate)
            {
                ModelState.AddModelError(nameof(submission.ClosingDate), "Closing Date must be after Opening Date.");
            }

            if (submission.PriceMin.HasValue && submission.PriceMax.HasValue && submission.PriceMax < submission.PriceMin)
            {
                ModelState.AddModelError(nameof(submission.PriceMax), "Maximum Price must be greater than or equal to Minimum Price.");
            }

            try
            {
                submission.TotalShares = ConvertShareValue(submission.TotalShares, "Units");
                submission.LotSize = ConvertIntShareValue(submission.LotSize, "Units");
                submission.IssueSize = ConvertMoneyValue(submission.IssueSize, submission.IssueSizeUnit);
                submission.Revenue = ConvertMoneyValue(submission.Revenue, submission.RevenueUnit);
                submission.Profit = ConvertMoneyValue(submission.Profit, submission.ProfitUnit);
                submission.NetWorth = ConvertMoneyValue(submission.NetWorth, submission.NetWorthUnit);
            }
            catch (ArgumentException exception)
            {
                ModelState.AddModelError("", exception.Message);
            }

            Company? company = await _context.Companies
                .FirstOrDefaultAsync(item => item.User_id == userId.Value && item.Approval_status == "Approved");
            if (company is null)
            {
                ModelState.AddModelError("", "An approved company profile is required before submitting an IPO.");
            }

            if (!ModelState.IsValid)
            {
                var model = new DashboardViewModel
                {
                    UserName = HttpContext.Session.GetString("UserName") ?? "User",
                    Role = "Company",
                    IpoSubmission = submission
                };
                await LoadCompanyDashboardAsync(model, userId.Value);
                model.IpoSubmission = submission;
                return View("Company", model);
            }

            var ipo = new IPO
            {
                Company_id = company!.Company_id,
                IPO_name = submission.IpoName,
                Total_shares = submission.TotalShares,
                Open_date = submission.OpeningDate!.Value,
                Close_date = submission.ClosingDate!.Value,
                Price_min = submission.PriceMin,
                Price_max = submission.PriceMax,
                Lot_size = submission.LotSize,
                Issue_size = submission.IssueSize,
                Allotment_date = submission.AllotmentDate,
                Funds_unblock_date = submission.FundsUnblockDate,
                Listing_date = submission.ListingDate,
                Description = submission.Description,
                Apply_type = "Retail",
                Status = "Pending",
                Created_at = DateTime.Now
            };

            try
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                _context.IPOs.Add(ipo);
                await _context.SaveChangesAsync();

                _context.IPOFinancials.Add(new IPOFinancial
                {
                    IPO_id = ipo.IPO_id,
                    Financial_year = submission.FinancialYear!.Value,
                    Revenue = submission.Revenue,
                    Profit = submission.Profit,
                    Net_worth = submission.NetWorth
                });
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (DbUpdateException)
            {
                ModelState.AddModelError("", "The IPO could not be saved. Please verify all required information and try again.");
                var model = new DashboardViewModel
                {
                    UserName = HttpContext.Session.GetString("UserName") ?? "User",
                    Role = "Company",
                    IpoSubmission = submission
                };
                await LoadCompanyDashboardAsync(model, userId.Value);
                model.IpoSubmission = submission;
                return View("Company", model);
            }

            TempData["SubmittedIpoName"] = ipo.IPO_name;
            return RedirectToAction(nameof(IpoSubmissionPending));
        }

        [HttpGet]
        public IActionResult IpoSubmissionPending()
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Company", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction("Login", "Account");
            }

            return View("~/Views/Home/IpoSubmissionPending.cshtml");
        }

        private static long? ConvertShareValue(long? value, string unit)
        {
            if (!value.HasValue)
            {
                return null;
            }

            decimal converted = value.Value * GetUnitMultiplier(unit);
            return checked((long)converted);
        }

        private static int? ConvertIntShareValue(int? value, string unit)
        {
            if (!value.HasValue)
            {
                return null;
            }

            decimal converted = value.Value * GetUnitMultiplier(unit);
            return checked((int)converted);
        }

        private static decimal? ConvertMoneyValue(decimal? value, string unit)
        {
            return value.HasValue ? value.Value * GetUnitMultiplier(unit) : null;
        }

        private static decimal GetUnitMultiplier(string unit)
        {
            return unit?.Trim().ToLowerInvariant() switch
            {
                "units" => 1m,
                "thousands" => 1_000m,
                "lakhs" => 100_000m,
                "crores" => 10_000_000m,
                _ => throw new ArgumentException("Please select a valid unit: Units, Thousands, Lakhs, or Crores.")
            };
        }

        private async Task LoadInvestorDashboardAsync(DashboardViewModel model, int userId)
        {
            DateTime today = DateTime.UtcNow.Date;
            model.OpenIpoCount = await _context.IPOs.CountAsync(ipo =>
                (ipo.Status == "Approved" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed") &&
                ipo.Open_date.Date <= today && ipo.Close_date.Date >= today);
            model.ApplicationCount = await _context.IPOApplications.CountAsync(application => application.User_id == userId);
            model.WatchlistCount = await _context.IPOWatchlists.CountAsync(item => item.User_id == userId);
            model.InvestmentCount = await _context.Investments.CountAsync(investment => investment.User_id == userId);
            List<IPO> publishedIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Approved" || ipo.Status == "Open" || ipo.Status == "Listed" || ipo.Status == "Closed")
                .OrderByDescending(ipo => ipo.Created_at)
                .ToListAsync();
            model.RecentIpos = publishedIpos.Take(5).ToList();
            model.PublishedIpoDetails = await LoadIpoDetailsAsync(publishedIpos);
            model.Investments = await _context.Investments
                .AsNoTracking()
                .Where(investment => investment.User_id == userId)
                .OrderByDescending(investment => investment.Buy_date)
                .Take(5)
                .ToListAsync();
            await LoadCompanyNamesAsync(model, publishedIpos.Select(ipo => ipo.Company_id));
        }

        private async Task<List<CompanyIpoDetailsViewModel>> LoadIpoDetailsAsync(List<IPO> ipos)
        {
            int[] ipoIds = ipos.Select(ipo => ipo.IPO_id).ToArray();
            var applicationCounts = await _context.IPOApplications
                .Where(application => ipoIds.Contains(application.IPO_id))
                .GroupBy(application => application.IPO_id)
                .Select(group => new { IpoId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.IpoId, item => item.Count);
            var financials = await _context.IPOFinancials
                .AsNoTracking()
                .Where(financial => ipoIds.Contains(financial.IPO_id))
                .OrderByDescending(financial => financial.Financial_year)
                .ToListAsync();
            var analyses = await _context.IPOAnalyses
                .AsNoTracking()
                .Where(analysis => ipoIds.Contains(analysis.IPO_id))
                .OrderByDescending(analysis => analysis.Updated_at)
                .ToListAsync();
            var listings = await _context.IPOListings
                .AsNoTracking()
                .Where(listing => ipoIds.Contains(listing.IPO_id))
                .ToListAsync();

            return ipos.Select(ipo => new CompanyIpoDetailsViewModel
            {
                Ipo = ipo,
                ApplicationCount = applicationCounts.GetValueOrDefault(ipo.IPO_id),
                Financial = financials.FirstOrDefault(financial => financial.IPO_id == ipo.IPO_id),
                Analysis = analyses.FirstOrDefault(analysis => analysis.IPO_id == ipo.IPO_id),
                Listing = listings.FirstOrDefault(listing => listing.IPO_id == ipo.IPO_id)
            }).ToList();
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
            model.PendingIpos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => ipo.Status == "Pending")
                .OrderBy(ipo => ipo.Created_at)
                .Take(10)
                .ToListAsync();
            int[] pendingIpoCompanyIds = model.PendingIpos.Select(ipo => ipo.Company_id).Distinct().ToArray();
            model.PendingIpoCompanyNames = await _context.Companies
                .AsNoTracking()
                .Where(company => pendingIpoCompanyIds.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
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
            int[] ipoIds = model.CompanyIpos.Select(ipo => ipo.IPO_id).ToArray();
            var applicationCounts = await _context.IPOApplications
                .Where(application => ipoIds.Contains(application.IPO_id))
                .GroupBy(application => application.IPO_id)
                .Select(group => new { IpoId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.IpoId, item => item.Count);
            var financials = await _context.IPOFinancials
                .AsNoTracking()
                .Where(financial => ipoIds.Contains(financial.IPO_id))
                .OrderByDescending(financial => financial.Financial_year)
                .ToListAsync();
            var analyses = await _context.IPOAnalyses
                .AsNoTracking()
                .Where(analysis => ipoIds.Contains(analysis.IPO_id))
                .OrderByDescending(analysis => analysis.Updated_at)
                .ToListAsync();
            var listings = await _context.IPOListings
                .AsNoTracking()
                .Where(listing => ipoIds.Contains(listing.IPO_id))
                .ToListAsync();
            model.CompanyIpoDetails = model.CompanyIpos.Select(ipo => new CompanyIpoDetailsViewModel
            {
                Ipo = ipo,
                ApplicationCount = applicationCounts.GetValueOrDefault(ipo.IPO_id),
                Financial = financials.FirstOrDefault(financial => financial.IPO_id == ipo.IPO_id),
                Analysis = analyses.FirstOrDefault(analysis => analysis.IPO_id == ipo.IPO_id),
                Listing = listings.FirstOrDefault(listing => listing.IPO_id == ipo.IPO_id)
            }).ToList();
            model.CompanyCount = model.Companies.Count;
            model.IpoCount = model.CompanyIpos.Count;
            DateTime today = DateTime.UtcNow.Date;
            model.TotalCompanyIpoCount = model.CompanyIpos.Count;
            model.ActiveCompanyIpoCount = model.CompanyIpos.Count(ipo => ipo.Open_date.Date <= today && ipo.Close_date.Date >= today);
            model.TotalCompanyIssueSize = model.CompanyIpos.Sum(ipo => ipo.Issue_size ?? 0m);
            model.ListedCompanyIpoCount = model.CompanyIpos.Count(ipo =>
                (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= today) ||
                string.Equals(ipo.Status, "Listed", StringComparison.OrdinalIgnoreCase));
            model.CompanyApplicationCount = await _context.IPOApplications
                .CountAsync(application => ipoIds.Contains(application.IPO_id));
            model.PendingCompanyCount = model.Companies.Count(company => company.Approval_status == "Pending");
            model.PendingIpoCount = model.CompanyIpos.Count(ipo => ipo.Status == "Pending");
            model.CompanyNotificationCount = model.PendingCompanyCount + model.PendingIpoCount;
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