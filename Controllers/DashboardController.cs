using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using IPOInvestmentManagement.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;

namespace IPOInvestmentManagement.Controllers
{
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailNotificationService _emailNotificationService;

        public DashboardController(ApplicationDbContext context, IEmailNotificationService emailNotificationService)
        {
            _context = context;
            _emailNotificationService = emailNotificationService;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            int? userId = HttpContext.Session.GetInt32("User_id");
            if (!userId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            bool isActive = await _context.Users
                .AsNoTracking()
                .AnyAsync(user => user.User_id == userId.Value && user.Is_active);
            if (!isActive)
            {
                HttpContext.Session.Clear();
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

        [HttpGet]
        public async Task<IActionResult> UserDetails(int userId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            User? user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.User_id == userId);

            if (user is null)
            {
                return NotFound();
            }

            return View(user);
        }

        [HttpGet]
        public async Task<IActionResult> EditProfile()
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            int? userId = HttpContext.Session.GetInt32("User_id");
            User? user = userId.HasValue
                ? await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.User_id == userId.Value)
                : null;
            if (user is null)
            {
                return RedirectToAction("Login", "Account");
            }

            return View(new AdminProfileViewModel
            {
                UserId = user.User_id,
                FirstName = user.First_name,
                LastName = user.Last_name,
                Email = user.Email,
                Phone = user.Phone,
                Role = user.Role ?? "Admin"
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditProfile(AdminProfileViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            int? sessionUserId = HttpContext.Session.GetInt32("User_id");
            if (!sessionUserId.HasValue || sessionUserId.Value != model.UserId)
            {
                return Forbid();
            }

            bool emailUsedByAnotherUser = await _context.Users.AnyAsync(user =>
                user.User_id != model.UserId && user.Email == model.Email);
            if (emailUsedByAnotherUser)
            {
                ModelState.AddModelError(nameof(model.Email), "This email address is already in use.");
            }

            if (!ModelState.IsValid)
            {
                model.Role = "Admin";
                return View(model);
            }

            User? userToUpdate = await _context.Users.FindAsync(model.UserId);
            if (userToUpdate is null || !userToUpdate.Is_active ||
                !string.Equals(userToUpdate.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            userToUpdate.First_name = model.FirstName.Trim();
            userToUpdate.Last_name = model.LastName.Trim();
            userToUpdate.Email = model.Email.Trim();
            userToUpdate.Phone = model.Phone.Trim();
            await _context.SaveChangesAsync();

            HttpContext.Session.SetString("UserName", $"{userToUpdate.First_name} {userToUpdate.Last_name}".Trim());
            HttpContext.Session.SetString("UserEmail", userToUpdate.Email);
            TempData["AdminMessage"] = "Your Admin profile was updated successfully.";
            return RedirectToAction(nameof(EditProfile));
        }

        [HttpGet]
        public async Task<IActionResult> CompanyDetails(int companyId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            Company? company = await _context.Companies
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Company_id == companyId);

            if (company is null)
            {
                return NotFound();
            }

            var model = new CompanyDetailsViewModel
            {
                Company = company,
                Ipos = await _context.IPOs
                    .AsNoTracking()
                    .Where(ipo => ipo.Company_id == companyId &&
                        (ipo.Status == "Approved" ||
                         ipo.Status == "Open" ||
                         ipo.Status == "Listed" ||
                         ipo.Status == "Closed"))
                    .OrderByDescending(ipo => ipo.Created_at)
                    .ToListAsync()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> IpoDetails(int ipoId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPO? ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(item => item.IPO_id == ipoId);
            if (ipo is null)
            {
                return NotFound();
            }

            string companyName = await _context.Companies
                .Where(company => company.Company_id == ipo.Company_id)
                .Select(company => company.Company_name)
                .FirstOrDefaultAsync() ?? "Company";

            return View(new AdminIpoDetailsViewModel { Ipo = ipo, CompanyName = companyName });
        }

        [HttpGet]
        public async Task<IActionResult> Applications(int? ipoId, string? status)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IQueryable<IPOApplication> query = _context.IPOApplications.AsNoTracking();
            if (ipoId.HasValue)
            {
                query = query.Where(application => application.IPO_id == ipoId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(application => application.Status == status);
            }

            List<IPOApplication> applications = await query
                .OrderByDescending(application => application.Application_date)
                .ToListAsync();
            int[] userIds = applications.Select(application => application.User_id).Distinct().ToArray();
            int[] applicationIpoIds = applications.Select(application => application.IPO_id).Distinct().ToArray();
            Dictionary<int, User> users = await _context.Users
                .AsNoTracking()
                .Where(user => userIds.Contains(user.User_id))
                .ToDictionaryAsync(user => user.User_id);
            Dictionary<int, IPO> ipos = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => applicationIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id);

            var model = new AdminApplicationsViewModel
            {
                SelectedIpoId = ipoId,
                SelectedStatus = status,
                TotalApplications = applications.Count,
                TotalSharesApplied = applications.Sum(application => (long)application.Applied_shares),
                TotalAmount = applications.Sum(application => application.Application_amount ?? 0m),
                Applications = applications.Select(application => new AdminApplicationListItem
                {
                    Application = application,
                    InvestorName = users.GetValueOrDefault(application.User_id) is User user
                        ? $"{user.First_name} {user.Last_name}".Trim()
                        : "Investor",
                    InvestorEmail = users.GetValueOrDefault(application.User_id)?.Email ?? string.Empty,
                    IpoName = ipos.GetValueOrDefault(application.IPO_id)?.IPO_name ?? "IPO"
                }).ToList(),
                AvailableIpos = await _context.IPOs
                    .AsNoTracking()
                    .OrderByDescending(ipo => ipo.Created_at)
                    .ToListAsync()
            };

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Search(AdminSearchViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IQueryable<User> users = _context.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(model.UserQuery))
            {
                string query = model.UserQuery.Trim();
                users = users.Where(user =>
                    (user.First_name + " " + user.Last_name).Contains(query) ||
                    user.Email.Contains(query) ||
                    user.Phone.Contains(query));
            }
            if (!string.IsNullOrWhiteSpace(model.UserRole))
            {
                users = users.Where(user => user.Role == model.UserRole);
            }
            if (!string.IsNullOrWhiteSpace(model.UserStatus))
            {
                bool isActive = model.UserStatus == "Active";
                users = users.Where(user => user.Is_active == isActive);
            }
            model.Users = await users.OrderBy(user => user.First_name).ThenBy(user => user.Last_name).Take(100).ToListAsync();

            IQueryable<IPO> ipos = _context.IPOs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(model.IpoQuery))
            {
                string query = model.IpoQuery.Trim();
                ipos = ipos.Where(ipo => ipo.IPO_name.Contains(query));
            }
            if (!string.IsNullOrWhiteSpace(model.IpoStatus))
            {
                ipos = ipos.Where(ipo => ipo.Status == model.IpoStatus);
            }
            if (!string.IsNullOrWhiteSpace(model.IpoCompanyQuery))
            {
                string companyQuery = model.IpoCompanyQuery.Trim();
                ipos = ipos.Where(ipo => _context.Companies.Any(company =>
                    company.Company_id == ipo.Company_id &&
                    company.Company_name.Contains(companyQuery)));
            }
            if (model.IpoDate.HasValue)
            {
                DateTime date = model.IpoDate.Value.Date;
                ipos = ipos.Where(ipo => ipo.Open_date.Date == date || ipo.Close_date.Date == date || ipo.Created_at.Date == date);
            }
            if (!string.IsNullOrWhiteSpace(model.IpoCategory))
            {
                ipos = ipos.Where(ipo => ipo.IPO_type == model.IpoCategory);
            }
            List<IPO> ipoResults = await ipos.OrderByDescending(ipo => ipo.Created_at).Take(100).ToListAsync();
            int[] companyIds = ipoResults.Select(ipo => ipo.Company_id).Distinct().ToArray();
            Dictionary<int, string> companyNames = await _context.Companies.AsNoTracking()
                .Where(company => companyIds.Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, company => company.Company_name);
            model.Ipos = ipoResults.Select(ipo => new AdminSearchIpoItem
            {
                Ipo = ipo,
                CompanyName = companyNames.GetValueOrDefault(ipo.Company_id, "Company")
            }).ToList();

            IQueryable<Company> companies = _context.Companies.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(model.CompanyQuery))
            {
                companies = companies.Where(company => company.Company_name.Contains(model.CompanyQuery.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(model.CompanyCin))
            {
                companies = companies.Where(company => company.CIN != null && company.CIN.Contains(model.CompanyCin.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(model.CompanyIndustry))
            {
                companies = companies.Where(company => company.Industry != null && company.Industry.Contains(model.CompanyIndustry.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(model.CompanyStatus))
            {
                companies = companies.Where(company => company.Approval_status == model.CompanyStatus);
            }
            model.Companies = await companies.OrderBy(company => company.Company_name).Take(100).ToListAsync();

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> ApplicationDetails(int applicationId)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Application_id == applicationId);
            if (application is null)
            {
                return NotFound();
            }

            return View(new AdminApplicationDetailsViewModel
            {
                Application = application,
                Investor = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.User_id == application.User_id),
                Ipo = await _context.IPOs.AsNoTracking().FirstOrDefaultAsync(ipo => ipo.IPO_id == application.IPO_id)
            });
        }

        [HttpGet]
        public async Task<IActionResult> CreateNotification()
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            return View(await BuildNotificationModelAsync(new CreateNotificationViewModel()));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateNotification(CreateNotificationViewModel model)
        {
            if (!IsAdmin())
            {
                return Forbid();
            }

            if (model.RecipientType == "User" && !model.UserId.HasValue)
            {
                ModelState.AddModelError(nameof(model.UserId), "Select a specific user.");
            }
            if (model.RecipientType == "Company" && !model.CompanyId.HasValue)
            {
                ModelState.AddModelError(nameof(model.CompanyId), "Select a specific company.");
            }

            if (!ModelState.IsValid)
            {
                return View(await BuildNotificationModelAsync(model));
            }

            int[] recipientIds;
            switch (model.RecipientType)
            {
                case "Investors":
                    recipientIds = await _context.Users.Where(user => user.Role == "Investor" && user.Is_active).Select(user => user.User_id).ToArrayAsync();
                    break;
                case "Companies":
                    recipientIds = await _context.Users.Where(user => user.Role == "Company" && user.Is_active).Select(user => user.User_id).ToArrayAsync();
                    break;
                case "User":
                    bool userExists = await _context.Users.AnyAsync(user => user.User_id == model.UserId && user.Is_active);
                    if (!userExists)
                    {
                        ModelState.AddModelError(nameof(model.UserId), "The selected user is unavailable.");
                        return View(await BuildNotificationModelAsync(model));
                    }
                    recipientIds = new[] { model.UserId!.Value };
                    break;
                case "Company":
                    int? companyUserId = await _context.Companies
                        .Where(company => company.Company_id == model.CompanyId)
                        .Select(company => (int?)company.User_id)
                        .FirstOrDefaultAsync();
                    if (!companyUserId.HasValue || !await _context.Users.AnyAsync(user => user.User_id == companyUserId && user.Is_active))
                    {
                        ModelState.AddModelError(nameof(model.CompanyId), "The selected company account is unavailable.");
                        return View(await BuildNotificationModelAsync(model));
                    }
                    recipientIds = new[] { companyUserId.Value };
                    break;
                default:
                    ModelState.AddModelError(nameof(model.RecipientType), "Select a valid recipient.");
                    return View(await BuildNotificationModelAsync(model));
            }

            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(recipientIds.Select(userId => new SystemNotification
            {
                User_id = userId,
                Title = model.Title.Trim(),
                Message = model.Message.Trim(),
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"Notification sent to {recipientIds.Length} recipient(s).";

            User[] recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.User_id) && user.Is_active)
                .ToArrayAsync();
            foreach (User recipient in recipients)
            {
                await TrySendEventEmailAsync(
                    recipient,
                    model.Title.Trim(),
                    model.Message.Trim());
            }
            return RedirectToAction(nameof(CreateNotification));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeactivateUser(int userId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            int? currentUserId = HttpContext.Session.GetInt32("User_id");
            if (currentUserId == userId)
            {
                TempData["AdminMessage"] = "The active administrator account cannot be deactivated.";
                return RedirectToAction(nameof(UserDetails), new { userId });
            }

            User? user = await _context.Users.FindAsync(userId);
            if (user is null)
            {
                return NotFound();
            }

            if (!user.Is_active)
            {
                TempData["AdminMessage"] = "This user account is already inactive.";
                return RedirectToAction(nameof(UserDetails), new { userId });
            }

            user.Is_active = false;
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{user.First_name} {user.Last_name}'s account was deactivated.";
            return RedirectToAction(nameof(UserDetails), new { userId });
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
            await TrySendEventEmailAsync(
                user,
                "Company Registration Approved",
                $"{company.Company_name} registration has been approved. You can now access the Company Dashboard.");

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
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null || !string.Equals(ipo.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That IPO request is no longer pending.";
                return RedirectToAction(nameof(Index));
            }
            ipo.Status = "Approved";
            await _context.SaveChangesAsync();
            User? companyUser = await _context.Companies
                .Where(company => company.Company_id == ipo.Company_id)
                .Join(_context.Users, company => company.User_id, user => user.User_id, (_, user) => user)
                .FirstOrDefaultAsync();
            if (companyUser is not null)
            {
                await TrySendEventEmailAsync(
                    companyUser,
                    "IPO Approved",
                    $"{ipo.IPO_name} has been approved and can now be published.");
            }
            TempData["AdminMessage"] = $"{ipo.IPO_name} was approved and can now be published publicly.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            if (!string.Equals(ipo.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "Only an approved IPO can be published.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }
            ipo.Status = "Open";
            await _context.SaveChangesAsync();
            User[] investors = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .ToArrayAsync();
            DateTime createdAt = DateTime.UtcNow;
            _context.SystemNotifications.AddRange(investors.Select(investor => new SystemNotification
            {
                User_id = investor.User_id,
                Title = "IPO Available",
                Message = $"{ipo.IPO_name} is now available to investors.",
                Created_at = createdAt,
                Is_read = false
            }));
            await _context.SaveChangesAsync();
            foreach (User investor in investors)
            {
                await TrySendEventEmailAsync(
                    investor,
                    "IPO Available",
                    $"{ipo.IPO_name} is now available to investors. Log in to review the offering.");
            }
            TempData["AdminMessage"] = $"{ipo.IPO_name} was published and is now visible to investors.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DenyIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            if (string.Equals(ipo.Status, "Rejected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ipo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That IPO is already closed and cannot be rejected again.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }
            ipo.Status = "Rejected";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} was rejected and is no longer available.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelIpo(int ipoId)
        {
            if (!IsAdmin()) return Forbid();
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            ipo.Status = "Cancelled";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} was cancelled.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeIpoStatus(int ipoId, string status)
        {
            if (!IsAdmin()) return Forbid();
            string[] validStatuses = { "Pending", "Approved", "Open", "Closed", "Listed", "Rejected", "Cancelled" };
            string? selectedStatus = validStatuses.FirstOrDefault(value =>
                string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
            IPO? ipo = await _context.IPOs.FindAsync(ipoId);
            if (ipo is null) return NotFound();
            if (selectedStatus is null)
            {
                TempData["AdminMessage"] = "Please select a valid IPO status.";
                return RedirectToAction(nameof(IpoDetails), new { ipoId });
            }
            ipo.Status = selectedStatus;
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"{ipo.IPO_name} status changed to {selectedStatus}.";
            return RedirectToAction(nameof(IpoDetails), new { ipoId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveApplication(int applicationId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications.FindAsync(applicationId);
            if (application is null || !string.Equals(application.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That investor application is no longer pending.";
                return Redirect($"{Url.Action(nameof(Index))}#application-requests");
            }

            application.Status = "Approved";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"Investor application #{application.Application_id} was approved.";
            return Redirect($"{Url.Action(nameof(Index))}#application-requests");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DenyApplication(int applicationId)
        {
            if (!string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            IPOApplication? application = await _context.IPOApplications.FindAsync(applicationId);
            if (application is null || !string.Equals(application.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                TempData["AdminMessage"] = "That investor application is no longer pending.";
                return Redirect($"{Url.Action(nameof(Index))}#application-requests");
            }

            application.Status = "Rejected";
            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"Investor application #{application.Application_id} was denied.";
            return Redirect($"{Url.Action(nameof(Index))}#application-requests");
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
            model.Notifications = await LoadNotificationsAsync(userId);
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
                user.Role == "Investor");
            model.CompanyCount = await _context.Companies.CountAsync();
            model.IpoCount = await _context.IPOs.CountAsync();
            model.PendingCompanyCount = await _context.Companies.CountAsync(company => company.Approval_status == "Pending");
            model.PendingApplicationCount = await _context.IPOApplications.CountAsync(application => application.Status == "Pending");
            model.PendingIpoCount = await _context.IPOs.CountAsync(ipo => ipo.Status == "Pending");
            DateTime today = DateTime.UtcNow.Date;
            model.ActiveIpoCount = await _context.IPOs.CountAsync(ipo =>
                (ipo.Status == "Approved" || ipo.Status == "Open") &&
                ipo.Open_date.Date <= today &&
                ipo.Close_date.Date >= today);
            model.ListedIpoCount = await _context.IPOs.CountAsync(ipo =>
                ipo.Status == "Listed" ||
                (ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= today));
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
            model.PendingApplications = await _context.IPOApplications
                .AsNoTracking()
                .Where(application => application.Status == "Pending")
                .OrderBy(application => application.Application_date)
                .Take(10)
                .ToListAsync();
            int[] pendingApplicationIpoIds = model.PendingApplications.Select(application => application.IPO_id).Distinct().ToArray();
            int[] pendingApplicationUserIds = model.PendingApplications.Select(application => application.User_id).Distinct().ToArray();
            model.PendingApplicationIpoNames = await _context.IPOs
                .AsNoTracking()
                .Where(ipo => pendingApplicationIpoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id, ipo => ipo.IPO_name);
            model.PendingApplicationUserNames = await _context.Users
                .AsNoTracking()
                .Where(user => pendingApplicationUserIds.Contains(user.User_id))
                .ToDictionaryAsync(user => user.User_id, user => $"{user.First_name} {user.Last_name}".Trim());
            model.PendingCompanies = await _context.Companies
                .AsNoTracking()
                .Where(company => company.Approval_status == "Pending")
                .OrderBy(company => company.Created_at)
                .Take(10)
                .ToListAsync();
            model.RecentInvestors = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Investor")
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToListAsync();
            model.RecentCompanies = await _context.Users
                .AsNoTracking()
                .Where(user => user.Role == "Company")
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToListAsync();
            model.RecentUsers = model.RecentInvestors
                .Concat(model.RecentCompanies)
                .OrderByDescending(user => user.Created_at)
                .Take(5)
                .ToList();
            model.RecentApplications = await _context.IPOApplications
                .AsNoTracking()
                .OrderByDescending(application => application.Application_date)
                .Take(5)
                .ToListAsync();
        }

        private bool IsAdmin()
        {
            return string.Equals(HttpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<CreateNotificationViewModel> BuildNotificationModelAsync(CreateNotificationViewModel model)
        {
            model.Investors = await _context.Users.AsNoTracking()
                .Where(user => user.Role == "Investor" && user.Is_active)
                .OrderBy(user => user.First_name).ThenBy(user => user.Last_name).ToListAsync();
            model.Companies = await _context.Users.AsNoTracking()
                .Where(user => user.Role == "Company" && user.Is_active)
                .OrderBy(user => user.First_name).ThenBy(user => user.Last_name).ToListAsync();
            model.CompanyProfiles = await _context.Companies.AsNoTracking()
                .OrderBy(company => company.Company_name).ToListAsync();
            return model;
        }

        private async Task LoadCompanyDashboardAsync(DashboardViewModel model, int userId)
        {
            model.Notifications = await LoadNotificationsAsync(userId);
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

        private async Task<List<SystemNotification>> LoadNotificationsAsync(int userId)
        {
            return await _context.SystemNotifications
                .AsNoTracking()
                .Where(notification => notification.User_id == userId)
                .OrderByDescending(notification => notification.Created_at)
                .Take(5)
                .ToListAsync();
        }

        private async Task TrySendEventEmailAsync(User user, string subject, string body)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return;
            }

            try
            {
                await _emailNotificationService.SendAsync(
                    user.Email,
                    $"{user.First_name} {user.Last_name}".Trim(),
                    subject,
                    body);
            }
            catch (SmtpException exception)
            {
                TempData["AdminMessage"] = $"The database update succeeded, but the email to {user.Email} could not be sent: {exception.Message}";
            }
            catch (InvalidOperationException exception)
            {
                TempData["AdminMessage"] = $"The database update succeeded, but email notifications are not configured: {exception.Message}";
            }
            catch (FormatException exception)
            {
                TempData["AdminMessage"] = $"The database update succeeded, but the email address is invalid: {exception.Message}";
            }
        }
    }
}