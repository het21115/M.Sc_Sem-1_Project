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
            DateTime istDate = ToIndiaStandardTime(date);
            DateTime day = istDate.Date;
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
            DateTime istNow = ToIndiaStandardTime(now);

            // Stock exchanges (NSE & BSE) are strictly closed on weekends (Saturday and Sunday) and trading holidays.
            if (!await IsTradingDayAsync(istNow, cancellationToken))
            {
                return false;
            }

            // AlwaysOpen only bypasses the daily 09:30-15:30 trading hours on valid trading days (Monday to Friday)
            if (configuration.GetValue<bool>("MarketTrading:AlwaysOpen", false))
            {
                return true;
            }

            string? openStr = configuration["MarketTrading:OpeningTime"];
            string? closeStr = configuration["MarketTrading:ClosingTime"];
            TimeSpan opening = TimeSpan.TryParse(openStr, out var ot) ? ot : new TimeSpan(9, 30, 0);
            TimeSpan closing = TimeSpan.TryParse(closeStr, out var ct) ? ct : new TimeSpan(15, 30, 0);
            return istNow.TimeOfDay >= opening && istNow.TimeOfDay < closing;
        }

        private static DateTime ToIndiaStandardTime(DateTime time)
        {
            if (time.Kind == DateTimeKind.Utc)
            {
                try
                {
                    return TimeZoneInfo.ConvertTimeFromUtc(time, TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
                }
                catch
                {
                    try
                    {
                        return TimeZoneInfo.ConvertTimeFromUtc(time, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
                    }
                    catch
                    {
                        return time.AddHours(5).AddMinutes(30);
                    }
                }
            }
            return time;
        }
    }
}
