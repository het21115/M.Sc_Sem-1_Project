namespace IPOInvestmentManagement.Services
{
    public interface IMarketTradingCalendar
    {
        Task<bool> IsTradingDayAsync(DateTime date, CancellationToken cancellationToken = default);
        Task<bool> IsMarketOpenAsync(DateTime now, CancellationToken cancellationToken = default);
    }
}
