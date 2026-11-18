using IPOInvestmentManagement.Data;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Services
{
    public sealed class MarketTradingCalendar(
        ApplicationDbContext context,
        IConfiguration configuration) : IMarketTradingCalendar
    {
        public async Task<bool> IsTradingDayAsync(DateTime date, CancellationToken cancellationToken = default)
        {
            DateTime day = date.Date;
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return false;
            }

            return !await context.MarketTradingHolidays
                .AsNoTracking()
                .AnyAsync(holiday => holiday.HolidayDate.Date == day, cancellationToken);
        }

        public async Task<bool> IsMarketOpenAsync(DateTime now, CancellationToken cancellationToken = default)
        {
            if (configuration.GetValue<bool>("MarketTrading:AlwaysOpen", false))
            {
                return true;
            }

            if (!await IsTradingDayAsync(now, cancellationToken))
            {
                return false;
            }

            string? openStr = configuration["MarketTrading:OpeningTime"];
            string? closeStr = configuration["MarketTrading:ClosingTime"];
            TimeSpan opening = TimeSpan.TryParse(openStr, out var ot) ? ot : new TimeSpan(9, 30, 0);
            TimeSpan closing = TimeSpan.TryParse(closeStr, out var ct) ? ct : new TimeSpan(15, 30, 0);
            return now.TimeOfDay >= opening && now.TimeOfDay < closing;
        }
    }
}
