namespace IPOInvestmentManagement.Services
{
    public sealed record MarketQuote(
        string Symbol,
        string Exchange,
        decimal CurrentPrice,
        decimal PreviousClose,
        decimal DayHigh,
        decimal DayLow,
        long Volume,
        DateTime LastUpdated);

    public interface IMarketDataProvider
    {
        Task<MarketQuote?> GetQuoteAsync(string symbol, string exchange, CancellationToken cancellationToken = default);
    }
}
