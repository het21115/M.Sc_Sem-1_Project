using System.Security.Claims;
using IPOInvestmentManagement.Controllers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IPOInvestmentManagement.Filters
{
    public static class TestClockAccessGuard
    {
        public static bool IsTestClockEnvironment(IHostEnvironment environment)
        {
            return environment.IsDevelopment() ||
                   environment.IsEnvironment("Testing") ||
                   environment.IsEnvironment("Test");
        }

        public static IActionResult? Validate(HttpContext? httpContext, IHostEnvironment environment)
        {
            if (!IsTestClockEnvironment(environment))
            {
                return new NotFoundResult();
            }

            ISession? session = httpContext?.Features.Get<ISessionFeature>()?.Session;
            int? userId = session?.GetInt32("User_id") ?? session?.GetInt32("UserId");
            string? sessionRole = session?.GetString("UserRole");

            ClaimsPrincipal? user = httpContext?.User;
            bool isClaimsAuthenticated = user?.Identity?.IsAuthenticated == true;
            bool isAuthenticated = userId.HasValue || isClaimsAuthenticated;

            if (!isAuthenticated)
            {
                return new UnauthorizedObjectResult(new
                {
                    success = false,
                    message = "Authentication required. Please sign in as an administrator."
                });
            }

            bool isSessionAdmin = userId.HasValue &&
                string.Equals(sessionRole, "Admin", StringComparison.OrdinalIgnoreCase);
            bool isClaimsAdmin = isClaimsAuthenticated &&
                user?.IsInRole("Admin") == true;

            if (!isSessionAdmin && !isClaimsAdmin)
            {
                return new ObjectResult(new
                {
                    success = false,
                    message = "Access denied: Only administrators are authorized to access test-clock endpoints."
                })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
            }

            return null;
        }
    }

    public sealed class TestClockAccessFilter : IAuthorizationFilter
    {
        private readonly IHostEnvironment _environment;

        public TestClockAccessFilter(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            if (TestClockAccessGuard.Validate(context.HttpContext, _environment) is { } denied)
            {
                context.Result = denied;
            }
        }
    }

    public sealed class TestClockEnvironmentConvention : IControllerModelConvention
    {
        private readonly IHostEnvironment _environment;

        public TestClockEnvironmentConvention(IHostEnvironment environment)
        {
            _environment = environment;
        }

        public void Apply(ControllerModel controller)
        {
            if (controller.ControllerType.AsType() == typeof(IpoTestClockController) &&
                !TestClockAccessGuard.IsTestClockEnvironment(_environment))
            {
                controller.Selectors.Clear();
                controller.Actions.Clear();
            }
        }
    }
}
