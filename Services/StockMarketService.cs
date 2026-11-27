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
            bool marketOpen = isCalendarOpen;

            List<MarketDataViewModel> rows = await LoadRowsAsync(null, now, marketOpen, cancellationToken);

            bool isSim = marketDataProvider.IsSimulationMode;
            bool isConfigured = marketDataProvider.IsConfigured;
            string dataSource = isSim 
                ? "Simulation Engine (Development Mode)" 
                : (isConfigured ? $"{marketDataProvider.ProviderName} (NSE/BSE)" : "Exchange Database (Static/Offline)");

            bool isStale = !isSim && (!isConfigured || rows.Any(r => r.IsStale));
            string? warning = marketDataProvider.StatusMessage;

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
                IsSimulationActive = isSim,
                DataSource = dataSource,
                IsStale = isStale,
                WarningMessage = warning
            };
        }

        public async Task<MarketDataViewModel?> GetMarketDataAsync(int ipoId, CancellationToken cancellationToken = default)
        {
            DateTime now = GetIndiaStandardTime();
            bool isCalendarOpen = await calendar.IsMarketOpenAsync(now, cancellationToken);
            bool marketOpen = isCalendarOpen;
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

            DateTime oneYearAgo = now.AddDays(-365);
            List<StockPriceHistory> historicalTrades = await context.StockPriceHistories.AsNoTracking()
                .Where(h => ids.Contains(h.IPOId) && h.Timestamp >= oneYearAgo)
                .ToListAsync(cancellationToken);

            return ipos.Select(ipo =>
            {
                listings.TryGetValue(ipo.IPO_id, out IPOListing? listing);
                StockMarketData? nseData = marketDataRows.FirstOrDefault(r => r.IPOId == ipo.IPO_id && string.Equals(r.Exchange, "NSE", StringComparison.OrdinalIgnoreCase));
                StockMarketData? bseData = marketDataRows.FirstOrDefault(r => r.IPOId == ipo.IPO_id && string.Equals(r.Exchange, "BSE", StringComparison.OrdinalIgnoreCase));

                decimal? defaultBaseNullable = listing?.NSE_listing_price
                    ?? listing?.BSE_listing_price
                    ?? listing?.Issue_price
                    ?? ipo.Price_max
                    ?? ipo.Price_min;

                decimal defaultBase = defaultBaseNullable ?? 0m;

                decimal? rawNseCurrent = nseData?.CurrentPrice ?? listing?.NSE_closing_price ?? listing?.NSE_listing_price ?? defaultBaseNullable;
                decimal nseCurrent = rawNseCurrent ?? 0m;
                decimal nsePrevious = nseData?.PreviousClose ?? listing?.NSE_listing_price ?? listing?.Issue_price ?? defaultBase;
                decimal nseChange = nseData?.PriceChange ?? (nseCurrent > 0 && nsePrevious > 0 ? (nseCurrent - nsePrevious) : 0m);
                decimal nseChangePct = nseData?.PriceChangePercentage ?? (nsePrevious > 0 ? Math.Round(nseChange / nsePrevious * 100m, 2) : 0m);

                decimal? rawBseCurrent = bseData?.CurrentPrice ?? listing?.BSE_closing_price ?? listing?.BSE_listing_price ?? defaultBaseNullable;
                decimal bseCurrent = rawBseCurrent ?? 0m;
                decimal bsePrevious = bseData?.PreviousClose ?? listing?.BSE_listing_price ?? listing?.Issue_price ?? defaultBase;
                decimal bseChange = bseData?.PriceChange ?? (bseCurrent > 0 && bsePrevious > 0 ? (bseCurrent - bsePrevious) : 0m);
                decimal bseChangePct = bseData?.PriceChangePercentage ?? (bsePrevious > 0 ? Math.Round(bseChange / bsePrevious * 100m, 2) : 0m);

                decimal primaryCurrent = nseCurrent > 0 ? nseCurrent : bseCurrent;
                decimal primaryPrevious = nsePrevious > 0 ? nsePrevious : bsePrevious;
                decimal primaryChange = nseCurrent > 0 ? nseChange : bseChange;
                decimal primaryPercentage = nseCurrent > 0 ? nseChangePct : bseChangePct;

                // Calculate genuine 52-week High and Low from actual trade history
                var ipoTrades = historicalTrades.Where(h => h.IPOId == ipo.IPO_id).ToList();
                var historicalPriceList = ipoTrades.Select(h => h.Price)
                    .Concat(ipoTrades.Select(h => h.HighPrice))
                    .Concat(ipoTrades.Select(h => h.LowPrice))
                    .Where(p => p > 0)
                    .ToList();

                if (primaryCurrent > 0) historicalPriceList.Add(primaryCurrent);
                if (primaryPrevious > 0) historicalPriceList.Add(primaryPrevious);
                if (nseData?.DayHigh > 0) historicalPriceList.Add(nseData.DayHigh.Value);
                if (bseData?.DayHigh > 0) historicalPriceList.Add(bseData.DayHigh.Value);
                if (nseData?.DayLow > 0) historicalPriceList.Add(nseData.DayLow.Value);
                if (bseData?.DayLow > 0) historicalPriceList.Add(bseData.DayLow.Value);
                if (listing?.NSE_listing_price > 0) historicalPriceList.Add(listing.NSE_listing_price.Value);
                if (listing?.BSE_listing_price > 0) historicalPriceList.Add(listing.BSE_listing_price.Value);
                if (listing?.Issue_price > 0) historicalPriceList.Add(listing.Issue_price.Value);

                decimal? h52 = historicalPriceList.Any() ? historicalPriceList.Max() : (decimal?)null;
                decimal? l52 = historicalPriceList.Any() ? historicalPriceList.Min() : (decimal?)null;

                long? nseVol = nseData?.Volume;
                long? bseVol = bseData?.Volume;
                long? combinedVol = (nseVol.HasValue || bseVol.HasValue) ? ((nseVol ?? 0) + (bseVol ?? 0)) : (long?)null;

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

                    CurrentPrice = primaryCurrent > 0 ? primaryCurrent : (decimal?)null,
                    PreviousClose = primaryPrevious > 0 ? primaryPrevious : (decimal?)null,
                    PriceChange = primaryCurrent > 0 ? primaryChange : (decimal?)null,
                    PriceChangePercentage = primaryCurrent > 0 ? primaryPercentage : (decimal?)null,
                    DayHigh = nseData?.DayHigh ?? bseData?.DayHigh ?? (primaryCurrent > 0 ? primaryCurrent : (decimal?)null),
                    DayLow = nseData?.DayLow ?? bseData?.DayLow ?? (primaryCurrent > 0 ? primaryCurrent : (decimal?)null),
                    Volume = combinedVol,
                    LastUpdated = nseData?.LastUpdated ?? bseData?.LastUpdated ?? listing?.Listing_date ?? now,

                    // 52-Week Actual High and Low
                    High52w = h52,
                    Low52w = l52,

                    // Dedicated NSE
                    NsePrice = nseCurrent > 0 ? nseCurrent : (decimal?)null,
                    NseListingPrice = listing?.NSE_listing_price,
                    NsePreviousClose = nsePrevious > 0 ? nsePrevious : (decimal?)null,
                    NsePriceChange = nseCurrent > 0 ? nseChange : (decimal?)null,
                    NsePriceChangePercentage = nseCurrent > 0 ? nseChangePct : (decimal?)null,
                    NseDayHigh = nseData?.DayHigh ?? (nseCurrent > 0 ? nseCurrent : (decimal?)null),
                    NseDayLow = nseData?.DayLow ?? (nseCurrent > 0 ? nseCurrent : (decimal?)null),
                    NseVolume = nseVol,

                    // Dedicated BSE
                    BsePrice = bseCurrent > 0 ? bseCurrent : (decimal?)null,
                    BseListingPrice = listing?.BSE_listing_price,
                    BsePreviousClose = bsePrevious > 0 ? bsePrevious : (decimal?)null,
                    BsePriceChange = bseCurrent > 0 ? bseChange : (decimal?)null,
                    BsePriceChangePercentage = bseCurrent > 0 ? bseChangePct : (decimal?)null,
                    BseDayHigh = bseData?.DayHigh ?? (bseCurrent > 0 ? bseCurrent : (decimal?)null),
                    BseDayLow = bseData?.DayLow ?? (bseCurrent > 0 ? bseCurrent : (decimal?)null),
                    BseVolume = bseVol,

                    BestBidPrice = primaryCurrent > 0 ? Math.Max(0.05m, primaryCurrent - 0.05m) : (decimal?)null,
                    BestAskPrice = primaryCurrent > 0 ? primaryCurrent + 0.05m : (decimal?)null,

                    IsAvailable = primaryCurrent > 0,
                    IsMarketOpen = marketOpen,
                    CanTrade = marketOpen && primaryCurrent > 0,
                    IsListingToday = ipo.Listing_date?.Date == now.Date,
                    IsSimulationActive = marketDataProvider.IsSimulationMode,
                    DataSource = marketDataProvider.IsSimulationMode 
                        ? "Simulation Engine (Development Mode)" 
                        : (marketDataProvider.IsConfigured ? $"{marketDataProvider.ProviderName} (NSE/BSE)" : "Exchange Database (Static/Offline)"),
                    IsStale = !marketDataProvider.IsSimulationMode && (!marketDataProvider.IsConfigured || (now - (nseData?.LastUpdated ?? bseData?.LastUpdated ?? now)).TotalMinutes > 5),
                    StaleWarning = !marketDataProvider.IsSimulationMode && !marketDataProvider.IsConfigured ? marketDataProvider.StatusMessage : null
                };
            }).ToList();
        }

        private static DateTime GetIndiaStandardTime()
        {
            return IpoClock.Shared.NowIst;
        }
    }
}
