using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Services
{
    public class InvestorReportService : IInvestorReportService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStockMarketService _stockMarketService;

        public InvestorReportService(ApplicationDbContext context, IStockMarketService stockMarketService)
        {
            _context = context;
            _stockMarketService = stockMarketService;
        }

        public async Task<byte[]> GeneratePortfolioStatementPdfAsync(int userId, CancellationToken cancellationToken = default)
        {
            User? user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == userId, cancellationToken);
            if (user == null) throw new InvalidOperationException("Investor not found.");

            List<Investment> investments = await _context.Investments.AsNoTracking()
                .Where(i => i.User_id == userId)
                .OrderByDescending(i => i.Buy_date)
                .ToListAsync(cancellationToken);

            int[] invIds = investments.Select(i => i.Investment_id).ToArray();
            List<InvestmentTransaction> transactions = await _context.InvestmentTransactions.AsNoTracking()
                .Where(t => invIds.Contains(t.Investment_id))
                .ToListAsync(cancellationToken);

            int[] ipoIds = investments.Select(i => i.IPO_id).Distinct().ToArray();
            Dictionary<int, IPO> ipos = await _context.IPOs.AsNoTracking()
                .Where(ipo => ipoIds.Contains(ipo.IPO_id))
                .ToDictionaryAsync(ipo => ipo.IPO_id, cancellationToken);

            Dictionary<int, string> companies = await _context.Companies.AsNoTracking()
                .Where(c => ipos.Values.Select(i => i.Company_id).Contains(c.Company_id))
                .ToDictionaryAsync(c => c.Company_id, c => c.Company_name, cancellationToken);

            Dictionary<int, IPOListing> listings = await _context.IPOListings.AsNoTracking()
                .Where(l => ipoIds.Contains(l.IPO_id))
                .ToDictionaryAsync(l => l.IPO_id, cancellationToken);

            MarketDashboardViewModel market = await _stockMarketService.GetDashboardAsync(cancellationToken);

            var holdings = new List<PortfolioHoldingViewModel>();
            decimal totalInvested = 0m;
            decimal totalCurrentVal = 0m;

            foreach (var inv in investments)
            {
                var invTxns = transactions.Where(t => t.Investment_id == inv.Investment_id).ToList();
                int soldQty = invTxns.Where(t => string.Equals(t.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Quantity);
                int activeQty = Math.Max(0, inv.Quantity - soldQty);
                if (activeQty <= 0 || !ipos.TryGetValue(inv.IPO_id, out var ipo)) continue;

                listings.TryGetValue(inv.IPO_id, out var listing);
                var marketRow = market.Listings.FirstOrDefault(r => r.IpoId == inv.IPO_id);

                decimal avgBuy = inv.Buy_price ?? 0m;
                decimal cmp = marketRow?.CurrentPrice ?? inv.Current_price ?? listing?.NSE_closing_price ?? listing?.NSE_listing_price ?? ipo.Price_max ?? avgBuy;
                decimal invested = activeQty * avgBuy;
                decimal curVal = activeQty * cmp;
                decimal pnl = curVal - invested;

                totalInvested += invested;
                totalCurrentVal += curVal;

                holdings.Add(new PortfolioHoldingViewModel
                {
                    InvestmentId = inv.Investment_id,
                    IpoId = ipo.IPO_id,
                    IpoName = ipo.IPO_name,
                    CompanyName = companies.GetValueOrDefault(ipo.Company_id, "Company"),
                    StockSymbol = marketRow?.StockSymbol ?? listing?.StockSymbol ?? "STOCK",
                    Exchange = "NSE",
                    Quantity = activeQty,
                    AverageBuyPrice = avgBuy,
                    CurrentPrice = cmp,
                    InvestedAmount = invested,
                    CurrentValue = curVal,
                    UnrealizedProfitLoss = pnl
                });
            }

            decimal totalUnrealizedPnl = totalCurrentVal - totalInvested;
            decimal pnlPct = totalInvested > 0m ? (totalUnrealizedPnl / totalInvested) * 100m : 0m;
            decimal cashBalance = user.SimulatedCashBalance;
            decimal totalPortfolio = totalCurrentVal + cashBalance;

            var builder = new PdfReportBuilder("CONSOLIDATED PORTFOLIO STATEMENT", "Mark-to-Market Securities Holding & Asset Valuation Statement");
            builder.SetInvestor(user);

            builder.AddMetricCard("TOTAL PORTFOLIO VALUE", $"INR {totalPortfolio:N2}", "Equity + Cash Balance", 0.08f, 0.39f, 0.75f);
            builder.AddMetricCard("INVESTED CAPITAL", $"INR {totalInvested:N2}", $"{holdings.Count} active position(s)", 0.28f, 0.32f, 0.38f);
            builder.AddMetricCard("UNREALIZED P/L", $"{(totalUnrealizedPnl >= 0 ? "+" : "")}INR {totalUnrealizedPnl:N2}", $"{(pnlPct >= 0 ? "+" : "")}{pnlPct:N2}% Return", totalUnrealizedPnl >= 0 ? 0.08f : 0.85f, totalUnrealizedPnl >= 0 ? 0.65f : 0.15f, totalUnrealizedPnl >= 0 ? 0.35f : 0.15f);
            builder.AddMetricCard("SIMULATED CASH WALLET", $"INR {cashBalance:N2}", "Liquid Virtual Balance", 0.05f, 0.55f, 0.65f);

            var columns = new List<PdfTableColumn>
            {
                new("Security / IPO Name", 115f, PdfTextAlign.Left),
                new("Symbol", 45f, PdfTextAlign.Center),
                new("Exch", 30f, PdfTextAlign.Center),
                new("Qty", 35f, PdfTextAlign.Right),
                new("Avg Cost", 55f, PdfTextAlign.Right),
                new("CMP", 55f, PdfTextAlign.Right),
                new("Invested", 60f, PdfTextAlign.Right),
                new("Current Val", 60f, PdfTextAlign.Right),
                new("Unrealized P&L", 60f, PdfTextAlign.Right)
            };

            var rows = new List<List<string>>();
            foreach (var h in holdings)
            {
                rows.Add(new List<string>
                {
                    h.IpoName,
                    h.StockSymbol,
                    h.Exchange,
                    h.Quantity.ToString("N0"),
                    $"INR {h.AverageBuyPrice:N2}",
                    $"INR {h.CurrentPrice:N2}",
                    $"INR {h.InvestedAmount:N2}",
                    $"INR {h.CurrentValue:N2}",
                    $"{(h.UnrealizedProfitLoss >= 0 ? "+" : "")}INR {h.UnrealizedProfitLoss:N2}"
                });
            }

            var totals = new List<string>
            {
                "TOTAL",
                "",
                "",
                holdings.Sum(h => h.Quantity).ToString("N0"),
                "",
                "",
                $"INR {totalInvested:N2}",
                $"INR {totalCurrentVal:N2}",
                $"{(totalUnrealizedPnl >= 0 ? "+" : "")}INR {totalUnrealizedPnl:N2}"
            };

            builder.SetTable(columns, rows, totals);
            builder.SetNotes("Statutory & Compliance Advisory: All share quantities, mark-to-market valuations, and profit/loss calculations are generated from real-time secondary market simulated trades. Values are expressed in Indian Rupees (INR) and do not constitute physical depository holdings.");

            return builder.Build();
        }

        public async Task<byte[]> GenerateTransactionReportPdfAsync(int userId, CancellationToken cancellationToken = default)
        {
            User? user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == userId, cancellationToken);
            if (user == null) throw new InvalidOperationException("Investor not found.");

            var invIds = await _context.Investments.AsNoTracking()
                .Where(i => i.User_id == userId)
                .Select(i => i.Investment_id)
                .ToListAsync(cancellationToken);

            var transactions = await _context.InvestmentTransactions.AsNoTracking()
                .Where(t => invIds.Contains(t.Investment_id))
                .OrderByDescending(t => t.Transaction_date)
                .ToListAsync(cancellationToken);

            var investments = await _context.Investments.AsNoTracking()
                .Where(i => invIds.Contains(i.Investment_id))
                .ToDictionaryAsync(i => i.Investment_id, cancellationToken);

            var ipoIds = investments.Values.Select(i => i.IPO_id).Distinct().ToArray();
            var ipoNames = await _context.IPOs.AsNoTracking()
                .Where(i => ipoIds.Contains(i.IPO_id))
                .ToDictionaryAsync(i => i.IPO_id, i => i.IPO_name, cancellationToken);

            decimal totalBuyAmount = transactions.Where(t => string.Equals(t.Transaction_type, "BUY", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Total_amount ?? 0m);
            decimal totalSellAmount = transactions.Where(t => string.Equals(t.Transaction_type, "SELL", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Total_amount ?? 0m);
            decimal netFlow = totalSellAmount - totalBuyAmount;

            var builder = new PdfReportBuilder("SECURITIES TRANSACTION AUDIT REPORT", "Execution History, Buy/Sell Order Fills & Trade Audit Trail");
            builder.SetInvestor(user);

            builder.AddMetricCard("TOTAL TRADES", $"{transactions.Count} Orders", "Executed Orders", 0.08f, 0.39f, 0.75f);
            builder.AddMetricCard("TOTAL BUY TURNOVER", $"INR {totalBuyAmount:N2}", $"{transactions.Count(t => t.Transaction_type == "BUY")} Buy Fills", 0.15f, 0.55f, 0.25f);
            builder.AddMetricCard("TOTAL SELL TURNOVER", $"INR {totalSellAmount:N2}", $"{transactions.Count(t => t.Transaction_type == "SELL")} Sell Fills", 0.75f, 0.25f, 0.15f);
            builder.AddMetricCard("NET CAPITAL FLOW", $"{(netFlow >= 0 ? "+" : "")}INR {netFlow:N2}", netFlow >= 0 ? "Net Realized Inflow" : "Net Capital Outflow", 0.30f, 0.35f, 0.42f);

            var columns = new List<PdfTableColumn>
            {
                new("Txn ID", 45f, PdfTextAlign.Left),
                new("Date & Time", 95f, PdfTextAlign.Left),
                new("Security / IPO Name", 130f, PdfTextAlign.Left),
                new("Exch", 35f, PdfTextAlign.Center),
                new("Type", 40f, PdfTextAlign.Center),
                new("Qty", 40f, PdfTextAlign.Right),
                new("Exec Price", 60f, PdfTextAlign.Right),
                new("Total Amount", 70f, PdfTextAlign.Right)
            };

            var rows = new List<List<string>>();
            foreach (var t in transactions)
            {
                investments.TryGetValue(t.Investment_id, out var inv);
                string stockName = inv != null && ipoNames.TryGetValue(inv.IPO_id, out var name) ? name : "Security";
                string type = t.Transaction_type?.ToUpperInvariant() ?? "TRADE";

                rows.Add(new List<string>
                {
                    $"#TX{t.Transaction_id}",
                    t.Transaction_date.ToString("dd MMM yyyy, hh:mm tt"),
                    stockName,
                    "NSE",
                    type,
                    t.Quantity.ToString("N0"),
                    $"INR {(t.Price ?? 0m):N2}",
                    $"INR {(t.Total_amount ?? 0m):N2}"
                });
            }

            var totals = new List<string>
            {
                "TOTALS",
                "",
                "",
                "",
                "",
                transactions.Sum(t => t.Quantity).ToString("N0"),
                "",
                $"INR {(totalBuyAmount + totalSellAmount):N2}"
            };

            builder.SetTable(columns, rows, totals);
            builder.SetNotes("Trade Execution Audit Note: Every transaction listed above reflects verified orders placed during active trading sessions or administrator simulation. Timestamps are recorded in Indian Standard Time (IST).");

            return builder.Build();
        }

        public async Task<byte[]> GenerateIpoApplicationReportPdfAsync(int userId, CancellationToken cancellationToken = default)
        {
            User? user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == userId, cancellationToken);
            if (user == null) throw new InvalidOperationException("Investor not found.");

            var applications = await _context.IPOApplications.AsNoTracking()
                .Where(a => a.User_id == userId)
                .OrderByDescending(a => a.Application_date)
                .ToListAsync(cancellationToken);

            var ipoIds = applications.Select(a => a.IPO_id).Distinct().ToArray();
            var ipos = await _context.IPOs.AsNoTracking()
                .Where(i => ipoIds.Contains(i.IPO_id))
                .ToDictionaryAsync(i => i.IPO_id, cancellationToken);

            decimal totalBlocked = applications.Sum(a => a.Application_amount ?? 0m);
            int allottedCount = applications.Count(a => string.Equals(a.Status, "Allotted", StringComparison.OrdinalIgnoreCase));
            int pendingCount = applications.Count(a => string.Equals(a.Status, "Submitted", StringComparison.OrdinalIgnoreCase) || string.Equals(a.Status, "Pending", StringComparison.OrdinalIgnoreCase));

            var builder = new PdfReportBuilder("IPO APPLICATION DISCLOSURE STATEMENT", "Official Bid Submission, ASBA Mandate & Payment Reference Statement");
            builder.SetInvestor(user);

            builder.AddMetricCard("APPLICATIONS", $"{applications.Count} IPOs", "Total Applied Issues", 0.08f, 0.39f, 0.75f);
            builder.AddMetricCard("TOTAL BID CAPITAL", $"INR {totalBlocked:N2}", "Cumulative Blocked Amount", 0.28f, 0.32f, 0.38f);
            builder.AddMetricCard("ALLOTTED ISSUES", $"{allottedCount} Success", $"{allottedCount} Allotted IPO(s)", 0.15f, 0.55f, 0.25f);
            builder.AddMetricCard("PENDING REVIEW", $"{pendingCount} Active", $"{pendingCount} Awaiting Allotment", 0.85f, 0.55f, 0.15f);

            var columns = new List<PdfTableColumn>
            {
                new("App No / Ref", 65f, PdfTextAlign.Left),
                new("Applied Date", 70f, PdfTextAlign.Left),
                new("IPO Offering Name", 135f, PdfTextAlign.Left),
                new("Lots", 30f, PdfTextAlign.Right),
                new("Shares", 35f, PdfTextAlign.Right),
                new("Bid Price", 55f, PdfTextAlign.Right),
                new("Blocked Amount", 65f, PdfTextAlign.Right),
                new("Status", 60f, PdfTextAlign.Center)
            };

            var rows = new List<List<string>>();
            foreach (var app in applications)
            {
                ipos.TryGetValue(app.IPO_id, out var ipo);
                string ipoName = ipo?.IPO_name ?? "IPO Issue";
                string appNum = !string.IsNullOrWhiteSpace(app.Application_number) ? app.Application_number : $"#APP-{app.Application_id}";

                rows.Add(new List<string>
                {
                    appNum,
                    app.Application_date.ToString("dd MMM yyyy"),
                    ipoName,
                    app.Applied_lots.ToString("N0"),
                    app.Applied_shares.ToString("N0"),
                    $"INR {(app.Bid_price ?? 0m):N2}",
                    $"INR {(app.Application_amount ?? 0m):N2}",
                    app.Status ?? "Submitted"
                });
            }

            var totals = new List<string>
            {
                "TOTALS",
                "",
                "",
                applications.Sum(a => a.Applied_lots).ToString("N0"),
                applications.Sum(a => a.Applied_shares).ToString("N0"),
                "",
                $"INR {totalBlocked:N2}",
                ""
            };

            builder.SetTable(columns, rows, totals);
            builder.SetNotes("Application Disclosure: Application mandates are processed under simulated ASBA (Application Supported by Blocked Amount) procedures. Payment references and UPI/Bank authorization records are archived for investor audit verification.");

            return builder.Build();
        }

        public async Task<byte[]> GenerateAllotmentReportPdfAsync(int userId, CancellationToken cancellationToken = default)
        {
            User? user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.User_id == userId, cancellationToken);
            if (user == null) throw new InvalidOperationException("Investor not found.");

            var applications = await _context.IPOApplications.AsNoTracking()
                .Where(a => a.User_id == userId)
                .OrderByDescending(a => a.Application_date)
                .ToListAsync(cancellationToken);

            var allotments = await _context.IPOAllotments.AsNoTracking()
                .Where(a => a.User_id == userId)
                .ToDictionaryAsync(a => a.Application_id, cancellationToken);

            var ipoIds = applications.Select(a => a.IPO_id).Distinct().ToArray();
            var ipos = await _context.IPOs.AsNoTracking()
                .Where(i => ipoIds.Contains(i.IPO_id))
                .ToDictionaryAsync(i => i.IPO_id, cancellationToken);

            int totalAllottedShares = 0;
            decimal totalAllottedAmount = 0m;
            decimal totalRefundAmount = 0m;
            int successfulAllotments = 0;

            var rows = new List<List<string>>();

            foreach (var app in applications)
            {
                ipos.TryGetValue(app.IPO_id, out var ipo);
                string ipoName = ipo?.IPO_name ?? "IPO Issue";
                allotments.TryGetValue(app.Application_id, out var allotment);

                int allottedShares = allotment?.Allotted_shares ?? 0;
                decimal allotPrice = allotment != null && allotment.Allotment_price > 0 ? allotment.Allotment_price : (app.Bid_price ?? 0m);
                decimal allottedValue = allottedShares * allotPrice;
                decimal appAmount = app.Application_amount ?? (app.Applied_shares * (app.Bid_price ?? 0m));
                decimal refund = Math.Max(0m, appAmount - allottedValue);

                if (allottedShares > 0)
                {
                    successfulAllotments++;
                }

                totalAllottedShares += allottedShares;
                totalAllottedAmount += allottedValue;
                totalRefundAmount += refund;

                string statusText = allotment?.Status ?? (string.Equals(app.Status, "Submitted", StringComparison.OrdinalIgnoreCase) ? "Awaiting Allotment" : app.Status ?? "Pending");
                string allotDate = allotment != null ? allotment.Allotment_date.ToString("dd MMM yyyy") : (ipo?.Allotment_date.HasValue == true ? ipo.Allotment_date.Value.ToString("dd MMM yyyy") : "TBA");

                rows.Add(new List<string>
                {
                    !string.IsNullOrWhiteSpace(app.Application_number) ? app.Application_number : $"#APP-{app.Application_id}",
                    allotDate,
                    ipoName,
                    app.Applied_shares.ToString("N0"),
                    allottedShares.ToString("N0"),
                    $"INR {allotPrice:N2}",
                    $"INR {allottedValue:N2}",
                    $"INR {refund:N2}",
                    statusText
                });
            }

            var builder = new PdfReportBuilder("IPO ALLOTMENT & REFUND DISCLOSURE REPORT", "Official Allotment Status, Share Credit Summary & ASBA Fund Release Audit");
            builder.SetInvestor(user);

            builder.AddMetricCard("TOTAL APPLIED", $"{applications.Count} Issues", "Total IPOs Applied", 0.08f, 0.39f, 0.75f);
            builder.AddMetricCard("ALLOTTED ISSUES", $"{successfulAllotments} Allotted", $"{totalAllottedShares:N0} Shares Credited", 0.15f, 0.55f, 0.25f);
            builder.AddMetricCard("TOTAL ALLOTTED VALUE", $"INR {totalAllottedAmount:N2}", "Debited / Invested Value", 0.28f, 0.32f, 0.38f);
            builder.AddMetricCard("UNBLOCKED / REFUNDED", $"INR {totalRefundAmount:N2}", "Released back to Investor", 0.05f, 0.55f, 0.65f);

            var columns = new List<PdfTableColumn>
            {
                new("App No / Ref", 55f, PdfTextAlign.Left),
                new("Allot Date", 65f, PdfTextAlign.Left),
                new("IPO Offering Name", 115f, PdfTextAlign.Left),
                new("Applied", 35f, PdfTextAlign.Right),
                new("Allotted", 35f, PdfTextAlign.Right),
                new("Price", 45f, PdfTextAlign.Right),
                new("Allotted Val", 55f, PdfTextAlign.Right),
                new("Refund/Release", 55f, PdfTextAlign.Right),
                new("Result", 55f, PdfTextAlign.Center)
            };

            var totals = new List<string>
            {
                "TOTALS",
                "",
                "",
                applications.Sum(a => a.Applied_shares).ToString("N0"),
                totalAllottedShares.ToString("N0"),
                "",
                $"INR {totalAllottedAmount:N2}",
                $"INR {totalRefundAmount:N2}",
                ""
            };

            builder.SetTable(columns, rows, totals);
            builder.SetNotes("Basis of Allotment Note: Allotments are calculated using SEBI category-wise allotment algorithms (proportionate lottery for Retail, proportionate allocation for HNI/QIB). Unallotted funds are unblocked in investor simulated balances on allotment completion.");

            return builder.Build();
        }

        public async Task<byte[]> GenerateAllotmentAdvicePdfAsync(int applicationId, CancellationToken cancellationToken = default)
        {
            IPOApplication? app = await _context.IPOApplications.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Application_id == applicationId, cancellationToken);
            if (app == null) throw new InvalidOperationException("IPO Application not found.");

            User? user = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.User_id == app.User_id, cancellationToken);
            if (user == null) throw new InvalidOperationException("Investor not found.");

            IPO? ipo = await _context.IPOs.AsNoTracking()
                .FirstOrDefaultAsync(i => i.IPO_id == app.IPO_id, cancellationToken);
            if (ipo == null) throw new InvalidOperationException("IPO issue not found.");

            IPOAllotment? allotment = await _context.IPOAllotments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Application_id == applicationId, cancellationToken);

            PaymentRecord? payment = await _context.PaymentRecords.AsNoTracking()
                .FirstOrDefaultAsync(p => p.RazorpayOrderId == app.Razorpay_order_id, cancellationToken);

            int appliedShares = app.Applied_shares;
            int appliedLots = app.Applied_lots;
            int allottedShares = allotment?.Allotted_shares ?? 0;
            int allottedLots = allotment?.Allotted_lots ?? 0;
            decimal price = allotment != null && allotment.Allotment_price > 0 ? allotment.Allotment_price : (app.Bid_price ?? 0m);
            decimal blockedAmount = app.Application_amount ?? (appliedShares * (app.Bid_price ?? 0m));
            decimal debitedAmount = allottedShares * price;
            decimal unblockedAmount = Math.Max(0m, blockedAmount - debitedAmount);
            string resultStatus = allotment?.Status ?? (string.Equals(app.Status, "Submitted", StringComparison.OrdinalIgnoreCase) ? "Awaiting Allotment" : app.Status ?? "Pending");

            string adviceNo = allotment?.Advice_number ?? $"CAN-{DateTime.UtcNow:yyyyMMdd}-{app.Application_id:D5}";

            var builder = new PdfReportBuilder("CONFIRMATION OF ALLOCATION NOTE (CAN)", "Official Allotment Advice & Mandate Settlement Statement");
            builder.SetInvestor(user);

            float statusR = allottedShares > 0 ? (allottedShares == appliedShares ? 0.15f : 0.08f) : 0.85f;
            float statusG = allottedShares > 0 ? (allottedShares == appliedShares ? 0.55f : 0.39f) : 0.35f;
            float statusB = allottedShares > 0 ? (allottedShares == appliedShares ? 0.25f : 0.75f) : 0.15f;

            builder.AddMetricCard("RESULT STATUS", resultStatus.ToUpperInvariant(), $"CAN Ref: {adviceNo}", statusR, statusG, statusB);
            builder.AddMetricCard("ALLOTTED SHARES", $"{allottedShares:N0} Shares", $"{allottedLots} Lot(s) Allotted", 0.15f, 0.55f, 0.25f);
            builder.AddMetricCard("DEBITED AMOUNT", $"INR {debitedAmount:N2}", $"Cut-off Price: INR {price:N2}", 0.28f, 0.32f, 0.38f);
            builder.AddMetricCard("UNBLOCKED AMOUNT", $"INR {unblockedAmount:N2}", $"Refund Status: {payment?.RefundStatus ?? "Processed"}", 0.08f, 0.39f, 0.75f);

            var columns = new List<PdfTableColumn>
            {
                new("Allocation Parameter", 240f, PdfTextAlign.Left),
                new("Particulars / Value", 260f, PdfTextAlign.Left)
            };

            var rows = new List<List<string>>
            {
                new() { "Advice (CAN) Reference Number", adviceNo },
                new() { "IPO Security Name", ipo.IPO_name },
                new() { "Application Number", app.Application_number ?? $"#APP-{app.Application_id}" },
                new() { "Application Date", app.Application_date.ToString("dd MMM yyyy, hh:mm tt") },
                new() { "Allotment Date", allotment?.Allotment_date.ToString("dd MMM yyyy") ?? ipo.Allotment_date?.ToString("dd MMM yyyy") ?? "Pending" },
                new() { "Demat Account (DP / Client ID)", app.Demat_account_number ?? user.DematAccountNumber ?? "N/A" },
                new() { "UPI ID (VPA)", app.Upi_id ?? user.UpiId ?? "N/A" },
                new() { "Investor Bidding Category", blockedAmount <= 200000m ? "Retail Individual Investor (RII)" : (blockedAmount <= 1000000m ? "Non-Institutional Investor (NII/HNI)" : "Qualified Institutional Buyer (QIB)") },
                new() { "Bid Lots / Shares Applied", $"{appliedLots} lot(s) / {appliedShares:N0} equity share(s)" },
                new() { "Lots / Shares Allotted", $"{allottedLots} lot(s) / {allottedShares:N0} equity share(s)" },
                new() { "Issue / Cut-off Price per Share", $"INR {price:N2}" },
                new() { "Application Amount Blocked (ASBA)", $"INR {blockedAmount:N2}" },
                new() { "Total Amount Adjusted / Debited", $"INR {debitedAmount:N2}" },
                new() { "Total Amount Unblocked / Refunded", $"INR {unblockedAmount:N2}" },
                new() { "Refund / Mandate Release Ref", payment?.RefundReferenceId ?? "N/A" },
                new() { "Expected Listing Date on NSE/BSE", ipo.Listing_date?.ToString("dd MMM yyyy") ?? "To be notified" },
                new() { "Registrar & Share Transfer Agent", "Link Intime / KFin Technologies (Simulated RTA)" }
            };

            builder.SetTable(columns, rows);
            builder.SetNotes("Official SEBI Allotment Note: This document serves as formal Confirmation of Allocation Note (CAN). Allotted equity shares are credited directly to the investor's designated CDSL/NSDL Demat account prior to trading commencement. Unallotted funds are unblocked in investor linked bank account under UPI ASBA mandate guidelines.");

            return builder.Build();
        }
    }

    #region Pure C# PDF Report Builder Engine

    public enum PdfTextAlign
    {
        Left,
        Center,
        Right
    }

    public sealed class PdfTableColumn
    {
        public string Header { get; set; }
        public float Width { get; set; }
        public PdfTextAlign Align { get; set; }

        public PdfTableColumn(string header, float width, PdfTextAlign align)
        {
            Header = header;
            Width = width;
            Align = align;
        }
    }

    public sealed class PdfMetricCard
    {
        public string Title { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
    }

    public sealed class PdfReportBuilder
    {
        private const float PageWidth = 595.28f;
        private const float PageHeight = 841.89f;
        private const float MarginLeft = 40f;
        private const float UsableWidth = 515.28f;
        private const float BottomMargin = 55f;

        private readonly string _reportTitle;
        private readonly string _reportSubtitle;
        private User? _investor;
        private readonly List<PdfMetricCard> _metricCards = new();
        private List<PdfTableColumn> _columns = new();
        private List<List<string>> _rows = new();
        private List<string>? _totals;
        private string? _notes;

        public PdfReportBuilder(string reportTitle, string reportSubtitle)
        {
            _reportTitle = reportTitle;
            _reportSubtitle = reportSubtitle;
        }

        public void SetInvestor(User user)
        {
            _investor = user;
        }

        public void AddMetricCard(string title, string value, string subtitle, float r, float g, float b)
        {
            _metricCards.Add(new PdfMetricCard
            {
                Title = title,
                Value = value,
                Subtitle = subtitle,
                R = r,
                G = g,
                B = b
            });
        }

        public void SetTable(List<PdfTableColumn> columns, List<List<string>> rows, List<string>? totals = null)
        {
            _columns = columns;
            _rows = rows;
            _totals = totals;
        }

        public void SetNotes(string notes)
        {
            _notes = notes;
        }

        public byte[] Build()
        {
            var pages = new List<StringBuilder>();
            var currentPage = new StringBuilder();

            float currentY = PageHeight - 40f;

            // PAGE 1: Header Banner
            DrawHeaderBanner(currentPage, isFirstPage: true);
            currentY = PageHeight - 75f;

            // Investor Metadata Box
            currentY = DrawInvestorProfileBox(currentPage, currentY);

            // Metric Summary Cards
            if (_metricCards.Count > 0)
            {
                currentY = DrawMetricCards(currentPage, currentY);
            }

            // Table Drawing
            if (_columns.Count > 0)
            {
                const float headerHeight = 22f;
                const float rowHeight = 18f;

                // Draw Table Header on page 1
                currentY -= 10f;
                DrawTableHeader(currentPage, currentY, headerHeight);
                currentY -= headerHeight;

                for (int i = 0; i < _rows.Count; i++)
                {
                    // Check if we need a new page
                    if (currentY - rowHeight < BottomMargin)
                    {
                        // Finalize current page
                        pages.Add(currentPage);
                        currentPage = new StringBuilder();

                        // Start next page with continuation banner
                        DrawHeaderBanner(currentPage, isFirstPage: false);
                        currentY = PageHeight - 45f;

                        // Re-draw table header
                        DrawTableHeader(currentPage, currentY, headerHeight);
                        currentY -= headerHeight;
                    }

                    bool isAlt = (i % 2 == 1);
                    DrawTableRow(currentPage, currentY, rowHeight, _rows[i], isAlt);
                    currentY -= rowHeight;
                }

                // Totals Row
                if (_totals != null && _totals.Count > 0)
                {
                    if (currentY - rowHeight < BottomMargin)
                    {
                        pages.Add(currentPage);
                        currentPage = new StringBuilder();
                        DrawHeaderBanner(currentPage, isFirstPage: false);
                        currentY = PageHeight - 45f;
                        DrawTableHeader(currentPage, currentY, headerHeight);
                        currentY -= headerHeight;
                    }

                    DrawTableTotals(currentPage, currentY, rowHeight, _totals);
                    currentY -= rowHeight;
                }
            }

            // Notes / Compliance Box
            if (!string.IsNullOrWhiteSpace(_notes))
            {
                if (currentY - 45f < BottomMargin)
                {
                    pages.Add(currentPage);
                    currentPage = new StringBuilder();
                    DrawHeaderBanner(currentPage, isFirstPage: false);
                    currentY = PageHeight - 45f;
                }

                currentY -= 15f;
                DrawNotesBox(currentPage, currentY, _notes);
            }

            pages.Add(currentPage);

            // Assemble complete PDF binary document
            return CompilePdfDocument(pages);
        }

        private void DrawHeaderBanner(StringBuilder sb, bool isFirstPage)
        {
            if (isFirstPage)
            {
                // Dark Slate Navy Banner: 0 782 595.28 60
                sb.AppendLine("0.06 0.09 0.16 rg");
                sb.AppendLine("0 782 595.28 60 re f");

                // Accent Teal Line: 0 780 595.28 2.5
                sb.AppendLine("0.04 0.71 0.83 rg");
                sb.AppendLine("0 780 595.28 2.5 re f");

                // Branding Title
                sb.AppendLine("1 1 1 rg");
                sb.AppendLine("BT /F2 13 Tf 1 0 0 1 40 818 Tm (IPO INVESTMENT MANAGEMENT SYSTEM) Tj ET");
                sb.AppendLine("0.85 0.90 0.96 rg");
                sb.AppendLine($"BT /F2 10 Tf 1 0 0 1 40 803 Tm ({EscapePdf(_reportTitle.ToUpperInvariant())}) Tj ET");
                sb.AppendLine("0.65 0.75 0.88 rg");
                sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 40 790 Tm ({EscapePdf(_reportSubtitle)}) Tj ET");

                // Right-aligned status mark
                sb.AppendLine("1 1 1 rg");
                sb.AppendLine($"BT /F2 8.5 Tf 1 0 0 1 430 815 Tm (OFFICIAL INVESTOR DISCLOSURE) Tj ET");
                sb.AppendLine("0.70 0.80 0.90 rg");
                sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 430 802 Tm (SEBI & Exchange Simulation) Tj ET");
                sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 430 790 Tm (Ref: INV-{_investor?.User_id:D5}) Tj ET");
            }
            else
            {
                // Compact Continuation Header: 0 815 595.28 27
                sb.AppendLine("0.06 0.09 0.16 rg");
                sb.AppendLine("0 815 595.28 27 re f");

                sb.AppendLine("0.04 0.71 0.83 rg");
                sb.AppendLine("0 813 595.28 2 re f");

                sb.AppendLine("1 1 1 rg");
                sb.AppendLine($"BT /F2 9.5 Tf 1 0 0 1 40 824 Tm (IPO INVESTMENT MANAGEMENT - {EscapePdf(_reportTitle)} \\(Continued\\)) Tj ET");
                sb.AppendLine("0.70 0.80 0.90 rg");
                sb.AppendLine($"BT /F1 8 Tf 1 0 0 1 450 824 Tm (Investor: {EscapePdf(_investor?.First_name ?? "Investor")}) Tj ET");
            }
        }

        private float DrawInvestorProfileBox(StringBuilder sb, float topY)
        {
            float boxH = 50f;
            float boxY = topY - boxH;

            // Box Background & Border
            sb.AppendLine("0.97 0.98 0.99 rg");
            sb.AppendLine($"40 {boxY:F2} 515.28 {boxH:F2} re f");
            sb.AppendLine("0.85 0.88 0.92 RG 0.5 w");
            sb.AppendLine($"40 {boxY:F2} 515.28 {boxH:F2} re S");

            string investorName = $"{_investor?.First_name} {_investor?.Last_name}".Trim();
            if (string.IsNullOrWhiteSpace(investorName)) investorName = "Simulated Investor";
            string email = _investor?.Email ?? "N/A";
            string phone = !string.IsNullOrWhiteSpace(_investor?.Phone) ? _investor.Phone : "N/A";
            string demat = !string.IsNullOrWhiteSpace(_investor?.DematAccountNumber) ? _investor.DematAccountNumber : $"IN300123-10{_investor?.User_id:D4}89";
            string pan = $"ABCDE{(_investor?.User_id ?? 1):D4}F";
            string nowFormatted = IpoClock.Shared.NowIst.ToString("dd MMM yyyy, hh:mm tt IST", CultureInfo.InvariantCulture);

            // Left Column
            sb.AppendLine("0.12 0.15 0.20 rg");
            sb.AppendLine($"BT /F2 8.5 Tf 1 0 0 1 50 {boxY + 36:F2} Tm (Investor: {EscapePdf(investorName)}) Tj ET");
            sb.AppendLine("0.35 0.40 0.48 rg");
            sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 50 {boxY + 23:F2} Tm (Client ID: INV-{_investor?.User_id:D5}   |   Email: {EscapePdf(email)}) Tj ET");
            sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 50 {boxY + 10:F2} Tm (Phone: {EscapePdf(phone)}   |   Status: Verified Investor Account) Tj ET");

            // Right Column
            sb.AppendLine("0.35 0.40 0.48 rg");
            sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 340 {boxY + 36:F2} Tm (Demat / DP A/C: {EscapePdf(demat)}) Tj ET");
            sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 340 {boxY + 23:F2} Tm (PAN Ref: {EscapePdf(pan)}   |   Currency: INR \\(Rs.\\)) Tj ET");
            sb.AppendLine($"BT /F1 7.5 Tf 1 0 0 1 340 {boxY + 10:F2} Tm (Statement As On: {EscapePdf(nowFormatted)}) Tj ET");

            return boxY - 10f;
        }

        private float DrawMetricCards(StringBuilder sb, float topY)
        {
            float cardH = 46f;
            float cardY = topY - cardH;
            int count = _metricCards.Count;
            float spacing = 8f;
            float cardW = (UsableWidth - ((count - 1) * spacing)) / count;

            for (int i = 0; i < count; i++)
            {
                var card = _metricCards[i];
                float cardX = MarginLeft + i * (cardW + spacing);

                // Card background
                sb.AppendLine("1 1 1 rg");
                sb.AppendLine($"{cardX:F2} {cardY:F2} {cardW:F2} {cardH:F2} re f");
                sb.AppendLine("0.85 0.88 0.92 RG 0.5 w");
                sb.AppendLine($"{cardX:F2} {cardY:F2} {cardW:F2} {cardH:F2} re S");

                // Accent Top Border Strip
                sb.AppendLine($"{card.R:F2} {card.G:F2} {card.B:F2} rg");
                sb.AppendLine($"{cardX:F2} {cardY + cardH - 2f:F2} {cardW:F2} 2 re f");

                // Card Title
                sb.AppendLine("0.45 0.50 0.58 rg");
                sb.AppendLine($"BT /F2 6.5 Tf 1 0 0 1 {cardX + 8:F2} {cardY + 31:F2} Tm ({EscapePdf(card.Title)}) Tj ET");

                // Card Value
                sb.AppendLine($"{card.R:F2} {card.G:F2} {card.B:F2} rg");
                sb.AppendLine($"BT /F2 9.5 Tf 1 0 0 1 {cardX + 8:F2} {cardY + 18:F2} Tm ({EscapePdf(card.Value)}) Tj ET");

                // Subtitle
                sb.AppendLine("0.55 0.60 0.68 rg");
                sb.AppendLine($"BT /F1 6.5 Tf 1 0 0 1 {cardX + 8:F2} {cardY + 7:F2} Tm ({EscapePdf(card.Subtitle)}) Tj ET");
            }

            return cardY - 5f;
        }

        private void DrawTableHeader(StringBuilder sb, float y, float height)
        {
            // Dark Header Background
            sb.AppendLine("0.09 0.12 0.18 rg");
            sb.AppendLine($"{MarginLeft} {y - height:F2} {UsableWidth} {height:F2} re f");

            float curX = MarginLeft;
            sb.AppendLine("1 1 1 rg");

            foreach (var col in _columns)
            {
                float textX = curX + 4f;
                float textY = y - height + 7f;

                if (col.Align == PdfTextAlign.Right)
                {
                    float textW = col.Header.Length * 4.6f;
                    textX = curX + col.Width - textW - 4f;
                }
                else if (col.Align == PdfTextAlign.Center)
                {
                    float textW = col.Header.Length * 4.6f;
                    textX = curX + (col.Width - textW) / 2f;
                }

                sb.AppendLine($"BT /F2 7.5 Tf 1 0 0 1 {textX:F2} {textY:F2} Tm ({EscapePdf(col.Header)}) Tj ET");
                curX += col.Width;
            }
        }

        private void DrawTableRow(StringBuilder sb, float y, float height, List<string> cells, bool isAlt)
        {
            // Row Background
            if (isAlt)
            {
                sb.AppendLine("0.97 0.98 0.99 rg");
                sb.AppendLine($"{MarginLeft} {y - height:F2} {UsableWidth} {height:F2} re f");
            }

            // Bottom Border line
            sb.AppendLine("0.90 0.92 0.95 RG 0.4 w");
            sb.AppendLine($"{MarginLeft} {y - height:F2} m {MarginLeft + UsableWidth} {y - height:F2} l S");

            float curX = MarginLeft;
            for (int i = 0; i < _columns.Count && i < cells.Count; i++)
            {
                var col = _columns[i];
                string val = cells[i];

                // Auto-truncate if exceeds width
                int maxChars = Math.Max(4, (int)(col.Width / 4.5f));
                if (val.Length > maxChars)
                {
                    val = val.Substring(0, maxChars - 2) + "..";
                }

                float textX = curX + 4f;
                float textY = y - height + 5.5f;

                // Color overrides for BUY / SELL or Gain / Loss
                if (val.Equals("BUY", StringComparison.OrdinalIgnoreCase) || val.Contains("Allotted") || val.Contains("Holding"))
                {
                    sb.AppendLine("0.08 0.55 0.25 rg"); // Green
                }
                else if (val.Equals("SELL", StringComparison.OrdinalIgnoreCase) || val.Contains("Rejected") || val.Contains("Cancelled"))
                {
                    sb.AppendLine("0.80 0.15 0.15 rg"); // Red
                }
                else if (val.StartsWith("+INR") || val.StartsWith("+"))
                {
                    sb.AppendLine("0.08 0.55 0.25 rg");
                }
                else if (val.StartsWith("-INR") || val.StartsWith("-"))
                {
                    sb.AppendLine("0.80 0.15 0.15 rg");
                }
                else
                {
                    sb.AppendLine("0.15 0.18 0.22 rg"); // Normal text
                }

                if (col.Align == PdfTextAlign.Right)
                {
                    float textW = val.Length * 4.4f;
                    textX = curX + col.Width - textW - 4f;
                }
                else if (col.Align == PdfTextAlign.Center)
                {
                    float textW = val.Length * 4.4f;
                    textX = curX + (col.Width - textW) / 2f;
                }

                sb.AppendLine($"BT /F1 7.2 Tf 1 0 0 1 {textX:F2} {textY:F2} Tm ({EscapePdf(val)}) Tj ET");
                curX += col.Width;
            }
        }

        private void DrawTableTotals(StringBuilder sb, float y, float height, List<string> totals)
        {
            // Totals background
            sb.AppendLine("0.93 0.95 0.97 rg");
            sb.AppendLine($"{MarginLeft} {y - height:F2} {UsableWidth} {height:F2} re f");

            // Top Border line
            sb.AppendLine("0.60 0.65 0.72 RG 0.8 w");
            sb.AppendLine($"{MarginLeft} {y:F2} m {MarginLeft + UsableWidth} {y:F2} l S");

            float curX = MarginLeft;
            sb.AppendLine("0.10 0.12 0.16 rg");

            for (int i = 0; i < _columns.Count && i < totals.Count; i++)
            {
                var col = _columns[i];
                string val = totals[i];

                if (!string.IsNullOrEmpty(val))
                {
                    float textX = curX + 4f;
                    float textY = y - height + 6f;

                    if (col.Align == PdfTextAlign.Right)
                    {
                        float textW = val.Length * 4.8f;
                        textX = curX + col.Width - textW - 4f;
                    }
                    else if (col.Align == PdfTextAlign.Center)
                    {
                        float textW = val.Length * 4.8f;
                        textX = curX + (col.Width - textW) / 2f;
                    }

                    sb.AppendLine($"BT /F2 7.5 Tf 1 0 0 1 {textX:F2} {textY:F2} Tm ({EscapePdf(val)}) Tj ET");
                }

                curX += col.Width;
            }
        }

        private void DrawNotesBox(StringBuilder sb, float topY, string notes)
        {
            float boxH = 34f;
            float boxY = topY - boxH;

            sb.AppendLine("0.98 0.98 0.99 rg");
            sb.AppendLine($"{MarginLeft} {boxY:F2} {UsableWidth} {boxH:F2} re f");
            sb.AppendLine("0.85 0.88 0.92 RG 0.5 w");
            sb.AppendLine($"{MarginLeft} {boxY:F2} {UsableWidth} {boxH:F2} re S");

            sb.AppendLine("0.25 0.30 0.38 rg");
            sb.AppendLine($"BT /F2 6.5 Tf 1 0 0 1 {MarginLeft + 8} {boxY + 22:F2} Tm (REGULATORY & PORTFOLIO DISCLOSURE:) Tj ET");

            sb.AppendLine("0.45 0.50 0.58 rg");
            // Wrap text into two lines if needed
            if (notes.Length > 115)
            {
                string line1 = notes.Substring(0, 115);
                string line2 = notes.Substring(115);
                sb.AppendLine($"BT /F1 6.5 Tf 1 0 0 1 {MarginLeft + 8} {boxY + 12:F2} Tm ({EscapePdf(line1)}) Tj ET");
                sb.AppendLine($"BT /F1 6.5 Tf 1 0 0 1 {MarginLeft + 8} {boxY + 3:F2} Tm ({EscapePdf(line2)}) Tj ET");
            }
            else
            {
                sb.AppendLine($"BT /F1 6.5 Tf 1 0 0 1 {MarginLeft + 8} {boxY + 10:F2} Tm ({EscapePdf(notes)}) Tj ET");
            }
        }

        private byte[] CompilePdfDocument(List<StringBuilder> pageStreams)
        {
            int pageCount = pageStreams.Count;
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.Latin1);

            writer.Write("%PDF-1.4\n%\xe2\xe3\xcf\xd3\n");
            writer.Flush();

            var offsets = new Dictionary<int, long>();

            void WriteObj(int num, string body)
            {
                writer.Flush();
                offsets[num] = ms.Position;
                writer.Write($"{num} 0 obj\n{body}\nendobj\n");
                writer.Flush();
            }

            // 1: Catalog
            WriteObj(1, "<< /Type /Catalog /Pages 2 0 R >>");

            // 2: Pages
            var pageObjIds = new List<int>();
            for (int i = 0; i < pageCount; i++)
            {
                pageObjIds.Add(6 + i * 2);
            }
            string kids = string.Join(" ", pageObjIds.Select(id => $"{id} 0 R"));
            WriteObj(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

            // 3, 4, 5: Standard Base Fonts
            WriteObj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            WriteObj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            WriteObj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>");

            // Render each page
            string genTime = IpoClock.Shared.NowIst.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture);

            for (int i = 0; i < pageCount; i++)
            {
                int pageObjId = 6 + i * 2;
                int streamObjId = pageObjId + 1;

                var pageSb = pageStreams[i];

                // Append standardized footer
                pageSb.AppendLine("0.80 0.83 0.88 RG 0.5 w");
                pageSb.AppendLine($"40 40 m 555.28 40 l S");

                pageSb.AppendLine("0.45 0.50 0.58 rg");
                pageSb.AppendLine($"BT /F1 7 Tf 1 0 0 1 40 28 Tm (IPO Investment Management System | Confidential Investor Statement) Tj ET");
                pageSb.AppendLine($"BT /F1 7 Tf 1 0 0 1 460 28 Tm (Page {i + 1} of {pageCount}) Tj ET");
                pageSb.AppendLine("0.60 0.65 0.72 rg");
                pageSb.AppendLine($"BT /F3 6.2 Tf 1 0 0 1 40 18 Tm (System generated report at {EscapePdf(genTime)} IST. All virtual holdings and transactions are simulated.) Tj ET");

                byte[] streamBytes = Encoding.Latin1.GetBytes(pageSb.ToString());

                // Content Stream Object
                writer.Flush();
                offsets[streamObjId] = ms.Position;
                writer.Write($"{streamObjId} 0 obj\n<< /Length {streamBytes.Length} >>\nstream\n");
                writer.Flush();
                ms.Write(streamBytes, 0, streamBytes.Length);
                writer.Write("\nendstream\nendobj\n");
                writer.Flush();

                // Page Object
                WriteObj(pageObjId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:F2} {PageHeight:F2}] /Contents {streamObjId} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> >>");
            }

            // Cross-Reference Table
            writer.Flush();
            long startXref = ms.Position;
            int totalObjs = 6 + pageCount * 2;

            writer.Write($"xref\n0 {totalObjs}\n0000000000 65535 f \n");
            for (int i = 1; i < totalObjs; i++)
            {
                long pos = offsets.TryGetValue(i, out var p) ? p : 0;
                writer.Write($"{pos:D10} 00000 n \n");
            }

            // Trailer
            writer.Write($"trailer\n<< /Size {totalObjs} /Root 1 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
            writer.Flush();

            return ms.ToArray();
        }

        private static string EscapePdf(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new StringBuilder(input.Length + 10);
            foreach (char c in input)
            {
                switch (c)
                {
                    case '(': sb.Append("\\("); break;
                    case ')': sb.Append("\\)"); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r':
                    case '\n': sb.Append(' '); break;
                    case '₹': sb.Append("Rs. "); break;
                    case '–': sb.Append('-'); break;
                    case '—': sb.Append("--"); break;
                    case '•': sb.Append('*'); break;
                    case '‘':
                    case '’': sb.Append('\''); break;
                    case '“':
                    case '”': sb.Append('"'); break;
                    default:
                        if (c >= 32 && c <= 126)
                            sb.Append(c);
                        else if (c >= 128 && c <= 255)
                            sb.Append(c);
                        else
                            sb.Append('?');
                        break;
                }
            }
            return sb.ToString();
        }
    }

    #endregion
}
