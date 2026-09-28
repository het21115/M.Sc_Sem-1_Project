using System.Diagnostics;
using IPOInvestmentManagement.Data;
using Microsoft.AspNetCore.Mvc;

namespace IPOInvestmentManagement.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;

        public HomeController(ApplicationDbContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var users = _context.Users.ToList();
            var companies = _context.Companies.ToList();
            var ipos = _context.IPOs.ToList();

            ViewBag.UserCount = users.Count;
            ViewBag.CompanyCount = companies.Count;
            ViewBag.IPOCount = ipos.Count;

            return View();
        }
    }
}