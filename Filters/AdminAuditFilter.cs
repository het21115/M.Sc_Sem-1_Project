using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IPOInvestmentManagement.Filters
{
    public sealed class AdminAuditFilter : IAsyncActionFilter
    {
        private readonly ApplicationDbContext _context;

        public AdminAuditFilter(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task OnActionExecutionAsync(
            ActionExecutingContext context,
            ActionExecutionDelegate next)
        {
            await next();
            HttpContext httpContext = context.HttpContext;
            if (httpContext.Response.StatusCode >= 400 ||
                !string.Equals(httpContext.Session.GetString("UserRole"), "Admin", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string action = context.ActionDescriptor.DisplayName ?? context.ActionDescriptor.Id;
            string details = $"{httpContext.Request.Method} {httpContext.Request.Path}";
            _context.AuditLogs.Add(new AuditLog
            {
                ActorUserId = httpContext.Session.GetInt32("User_id"),
                Action = action.Length > 100 ? action[..100] : action,
                EntityType = context.Controller.GetType().Name,
                Details = details.Length > 1000 ? details[..1000] : details,
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
        }
    }
}
