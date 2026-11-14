using IPOInvestmentManagement.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Hubs
{
    public class MarketHub : Hub
    {
        public const string MarketFeedGroup = "MarketFeed";
        public const string InvestorsGroup = "Investors";
        private readonly IMarketDataProvider _marketDataProvider;
        private readonly ILogger<MarketHub> _logger;

        public MarketHub(IMarketDataProvider marketDataProvider, ILogger<MarketHub> logger)
        {
            _marketDataProvider = marketDataProvider;
            _logger = logger;
        }

        public async Task JoinMarketFeed()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MarketFeedGroup);
            _logger.LogInformation("Connection {ConnectionId} joined market feed.", Context.ConnectionId);
        }

        public async Task LeaveMarketFeed()
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MarketFeedGroup);
        }

        public async Task JoinInvestorGroup()
        {
            var httpContext = Context.GetHttpContext();
            string? role = null;
            if (httpContext != null)
            {
                try
                {
                    role = httpContext.Session?.GetString("UserRole");
                }
                catch
                {
                    // Session might be uninitialized
                }
            }

            if (string.Equals(role, "Investor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, InvestorsGroup);
                _logger.LogInformation("Connection {ConnectionId} with role '{Role}' joined investor group.", Context.ConnectionId, role);
            }
            else
            {
                _logger.LogWarning("Unauthorized attempt to join investor group from connection {ConnectionId} with role '{Role}'.", Context.ConnectionId, role ?? "Anonymous");
                throw new HubException("Access denied: Only authenticated investors and administrators may join the investor group.");
            }
        }

        public async Task BroadcastToInvestors(string title, string message, string category)
        {
            EnsureAdminCaller("BroadcastToInvestors");

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
            {
                throw new HubException("Broadcast title and message are required.");
            }

            var payload = new
            {
                Title = title.Trim(),
                Message = message.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "Announcement" : category.Trim(),
                Timestamp = DateTime.Now.ToString("hh:mm:ss tt"),
                Date = DateTime.Now.ToString("dd MMM yyyy")
            };

            await Clients.All.SendAsync("ReceiveBroadcastMessage", payload);
            _logger.LogInformation("Admin broadcasted message: {Title}", title);
        }

        public Task NotifyTradeExecuted(string stockName, string exchange, string tradeType, int quantity, decimal price, string userDisplayName)
        {
            // Security enforcement: Direct client invocation of trade notifications is strictly forbidden.
            // All trade notifications must originate exclusively from server-side trade validation pipelines (e.g. ExecuteTrade / Buy / Sell).
            _logger.LogWarning("Security Alert: Blocked client attempt to invoke NotifyTradeExecuted from connection {ConnectionId}.", Context.ConnectionId);
            throw new HubException("Security violation: Trade notifications cannot be fabricated or dispatched directly by clients. All trades must be processed and validated through the server trading engine.");
        }

        public Task<bool> ToggleSimulation()
        {
            EnsureAdminCaller("ToggleSimulation");
            _marketDataProvider.IsSimulationMode = !_marketDataProvider.IsSimulationMode;
            _logger.LogInformation("Admin toggled simulation mode to {Status}.", _marketDataProvider.IsSimulationMode);
            return Task.FromResult(_marketDataProvider.IsSimulationMode);
        }

        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MarketFeedGroup);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, MarketFeedGroup);
            await base.OnDisconnectedAsync(exception);
        }

        private void EnsureAdminCaller(string operation)
        {
            var httpContext = Context.GetHttpContext();
            int? userId = null;
            string? role = null;

            if (httpContext != null)
            {
                try
                {
                    userId = httpContext.Session?.GetInt32("User_id") ?? httpContext.Session?.GetInt32("UserId");
                    role = httpContext.Session?.GetString("UserRole");
                }
                catch
                {
                    // In case session state is uninitialized
                }

                if (string.IsNullOrWhiteSpace(role) && httpContext.User?.Identity?.IsAuthenticated == true)
                {
                    if (httpContext.User.IsInRole("Admin"))
                    {
                        return;
                    }
                }
            }

            if (!userId.HasValue || !string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Trading Security Violation: Unauthorized attempt to invoke '{Operation}' on MarketHub from connection {ConnectionId} with role '{Role}'.", operation, Context.ConnectionId, role ?? "Anonymous");
                throw new HubException($"Access denied: Only platform administrators are authorized to execute '{operation}'. Investors and companies are not permitted to configure trading security or broadcast market events.");
            }
        }
    }
}
