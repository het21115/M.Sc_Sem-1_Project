using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace IPOInvestmentManagement.Hubs
{
    public class MarketHub : Hub
    {
        public const string MarketFeedGroup = "MarketFeed";
        public const string InvestorsGroup = "Investors";
        private readonly ILogger<MarketHub> _logger;

        public MarketHub(ILogger<MarketHub> logger)
        {
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
            await Groups.AddToGroupAsync(Context.ConnectionId, InvestorsGroup);
        }

        public async Task BroadcastToInvestors(string title, string message, string category)
        {
            var payload = new
            {
                Title = title,
                Message = message,
                Category = string.IsNullOrWhiteSpace(category) ? "Announcement" : category,
                Timestamp = DateTime.Now.ToString("hh:mm:ss tt"),
                Date = DateTime.Now.ToString("dd MMM yyyy")
            };

            await Clients.All.SendAsync("ReceiveBroadcastMessage", payload);
            _logger.LogInformation("Admin broadcasted message: {Title}", title);
        }

        public async Task NotifyTradeExecuted(string stockName, string exchange, string tradeType, int quantity, decimal price, string userDisplayName)
        {
            var payload = new
            {
                StockName = stockName,
                Exchange = exchange,
                TradeType = tradeType.ToUpperInvariant(),
                Quantity = quantity,
                Price = price,
                TotalAmount = quantity * price,
                User = string.IsNullOrWhiteSpace(userDisplayName) ? "Simulated Investor" : userDisplayName,
                Timestamp = DateTime.Now.ToString("hh:mm:ss tt")
            };

            await Clients.All.SendAsync("ReceiveTradeNotification", payload);
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
    }
}
