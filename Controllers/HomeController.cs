using System.Diagnostics;
using IPOInvestmentManagement.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Controllers
{
    public class HomeController : Controller
    {
        private readonly ApplicationDbContext _context;

        public HomeController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            ViewBag.UserCount = await _context.Users.CountAsync();
            ViewBag.CompanyCount = await _context.Companies.CountAsync();
            ViewBag.IPOCount = await _context.IPOs.CountAsync();

            return View();
        }
    }
}