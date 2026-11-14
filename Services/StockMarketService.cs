using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Services
{
    public sealed class StockMarketService(
        ApplicationDbContext context,
        IMarketTradingCalendar calendar,
        IMarketDataProvider marketDataProvider) : IStockMarketService
    {
        public async Task<MarketDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
        {
            DateTime now = GetIndiaStandardTime();
            bool isCalendarOpen = await calendar.IsMarketOpenAsync(now, cancellationToken);
            bool marketOpen = isCalendarOpen || marketDataProvider.IsSimulationMode;

            List<MarketDataViewModel> rows = await LoadRowsAsync(null, now, marketOpen, cancellationToken);
            return new MarketDashboardViewModel
            {
                IsMarketOpen = marketOpen,
                MarketStatus = marketOpen ? "Open" : "Closed",
                LastUpdated = rows.Where(row => row.LastUpdated.HasValue).MaxBy(row => row.LastUpdated)?.LastUpdated ?? now,
                Listings = rows,
                TopGainers = rows.Where(row => row.IsAvailable && (row.PriceChangePercentage > 0 || row.NsePriceChangePercentage > 0))
                    .OrderByDescending(row => row.PriceChangePercentage ?? row.NsePriceChangePercentage ?? 0)
                    .Take(5).ToList(),
                TopLosers = rows.Where(row => row.IsAvailable && (row.PriceChangePercentage < 0 || row.NsePriceChangePercentage < 0))
                    .OrderBy(row => row.PriceChangePercentage ?? row.NsePriceChangePercentage ?? 0)
                    .Take(5).ToList(),
                IsSimulationActive = marketDataProvider.IsSimulationMode
            };
        }

        public async Task<MarketDataViewModel?> GetMarketDataAsync(int ipoId, CancellationToken cancellationToken = default)
        {
            DateTime now = GetIndiaStandardTime();
            bool isCalendarOpen = await calendar.IsMarketOpenAsync(now, cancellationToken);
            bool marketOpen = isCalendarOpen || marketDataProvider.IsSimulationMode;
            return (await LoadRowsAsync(ipoId, now, marketOpen, cancellationToken)).SingleOrDefault();
        }

        private async Task<List<MarketDataViewModel>> LoadRowsAsync(
            int? ipoId,
            DateTime now,
            bool marketOpen,
            CancellationToken cancellationToken)
        {
            IQueryable<IPO> ipoQuery = context.IPOs.AsNoTracking()
                .Where(ipo => ipo.Status == "Listed" ||
                    ((ipo.Status == "Approved" || ipo.Status == "Published" || ipo.Status == "Open" || ipo.Status == "Closed") &&
                     ipo.Listing_date.HasValue && ipo.Listing_date.Value.Date <= now.Date) ||
                    context.IPOListings.Any(l => l.IPO_id == ipo.IPO_id));

            if (ipoId.HasValue)
            {
                ipoQuery = ipoQuery.Where(ipo => ipo.IPO_id == ipoId.Value);
            }

            List<IPO> ipos = await ipoQuery.ToListAsync(cancellationToken);

            // Fallback: If no IPO matches the listing date <= today, include any listed/approved IPOs for demonstration
            if (ipos.Count == 0 && !ipoId.HasValue)
            {
                ipos = await context.IPOs.AsNoTracking()
                    .Where(ipo => ipo.Status == "Listed" || ipo.Status == "Published" || ipo.Status == "Approved" || ipo.Status == "Open" || ipo.Status == "Closed")
                    .Take(10)
                    .ToListAsync(cancellationToken);
            }
            else if (ipos.Count == 0 && ipoId.HasValue)
            {
                ipos = await context.IPOs.AsNoTracking()
                    .Where(ipo => ipo.IPO_id == ipoId.Value)
                    .ToListAsync(cancellationToken);
            }

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
                .ToListAsync(cancellationToken);

            return ipos.Select(ipo =>
            {
                listings.TryGetValue(ipo.IPO_id, out IPOListing? listing);
                StockMarketData? nseData = marketDataRows.FirstOrDefault(r => r.IPOId == ipo.IPO_id && string.Equals(r.Exchange, "NSE", StringComparison.OrdinalIgnoreCase));
                StockMarketData? bseData = marketDataRows.FirstOrDefault(r => r.IPOId == ipo.IPO_id && string.Equals(r.Exchange, "BSE", StringComparison.OrdinalIgnoreCase));

                decimal defaultBase = listing?.NSE_listing_price
                    ?? listing?.BSE_listing_price
                    ?? listing?.Issue_price
                    ?? ipo.Price_max
                    ?? ipo.Price_min
                    ?? 100m;

                decimal nseCurrent = nseData?.CurrentPrice ?? listing?.NSE_closing_price ?? listing?.NSE_listing_price ?? defaultBase;
                decimal nsePrevious = nseData?.PreviousClose ?? listing?.NSE_listing_price ?? listing?.Issue_price ?? defaultBase;
                decimal nseChange = nseData?.PriceChange ?? (nseCurrent - nsePrevious);
                decimal nseChangePct = nseData?.PriceChangePercentage ?? (nsePrevious > 0 ? (nseChange / nsePrevious * 100m) : 0m);

                decimal bseCurrent = bseData?.CurrentPrice ?? listing?.BSE_closing_price ?? listing?.BSE_listing_price ?? Math.Round(defaultBase * 0.999m, 2);
                decimal bsePrevious = bseData?.PreviousClose ?? listing?.BSE_listing_price ?? listing?.Issue_price ?? defaultBase;
                decimal bseChange = bseData?.PriceChange ?? (bseCurrent - bsePrevious);
                decimal bseChangePct = bseData?.PriceChangePercentage ?? (bsePrevious > 0 ? (bseChange / bsePrevious * 100m) : 0m);

                decimal primaryCurrent = nseCurrent > 0 ? nseCurrent : bseCurrent;
                decimal primaryPrevious = nsePrevious > 0 ? nsePrevious : bsePrevious;
                decimal primaryChange = nseChange;
                decimal primaryPercentage = nseChangePct;

                string stockSymbol = nseData?.StockSymbol ?? bseData?.StockSymbol ?? listing?.StockSymbol ?? (ipo.IPO_name.Length <= 8 ? ipo.IPO_name.ToUpperInvariant() : ipo.IPO_name[..8].ToUpperInvariant());

                return new MarketDataViewModel
                {
                    IpoId = ipo.IPO_id,
                    IpoName = ipo.IPO_name,
                    CompanyName = companies.GetValueOrDefault(ipo.Company_id)?.Company_name ?? "Company",
                    StockSymbol = stockSymbol,
                    Exchange = "NSE",
                    IssuePrice = listing?.Issue_price ?? ipo.Price_max ?? ipo.Price_min,
                    ListingPrice = listing?.NSE_listing_price ?? listing?.BSE_listing_price,

                    CurrentPrice = primaryCurrent,
                    PreviousClose = primaryPrevious,
                    PriceChange = primaryChange,
                    PriceChangePercentage = primaryPercentage,
                    DayHigh = nseData?.DayHigh ?? bseData?.DayHigh ?? Math.Max(primaryCurrent, primaryPrevious),
                    DayLow = nseData?.DayLow ?? bseData?.DayLow ?? Math.Min(primaryCurrent, primaryPrevious),
                    Volume = (nseData?.Volume ?? 0) + (bseData?.Volume ?? 0),
                    LastUpdated = nseData?.LastUpdated ?? bseData?.LastUpdated ?? listing?.Listing_date ?? now,

                    // Dedicated NSE
                    NsePrice = nseCurrent,
                    NseListingPrice = listing?.NSE_listing_price,
                    NsePreviousClose = nsePrevious,
                    NsePriceChange = nseChange,
                    NsePriceChangePercentage = nseChangePct,
                    NseDayHigh = nseData?.DayHigh ?? Math.Max(nseCurrent, nsePrevious),
                    NseDayLow = nseData?.DayLow ?? Math.Min(nseCurrent, nsePrevious),
                    NseVolume = nseData?.Volume ?? 45000,

                    // Dedicated BSE
                    BsePrice = bseCurrent,
                    BseListingPrice = listing?.BSE_listing_price,
                    BsePreviousClose = bsePrevious,
                    BsePriceChange = bseChange,
                    BsePriceChangePercentage = bseChangePct,
                    BseDayHigh = bseData?.DayHigh ?? Math.Max(bseCurrent, bsePrevious),
                    BseDayLow = bseData?.DayLow ?? Math.Min(bseCurrent, bsePrevious),
                    BseVolume = bseData?.Volume ?? 32000,

                    BestBidPrice = Math.Max(0.05m, primaryCurrent - 0.05m),
                    BestAskPrice = primaryCurrent + 0.05m,

                    IsAvailable = true,
                    IsMarketOpen = marketOpen,
                    CanTrade = marketOpen && primaryCurrent > 0,
                    IsListingToday = ipo.Listing_date?.Date == now.Date
                };
            }).ToList();
        }

        private static DateTime GetIndiaStandardTime()
        {
            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"));
            }
            catch
            {
                try
                {
                    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
                }
                catch
                {
                    return DateTime.UtcNow.AddHours(5).AddMinutes(30);
                }
            }
        }
    }
}
