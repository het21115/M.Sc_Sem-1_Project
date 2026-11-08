using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Services
{
    public sealed class StockMarketService(
        ApplicationDbContext context,
        IMarketTradingCalendar calendar) : IStockMarketService
    {
        public async Task<MarketDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
        {
            DateTime now = GetIndiaStandardTime();
            bool marketOpen = await calendar.IsMarketOpenAsync(now, cancellationToken);
            List<MarketDataViewModel> rows = await LoadRowsAsync(null, now, marketOpen, cancellationToken);
            return new MarketDashboardViewModel
            {
                IsMarketOpen = marketOpen,
                MarketStatus = marketOpen ? "Open" : "Closed",
                LastUpdated = rows.Where(row => row.LastUpdated.HasValue).MaxBy(row => row.LastUpdated)?.LastUpdated,
                Listings = rows,
                TopGainers = rows.Where(row => row.IsAvailable && row.PriceChangePercentage > 0).OrderByDescending(row => row.PriceChangePercentage).Take(5).ToList(),
                TopLosers = rows.Where(row => row.IsAvailable && row.PriceChangePercentage < 0).OrderBy(row => row.PriceChangePercentage).Take(5).ToList()
            };
        }

        public async Task<MarketDataViewModel?> GetMarketDataAsync(int ipoId, CancellationToken cancellationToken = default)
        {
            DateTime now = GetIndiaStandardTime();
            bool marketOpen = await calendar.IsMarketOpenAsync(now, cancellationToken);
            return (await LoadRowsAsync(ipoId, now, marketOpen, cancellationToken)).SingleOrDefault();
        }

        private async Task<List<MarketDataViewModel>> LoadRowsAsync(
            int? ipoId,
            DateTime now,
            bool marketOpen,
            CancellationToken cancellationToken)
        {
            IQueryable<IPO> ipoQuery = context.IPOs.AsNoTracking()
                .Where(ipo => (ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Listed" || ipo.Status == "Closed") &&
                    ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= now.Date);
            if (ipoId.HasValue)
            {
                ipoQuery = ipoQuery.Where(ipo => ipo.IPO_id == ipoId.Value);
            }

            List<IPO> ipos = await ipoQuery.ToListAsync(cancellationToken);
            int[] ids = ipos.Select(ipo => ipo.IPO_id).ToArray();
            Dictionary<int, Company> companies = await context.Companies.AsNoTracking()
                .Where(company => ipos.Select(ipo => ipo.Company_id).Contains(company.Company_id))
                .ToDictionaryAsync(company => company.Company_id, cancellationToken);
            Dictionary<int, IPOListing> listings = await context.IPOListings.AsNoTracking()
                .Where(listing => ids.Contains(listing.IPO_id))
                .ToDictionaryAsync(listing => listing.IPO_id, cancellationToken);
            List<StockMarketData> marketDataRows = await context.StockMarketData.AsNoTracking()
                .Where(marketData => ids.Contains(marketData.IPOId))
                .OrderByDescending(marketData => marketData.LastUpdated)
                .ThenByDescending(marketData => marketData.IsAvailable)
                .ToListAsync(cancellationToken);
            Dictionary<int, StockMarketData> data = marketDataRows
                .GroupBy(marketData => marketData.IPOId)
                .ToDictionary(group => group.Key, group => group.First());

            return ipos.Select(ipo =>
            {
                listings.TryGetValue(ipo.IPO_id, out IPOListing? listing);
                data.TryGetValue(ipo.IPO_id, out StockMarketData? marketData);
                decimal? current = marketData?.CurrentPrice ?? listing?.NSE_closing_price ?? listing?.BSE_closing_price ?? listing?.NSE_listing_price ?? listing?.BSE_listing_price;
                decimal? previous = marketData?.PreviousClose ?? listing?.NSE_listing_price ?? listing?.BSE_listing_price;
                decimal? change = current.HasValue && previous.HasValue ? current - previous : null;
                decimal? percentage = change.HasValue && previous > 0 ? change / previous * 100m : null;
                return new MarketDataViewModel
                {
                    IpoId = ipo.IPO_id,
                    IpoName = ipo.IPO_name,
                    CompanyName = companies.GetValueOrDefault(ipo.Company_id)?.Company_name ?? "Company",
                    StockSymbol = marketData?.StockSymbol ?? listing?.StockSymbol ?? string.Empty,
                    Exchange = marketData?.Exchange ?? listing?.Exchange ?? string.Empty,
                    IssuePrice = marketData?.IssuePrice ?? listing?.Issue_price ?? ipo.Price_max ?? ipo.Price_min,
                    ListingPrice = marketData?.ListingPrice ?? listing?.NSE_listing_price ?? listing?.BSE_listing_price,
                    CurrentPrice = current,
                    PreviousClose = previous,
                    PriceChange = marketData?.PriceChange ?? change,
                    PriceChangePercentage = marketData?.PriceChangePercentage ?? percentage,
                    DayHigh = marketData?.DayHigh,
                    DayLow = marketData?.DayLow,
                    Volume = marketData?.Volume,
                    LastUpdated = marketData?.LastUpdated ?? listing?.Listing_date,
                    IsAvailable = current.HasValue && current > 0,
                    IsMarketOpen = marketOpen,
                    CanTrade = marketData?.IsAvailable == true && current > 0,
                    IsListingToday = ipo.Listing_date?.Date == now.Date
                };
            }).ToList();
        }

        private static DateTime GetIndiaStandardTime()
        {
            TimeZoneInfo indiaTimeZone;
            try
            {
                indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch (InvalidTimeZoneException)
            {
                indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }

            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, indiaTimeZone);
        }
    }
}
