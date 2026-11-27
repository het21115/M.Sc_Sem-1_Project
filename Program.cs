using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Filters;
using IPOInvestmentManagement.Hubs;
using IPOInvestmentManagement.Models;
using IPOInvestmentManagement.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddScoped<AdminAuditFilter>();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.AddService<AdminAuditFilter>();
    options.Conventions.Add(new TestClockEnvironmentConvention(builder.Environment));
});
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.Name = "__Host-IpoSession";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
    });
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IEmailNotificationService, SmtpEmailNotificationService>();
builder.Services.AddScoped<IMarketTradingCalendar, MarketTradingCalendar>();
builder.Services.AddScoped<IStockMarketService, StockMarketService>();
builder.Services.AddScoped<IInvestorReportService, InvestorReportService>();
builder.Services.AddSingleton<IMarketDataProvider, RealTimeStockMarketDataProvider>();
builder.Services.AddSingleton<IIpoClock, IpoClock>();
builder.Services.AddScoped<IIpoStatusService, IpoStatusService>();
builder.Services.AddScoped<IPaymentReconciliationService, PaymentReconciliationService>();
builder.Services.AddScoped<IStockSipService, StockSipService>();
builder.Services.AddScoped<IWalletWithdrawalService, WalletWithdrawalService>();
builder.Services.AddHostedService<StockPriceBroadcastingService>();
builder.Services.AddHostedService<IpoStatusBackgroundService>();
builder.Services.AddHostedService<PaymentReconciliationBackgroundService>();
builder.Services.AddHttpClient<IRazorpayPaymentService, RazorpayPaymentService>(client =>
{
    client.BaseAddress = new Uri("https://api.razorpay.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")
    ));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.ExecuteSqlRawAsync("""
        IF OBJECT_ID(N'[SystemNotifications]', N'U') IS NULL
        BEGIN
            CREATE TABLE [SystemNotifications] (
                [Notification_id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SystemNotifications] PRIMARY KEY,
                [User_id] int NOT NULL,
                [Title] nvarchar(200) NOT NULL,
                [Message] nvarchar(max) NOT NULL,
                [Created_at] datetime2 NOT NULL,
                [Is_read] bit NOT NULL CONSTRAINT [DF_SystemNotifications_Is_read] DEFAULT (0)
            );
            CREATE INDEX [IX_SystemNotifications_User_id] ON [SystemNotifications] ([User_id]);
        END
        IF OBJECT_ID(N'[IPOAllotments]', N'U') IS NULL
        BEGIN
            CREATE TABLE [IPOAllotments] (
                [Allotment_id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_IPOAllotments] PRIMARY KEY,
                [Application_id] int NOT NULL,
                [User_id] int NOT NULL,
                [IPO_id] int NOT NULL,
                [Allotted_lots] int NOT NULL,
                [Allotted_shares] int NOT NULL,
                [Allotment_price] decimal(18,2) NOT NULL,
                [Status] nvarchar(30) NOT NULL,
                [Allotment_date] datetime2 NOT NULL,
                CONSTRAINT [FK_IPOAllotments_IPOApplications] FOREIGN KEY ([Application_id]) REFERENCES [IPOApplications] ([Application_id]),
                CONSTRAINT [FK_IPOAllotments_Users] FOREIGN KEY ([User_id]) REFERENCES [Users] ([User_id]),
                CONSTRAINT [FK_IPOAllotments_IPOs] FOREIGN KEY ([IPO_id]) REFERENCES [IPOs] ([IPO_id]),
                CONSTRAINT [UQ_IPOAllotments_Application] UNIQUE ([Application_id])
            );
            CREATE INDEX [IX_IPOAllotments_User_id] ON [IPOAllotments] ([User_id]);
            CREATE INDEX [IX_IPOAllotments_IPO_id] ON [IPOAllotments] ([IPO_id]);
        END
        IF COL_LENGTH(N'[Users]', N'DematAccountNumber') IS NULL
            ALTER TABLE [Users] ADD [DematAccountNumber] nvarchar(30) NULL;
        IF COL_LENGTH(N'[Users]', N'UpiId') IS NULL
            ALTER TABLE [Users] ADD [UpiId] nvarchar(100) NULL;
        IF COL_LENGTH(N'[Users]', N'FailedLoginAttempts') IS NULL
            ALTER TABLE [Users] ADD [FailedLoginAttempts] int NOT NULL CONSTRAINT [DF_Users_FailedLoginAttempts] DEFAULT (0);
        IF COL_LENGTH(N'[Users]', N'LockoutEndsAt') IS NULL
            ALTER TABLE [Users] ADD [LockoutEndsAt] datetime2 NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Application_number') IS NULL
            ALTER TABLE [IPOApplications] ADD [Application_number] nvarchar(40) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Bid_price') IS NULL
            ALTER TABLE [IPOApplications] ADD [Bid_price] decimal(18,2) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Demat_account_number') IS NULL
            ALTER TABLE [IPOApplications] ADD [Demat_account_number] nvarchar(30) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Upi_id') IS NULL
            ALTER TABLE [IPOApplications] ADD [Upi_id] nvarchar(100) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Payment_reference') IS NULL
            ALTER TABLE [IPOApplications] ADD [Payment_reference] nvarchar(40) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Payment_status') IS NULL
            ALTER TABLE [IPOApplications] ADD [Payment_status] nvarchar(20) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Razorpay_order_id') IS NULL
            ALTER TABLE [IPOApplications] ADD [Razorpay_order_id] nvarchar(40) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Razorpay_payment_id') IS NULL
            ALTER TABLE [IPOApplications] ADD [Razorpay_payment_id] nvarchar(40) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Payment_amount') IS NULL
            ALTER TABLE [IPOApplications] ADD [Payment_amount] decimal(18,2) NULL;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IPOApplications_RazorpayOrderId' AND object_id = OBJECT_ID(N'[IPOApplications]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_IPOApplications_RazorpayOrderId] ON [IPOApplications] ([Razorpay_order_id]) WHERE [Razorpay_order_id] IS NOT NULL');
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IPOApplications_RazorpayPaymentId' AND object_id = OBJECT_ID(N'[IPOApplications]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_IPOApplications_RazorpayPaymentId] ON [IPOApplications] ([Razorpay_payment_id]) WHERE [Razorpay_payment_id] IS NOT NULL');
        IF COL_LENGTH(N'[IPOApplications]', N'Cancellation_admin_id') IS NULL
            ALTER TABLE [IPOApplications] ADD [Cancellation_admin_id] int NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Cancellation_reason') IS NULL
            ALTER TABLE [IPOApplications] ADD [Cancellation_reason] nvarchar(500) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Cancellation_date') IS NULL
            ALTER TABLE [IPOApplications] ADD [Cancellation_date] datetime2 NULL;
        IF OBJECT_ID(N'[PaymentRecords]', N'U') IS NULL
        BEGIN
            CREATE TABLE [PaymentRecords] (
                [PaymentRecordId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PaymentRecords] PRIMARY KEY,
                [UserId] int NOT NULL,
                [IpoId] int NOT NULL,
                [AppliedLots] int NOT NULL,
                [AppliedShares] int NOT NULL,
                [BidPrice] decimal(18,2) NOT NULL,
                [ApplicationNumber] nvarchar(40) NOT NULL,
                [RazorpayOrderId] nvarchar(40) NOT NULL,
                [RazorpayPaymentId] nvarchar(40) NULL,
                [Amount] decimal(18,2) NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [UpdatedAt] datetime2 NOT NULL,
                [RefundStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_PaymentRecords_RefundStatus] DEFAULT ('Not Applicable'),
                [UnblockedAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_PaymentRecords_UnblockedAmount] DEFAULT (0),
                [RefundProcessedAt] datetime2 NULL,
                CONSTRAINT [UQ_PaymentRecords_RazorpayOrderId] UNIQUE ([RazorpayOrderId])
            );
            CREATE UNIQUE INDEX [IX_PaymentRecords_RazorpayPaymentId]
                ON [PaymentRecords] ([RazorpayPaymentId])
                WHERE [RazorpayPaymentId] IS NOT NULL;
            CREATE INDEX [IX_PaymentRecords_UserId] ON [PaymentRecords] ([UserId]);
        END
        IF COL_LENGTH(N'[Users]', N'SimulatedCashBalance') IS NULL
            ALTER TABLE [Users] ADD [SimulatedCashBalance] decimal(18,2) NOT NULL CONSTRAINT [DF_Users_SimulatedCashBalance] DEFAULT (100000);
        IF COL_LENGTH(N'[IPOListing]', N'StockSymbol') IS NULL
            ALTER TABLE [IPOListing] ADD [StockSymbol] nvarchar(30) NULL;
        IF COL_LENGTH(N'[IPOListing]', N'Exchange') IS NULL
            ALTER TABLE [IPOListing] ADD [Exchange] nvarchar(10) NULL;
        IF OBJECT_ID(N'[MarketTradingHolidays]', N'U') IS NULL
        BEGIN
            CREATE TABLE [MarketTradingHolidays] (
                [HolidayId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_MarketTradingHolidays] PRIMARY KEY,
                [HolidayDate] date NOT NULL,
                [Name] nvarchar(150) NOT NULL,
                CONSTRAINT [UQ_MarketTradingHolidays_HolidayDate] UNIQUE ([HolidayDate])
            );
        END
        IF OBJECT_ID(N'[StockMarketData]', N'U') IS NULL
        BEGIN
            CREATE TABLE [StockMarketData] (
                [MarketDataId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StockMarketData] PRIMARY KEY,
                [IPOId] int NOT NULL,
                [CompanyId] int NOT NULL,
                [StockSymbol] nvarchar(30) NOT NULL,
                [Exchange] nvarchar(10) NOT NULL,
                [IssuePrice] decimal(18,2) NULL,
                [ListingPrice] decimal(18,2) NULL,
                [CurrentPrice] decimal(18,2) NULL,
                [PreviousClose] decimal(18,2) NULL,
                [DayHigh] decimal(18,2) NULL,
                [DayLow] decimal(18,2) NULL,
                [Volume] bigint NULL,
                [PriceChange] decimal(18,2) NULL,
                [PriceChangePercentage] decimal(10,4) NULL,
                [LastUpdated] datetime2 NOT NULL,
                [IsAvailable] bit NOT NULL CONSTRAINT [DF_StockMarketData_IsAvailable] DEFAULT (0),
                CONSTRAINT [FK_StockMarketData_IPO] FOREIGN KEY ([IPOId]) REFERENCES [IPOs] ([IPO_id]) ON DELETE CASCADE,
                CONSTRAINT [FK_StockMarketData_Company] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Company_id])
            );
            CREATE UNIQUE INDEX [IX_StockMarketData_IPOId_Exchange] ON [StockMarketData] ([IPOId], [Exchange]);
            CREATE INDEX [IX_StockMarketData_StockSymbol] ON [StockMarketData] ([StockSymbol]);
        END
        IF COL_LENGTH(N'[PaymentRecords]', N'AppliedLots') IS NULL
            ALTER TABLE [PaymentRecords] ADD [AppliedLots] int NOT NULL CONSTRAINT [DF_PaymentRecords_AppliedLots] DEFAULT (0);
        IF COL_LENGTH(N'[PaymentRecords]', N'AppliedShares') IS NULL
            ALTER TABLE [PaymentRecords] ADD [AppliedShares] int NOT NULL CONSTRAINT [DF_PaymentRecords_AppliedShares] DEFAULT (0);
        IF COL_LENGTH(N'[PaymentRecords]', N'BidPrice') IS NULL
            ALTER TABLE [PaymentRecords] ADD [BidPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_PaymentRecords_BidPrice] DEFAULT (0);
        IF COL_LENGTH(N'[PaymentRecords]', N'RefundStatus') IS NULL
            ALTER TABLE [PaymentRecords] ADD [RefundStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_PaymentRecords_RefundStatus] DEFAULT ('Not Applicable');
        IF COL_LENGTH(N'[PaymentRecords]', N'UnblockedAmount') IS NULL
            ALTER TABLE [PaymentRecords] ADD [UnblockedAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_PaymentRecords_UnblockedAmount] DEFAULT (0);
        IF OBJECT_ID(N'[AuditLogs]', N'U') IS NULL
        BEGIN
            CREATE TABLE [AuditLogs] (
                [AuditLogId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AuditLogs] PRIMARY KEY,
                [ActorUserId] int NULL,
                [Action] nvarchar(100) NOT NULL,
                [EntityType] nvarchar(100) NULL,
                [EntityId] nvarchar(100) NULL,
                [Details] nvarchar(1000) NULL,
                [IpAddress] nvarchar(64) NULL,
                [CreatedAt] datetime2 NOT NULL
            );
            CREATE INDEX [IX_AuditLogs_ActorUserId_CreatedAt] ON [AuditLogs] ([ActorUserId], [CreatedAt]);
        END
        IF OBJECT_ID(N'[VirtualBalanceLedgers]', N'U') IS NULL
        BEGIN
            CREATE TABLE [VirtualBalanceLedgers] (
                [LedgerId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_VirtualBalanceLedgers] PRIMARY KEY,
                [UserId] int NOT NULL,
                [AdminUserId] int NULL,
                [TransactionType] nvarchar(50) NOT NULL,
                [Amount] decimal(18,2) NOT NULL,
                [BalanceBefore] decimal(18,2) NOT NULL,
                [BalanceAfter] decimal(18,2) NOT NULL,
                [Reason] nvarchar(500) NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                CONSTRAINT [FK_VirtualBalanceLedgers_Users] FOREIGN KEY ([UserId]) REFERENCES [Users] ([User_id])
            );
            CREATE INDEX [IX_VirtualBalanceLedgers_UserId_CreatedAt] ON [VirtualBalanceLedgers] ([UserId], [CreatedAt]);
            CREATE INDEX [IX_VirtualBalanceLedgers_AdminUserId] ON [VirtualBalanceLedgers] ([AdminUserId]);
        END
        IF OBJECT_ID(N'[IPOGMPHistory]', N'U') IS NULL
        BEGIN
            CREATE TABLE [IPOGMPHistory] (
                [Gmp_id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_IPOGMPHistory] PRIMARY KEY,
                [IPO_id] int NOT NULL,
                [GMPPrice] decimal(18,2) NOT NULL,
                [EstimatedListingPrice] decimal(18,2) NULL,
                [ExpectedListingGainPercentage] decimal(10,2) NULL,
                [KostakRate] decimal(18,2) NULL,
                [SubjectToSauda] decimal(18,2) NULL,
                [FireRating] nvarchar(50) NULL,
                [Source] nvarchar(200) NULL,
                [RecordedDate] datetime2 NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                CONSTRAINT [FK_IPOGMPHistory_IPOs] FOREIGN KEY ([IPO_id]) REFERENCES [IPOs] ([IPO_id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_IPOGMPHistory_IPO_id_RecordedDate] ON [IPOGMPHistory] ([IPO_id], [RecordedDate]);
        END
        IF OBJECT_ID(N'[StockPriceHistory]', N'U') IS NULL
        BEGIN
            CREATE TABLE [StockPriceHistory] (
                [HistoryId] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StockPriceHistory] PRIMARY KEY,
                [IPOId] int NOT NULL,
                [StockSymbol] nvarchar(30) NOT NULL,
                [Exchange] nvarchar(10) NOT NULL,
                [Price] decimal(18,2) NOT NULL,
                [OpenPrice] decimal(18,2) NOT NULL,
                [HighPrice] decimal(18,2) NOT NULL,
                [LowPrice] decimal(18,2) NOT NULL,
                [Volume] bigint NOT NULL,
                [Timestamp] datetime2 NOT NULL,
                [IsSimulated] bit NOT NULL CONSTRAINT [DF_StockPriceHistory_IsSimulated] DEFAULT (1),
                CONSTRAINT [FK_StockPriceHistory_IPO] FOREIGN KEY ([IPOId]) REFERENCES [IPOs] ([IPO_id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_StockPriceHistory_IPOId_Exchange_Timestamp] ON [StockPriceHistory] ([IPOId], [Exchange], [Timestamp]);
        END
        IF OBJECT_ID(N'[StockSipPlans]', N'U') IS NULL
        BEGIN
            CREATE TABLE [StockSipPlans] (
                [SipId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StockSipPlans] PRIMARY KEY,
                [UserId] int NOT NULL,
                [IPOId] int NOT NULL,
                [StockSymbol] nvarchar(30) NOT NULL,
                [Exchange] nvarchar(10) NOT NULL CONSTRAINT [DF_StockSipPlans_Exchange] DEFAULT ('NSE'),
                [Frequency] nvarchar(20) NOT NULL CONSTRAINT [DF_StockSipPlans_Frequency] DEFAULT ('Monthly'),
                [InstallmentAmount] decimal(18,2) NOT NULL,
                [InstallmentDay] int NOT NULL CONSTRAINT [DF_StockSipPlans_InstallmentDay] DEFAULT (5),
                [StartDate] datetime2 NOT NULL,
                [EndDate] datetime2 NULL,
                [NextExecutionDate] datetime2 NOT NULL,
                [LastExecutedDate] datetime2 NULL,
                [InstallmentsCompleted] int NOT NULL CONSTRAINT [DF_StockSipPlans_InstallmentsCompleted] DEFAULT (0),
                [TotalInvested] decimal(18,2) NOT NULL CONSTRAINT [DF_StockSipPlans_TotalInvested] DEFAULT (0),
                [TotalSharesAccumulated] int NOT NULL CONSTRAINT [DF_StockSipPlans_TotalSharesAccumulated] DEFAULT (0),
                [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_StockSipPlans_Status] DEFAULT ('Active'),
                [CreatedAt] datetime2 NOT NULL,
                [UpdatedAt] datetime2 NULL,
                CONSTRAINT [FK_StockSipPlans_Users] FOREIGN KEY ([UserId]) REFERENCES [Users] ([User_id]),
                CONSTRAINT [FK_StockSipPlans_IPOs] FOREIGN KEY ([IPOId]) REFERENCES [IPOs] ([IPO_id])
            );
            CREATE INDEX [IX_StockSipPlans_UserId] ON [StockSipPlans] ([UserId]);
            CREATE INDEX [IX_StockSipPlans_IPOId] ON [StockSipPlans] ([IPOId]);
            CREATE INDEX [IX_StockSipPlans_Status] ON [StockSipPlans] ([Status]);
        END
        IF OBJECT_ID(N'[SystemNotifications]', N'U') IS NOT NULL
        BEGIN
            UPDATE [SystemNotifications]
            SET [Created_at] = DATEADD(minute, -330, [Created_at])
            WHERE [Title] LIKE '%Stock SIP%' AND [Created_at] > GETUTCDATE();
        END
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Email_Unique' AND object_id = OBJECT_ID(N'[Users]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_Users_Email_Unique] ON [Users] ([Email])');
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IPOApplications_ApplicationNumber_Unique' AND object_id = OBJECT_ID(N'[IPOApplications]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_IPOApplications_ApplicationNumber_Unique] ON [IPOApplications] ([Application_number]) WHERE [Application_number] IS NOT NULL');
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IPOWatchlist_UserId_IpoId_Unique' AND object_id = OBJECT_ID(N'[IPOWatchlist]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_IPOWatchlist_UserId_IpoId_Unique] ON [IPOWatchlist] ([User_id], [IPO_id])');
        IF COL_LENGTH(N'[StockMarketData]', N'IsSimulated') IS NULL
            ALTER TABLE [StockMarketData] ADD [IsSimulated] bit NOT NULL CONSTRAINT [DF_StockMarketData_IsSimulated] DEFAULT (0);
        IF COL_LENGTH(N'[StockMarketData]', N'DataSource') IS NULL
            ALTER TABLE [StockMarketData] ADD [DataSource] nvarchar(100) NULL;
        IF COL_LENGTH(N'[StockPriceHistory]', N'IsSimulated') IS NULL
            ALTER TABLE [StockPriceHistory] ADD [IsSimulated] bit NOT NULL CONSTRAINT [DF_StockPriceHistory_IsSimulated] DEFAULT (1);
        IF COL_LENGTH(N'[PaymentRecords]', N'RefundProcessedAt') IS NULL
            ALTER TABLE [PaymentRecords] ADD [RefundProcessedAt] datetime2 NULL;
        IF COL_LENGTH(N'[IPOs]', N'Opening_time') IS NULL
            ALTER TABLE [IPOs] ADD [Opening_time] time NULL;
        IF COL_LENGTH(N'[IPOs]', N'Closing_time') IS NULL
            ALTER TABLE [IPOs] ADD [Closing_time] time NULL;
        IF COL_LENGTH(N'[IPOs]', N'Opening_time') IS NOT NULL
        BEGIN
            EXEC(N'UPDATE [IPOs] SET [Opening_time] = ''10:00:00'' WHERE [Opening_time] IS NULL OR [Opening_time] = ''09:30:00'';
                   UPDATE [IPOs] SET [Closing_time] = ''17:00:00'' WHERE [Closing_time] IS NULL OR [Closing_time] = ''15:30:00'';');
        END
        IF COL_LENGTH(N'[PaymentRecords]', N'PaymentMode') IS NULL
            ALTER TABLE [PaymentRecords] ADD [PaymentMode] nvarchar(40) NOT NULL CONSTRAINT [DF_PaymentRecords_PaymentMode] DEFAULT ('SIMULATED_MANDATE');
        IF COL_LENGTH(N'[PaymentRecords]', N'IsSimulated') IS NULL
            ALTER TABLE [PaymentRecords] ADD [IsSimulated] bit NOT NULL CONSTRAINT [DF_PaymentRecords_IsSimulated] DEFAULT (1);
        IF COL_LENGTH(N'[PaymentRecords]', N'RefundReferenceId') IS NULL
            ALTER TABLE [PaymentRecords] ADD [RefundReferenceId] nvarchar(50) NULL;
        IF COL_LENGTH(N'[PaymentRecords]', N'ReceiptNumber') IS NULL
            ALTER TABLE [PaymentRecords] ADD [ReceiptNumber] nvarchar(40) NULL;
        IF COL_LENGTH(N'[PaymentRecords]', N'FailureReason') IS NULL
            ALTER TABLE [PaymentRecords] ADD [FailureReason] nvarchar(500) NULL;
        IF COL_LENGTH(N'[PaymentRecords]', N'ReconciledAt') IS NULL
            ALTER TABLE [PaymentRecords] ADD [ReconciledAt] datetime2 NULL;
        IF COL_LENGTH(N'[PaymentRecords]', N'ReconciliationStatus') IS NULL
            ALTER TABLE [PaymentRecords] ADD [ReconciliationStatus] nvarchar(50) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Payment_mode') IS NULL
            ALTER TABLE [IPOApplications] ADD [Payment_mode] nvarchar(40) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Is_simulated_payment') IS NULL
            ALTER TABLE [IPOApplications] ADD [Is_simulated_payment] bit NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Receipt_number') IS NULL
            ALTER TABLE [IPOApplications] ADD [Receipt_number] nvarchar(40) NULL;
        IF OBJECT_ID(N'[PaymentWebhookLogs]', N'U') IS NULL
        BEGIN
            CREATE TABLE [PaymentWebhookLogs] (
                [WebhookLogId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PaymentWebhookLogs] PRIMARY KEY,
                [EventId] nvarchar(100) NOT NULL,
                [EventType] nvarchar(100) NOT NULL,
                [RazorpayOrderId] nvarchar(50) NULL,
                [RazorpayPaymentId] nvarchar(50) NULL,
                [Payload] nvarchar(max) NOT NULL,
                [Status] nvarchar(30) NOT NULL CONSTRAINT [DF_PaymentWebhookLogs_Status] DEFAULT ('Received'),
                [RetryCount] int NOT NULL CONSTRAINT [DF_PaymentWebhookLogs_RetryCount] DEFAULT (0),
                [ErrorMessage] nvarchar(1000) NULL,
                [ReceivedAt] datetime2 NOT NULL,
                [ProcessedAt] datetime2 NULL
            );
            CREATE INDEX [IX_PaymentWebhookLogs_EventId] ON [PaymentWebhookLogs] ([EventId]);
            CREATE INDEX [IX_PaymentWebhookLogs_Status_RetryCount] ON [PaymentWebhookLogs] ([Status], [RetryCount]);
            CREATE INDEX [IX_PaymentWebhookLogs_RazorpayOrderId] ON [PaymentWebhookLogs] ([RazorpayOrderId]);
        END
        IF COL_LENGTH(N'[IPOAllotments]', N'Debited_amount') IS NULL
            ALTER TABLE [IPOAllotments] ADD [Debited_amount] decimal(18,2) NOT NULL CONSTRAINT [DF_IPOAllotments_Debited_amount] DEFAULT (0);
        IF COL_LENGTH(N'[IPOAllotments]', N'Unblocked_amount') IS NULL
            ALTER TABLE [IPOAllotments] ADD [Unblocked_amount] decimal(18,2) NOT NULL CONSTRAINT [DF_IPOAllotments_Unblocked_amount] DEFAULT (0);
        IF COL_LENGTH(N'[IPOAllotments]', N'Advice_number') IS NULL
            ALTER TABLE [IPOAllotments] ADD [Advice_number] nvarchar(50) NULL;
        IF COL_LENGTH(N'[IPOs]', N'Allotment_finalized') IS NULL
            ALTER TABLE [IPOs] ADD [Allotment_finalized] bit NOT NULL CONSTRAINT [DF_IPOs_Allotment_finalized] DEFAULT (0);
        IF COL_LENGTH(N'[IPOs]', N'Allotment_finalized_at') IS NULL
            ALTER TABLE [IPOs] ADD [Allotment_finalized_at] datetime2 NULL;
        IF OBJECT_ID(N'[IPOAllotmentHistories]', N'U') IS NULL
        BEGIN
            CREATE TABLE [IPOAllotmentHistories] (
                [AllotmentHistoryId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_IPOAllotmentHistories] PRIMARY KEY,
                [IPOId] int NOT NULL,
                [FinalizedByUserId] int NOT NULL,
                [FinalizedAt] datetime2 NOT NULL,
                [TotalApplications] int NOT NULL,
                [AllottedApplications] int NOT NULL,
                [PartialAllotmentCount] int NOT NULL,
                [UnallottedCount] int NOT NULL,
                [TotalAppliedShares] bigint NOT NULL,
                [TotalAllottedShares] bigint NOT NULL,
                [TotalDebitedAmount] decimal(18,2) NOT NULL,
                [TotalUnblockedAmount] decimal(18,2) NOT NULL,
                [OversubscriptionMultiple] decimal(10,2) NOT NULL,
                [CutOffPrice] decimal(18,2) NOT NULL,
                [Notes] nvarchar(500) NULL,
                CONSTRAINT [FK_IPOAllotmentHistories_IPOs] FOREIGN KEY ([IPOId]) REFERENCES [IPOs] ([IPO_id]) ON DELETE CASCADE
            );
            CREATE INDEX [IX_IPOAllotmentHistories_IPOId] ON [IPOAllotmentHistories] ([IPOId]);
            CREATE INDEX [IX_IPOAllotmentHistories_FinalizedAt] ON [IPOAllotmentHistories] ([FinalizedAt]);
        END
        IF OBJECT_ID(N'[WalletWithdrawals]', N'U') IS NULL
        BEGIN
            CREATE TABLE [WalletWithdrawals] (
                [WithdrawalId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_WalletWithdrawals] PRIMARY KEY,
                [UserId] int NOT NULL,
                [ReferenceNumber] nvarchar(40) NOT NULL,
                [Amount] decimal(18,2) NOT NULL,
                [BalanceBefore] decimal(18,2) NOT NULL,
                [BalanceAfter] decimal(18,2) NOT NULL,
                [PayoutMethod] nvarchar(30) NOT NULL,
                [DestinationAccount] nvarchar(120) NOT NULL,
                [Status] nvarchar(20) NOT NULL CONSTRAINT [DF_WalletWithdrawals_Status] DEFAULT ('Completed'),
                [Remarks] nvarchar(300) NULL,
                [CreatedAt] datetime2 NOT NULL,
                CONSTRAINT [FK_WalletWithdrawals_Users] FOREIGN KEY ([UserId]) REFERENCES [Users] ([User_id]) ON DELETE CASCADE,
                CONSTRAINT [UQ_WalletWithdrawals_ReferenceNumber] UNIQUE ([ReferenceNumber])
            );
            CREATE INDEX [IX_WalletWithdrawals_UserId_CreatedAt] ON [WalletWithdrawals] ([UserId], [CreatedAt]);
        END
        """);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapHub<MarketHub>("/hubs/market");
app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

if (args.Contains("--run-ipo-status-tests"))
{
    using var testScope = app.Services.CreateScope();
    var statusService = testScope.ServiceProvider.GetRequiredService<IIpoStatusService>();
    var clock = testScope.ServiceProvider.GetRequiredService<IIpoClock>();
    var calendar = testScope.ServiceProvider.GetRequiredService<IMarketTradingCalendar>();
    var db = testScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    Console.WriteLine("================================================================================");
    Console.WriteLine("RUNNING AUTOMATIC IPO STATUS MANAGEMENT TEST SUITE");
    Console.WriteLine("================================================================================");

    int passedCount = 0;
    int totalTests = 0;

    void AssertTest(string testName, bool condition, string detail)
    {
        totalTests++;
        if (condition)
        {
            passedCount++;
            Console.WriteLine($"[PASS] {testName}: {detail}");
        }
        else
        {
            Console.WriteLine($"[FAIL] {testName}: {detail}");
        }
    }

    var testIpo = new IPO
    {
        IPO_id = 99999,
        IPO_name = "Vertex Renewable Energy IPO",
        Open_date = new DateTime(2026, 12, 7),
        Opening_time = new TimeSpan(10, 0, 0),
        Close_date = new DateTime(2026, 12, 9),
        Closing_time = new TimeSpan(17, 0, 0),
        Status = "Approved"
    };

    // 1. 7 Dec 2026, 09:59 AM IST -> Upcoming
    DateTime t1 = new(2026, 12, 7, 9, 59, 0);
    string s1 = statusService.CalculateStatus(testIpo, t1);
    var (canApply1, reason1) = statusService.CanApply(testIpo, t1);
    AssertTest("1. 7 Dec 2026 09:59 AM IST", s1 == "Upcoming" && !canApply1, $"Status={s1}, CanApply={canApply1} ({reason1})");

    // 2. 7 Dec 2026, 10:00 AM IST -> Open
    DateTime t2 = new(2026, 12, 7, 10, 0, 0);
    string s2 = statusService.CalculateStatus(testIpo, t2);
    var (canApply2, reason2) = statusService.CanApply(testIpo, t2);
    AssertTest("2. 7 Dec 2026 10:00 AM IST", s2 == "Open" && canApply2, $"Status={s2}, CanApply={canApply2}");

    // 3. 7 Dec 2026, 11:30 AM IST -> Open
    DateTime t3 = new(2026, 12, 7, 11, 30, 0);
    string s3 = statusService.CalculateStatus(testIpo, t3);
    var (canApply3, reason3) = statusService.CanApply(testIpo, t3);
    AssertTest("3. 7 Dec 2026 11:30 AM IST", s3 == "Open" && canApply3, $"Status={s3}, CanApply={canApply3}");

    // 4. 9 Dec 2026, before closing time (16:59:59 IST) -> Open
    DateTime t4 = new(2026, 12, 9, 16, 59, 59);
    string s4 = statusService.CalculateStatus(testIpo, t4);
    var (canApply4, reason4) = statusService.CanApply(testIpo, t4);
    AssertTest("4. 9 Dec 2026 before closing (16:59:59)", s4 == "Open" && canApply4, $"Status={s4}, CanApply={canApply4}");

    // 5. After closing date and time (9 Dec 2026 17:00:01 IST) -> Closed
    DateTime t5 = new(2026, 12, 9, 17, 0, 1);
    string s5 = statusService.CalculateStatus(testIpo, t5);
    var (canApply5, reason5) = statusService.CanApply(testIpo, t5);
    AssertTest("5. After closing date and time (17:00:01)", s5 == "Closed" && !canApply5, $"Status={s5}, CanApply={canApply5} ({reason5})");

    // 6. Rejected IPO -> Must never become Open
    var rejectedIpo = new IPO { IPO_name = "Rejected IPO", Open_date = new(2026, 12, 7), Opening_time = new(10, 0, 0), Close_date = new(2026, 12, 9), Closing_time = new(17, 0, 0), Status = "Rejected" };
    string s6 = statusService.CalculateStatus(rejectedIpo, t2);
    var (canApply6, reason6) = statusService.CanApply(rejectedIpo, t2);
    AssertTest("6. Rejected IPO during open window", s6 == "Rejected" && !canApply6, $"Status={s6}, CanApply={canApply6} ({reason6})");

    // 7. Cancelled IPO -> Must never become Open
    var cancelledIpo = new IPO { IPO_name = "Cancelled IPO", Open_date = new(2026, 12, 7), Opening_time = new(10, 0, 0), Close_date = new(2026, 12, 9), Closing_time = new(17, 0, 0), Status = "Cancelled" };
    string s7 = statusService.CalculateStatus(cancelledIpo, t2);
    var (canApply7, reason7) = statusService.CanApply(cancelledIpo, t2);
    AssertTest("7. Cancelled IPO during open window", s7 == "Cancelled" && !canApply7, $"Status={s7}, CanApply={canApply7} ({reason7})");

    // 8. Unapproved (Pending) IPO -> Must never become Open
    var pendingIpo = new IPO { IPO_name = "Pending IPO", Open_date = new(2026, 12, 7), Opening_time = new(10, 0, 0), Close_date = new(2026, 12, 9), Closing_time = new(17, 0, 0), Status = "Pending" };
    string s8 = statusService.CalculateStatus(pendingIpo, t2);
    var (canApply8, reason8) = statusService.CanApply(pendingIpo, t2);
    AssertTest("8. Unapproved (Pending) IPO during open window", s8 == "Pending" && !canApply8, $"Status={s8}, CanApply={canApply8} ({reason8})");

    // 9. Weekend Test (12 Dec 2026 is Saturday) -> Not allowed
    DateTime weekendTime = new(2026, 12, 12, 12, 0, 0);
    var (canApplyWeekend, reasonWeekend) = statusService.CanApply(testIpo, weekendTime);
    AssertTest("9. Weekend application restriction (Saturday)", !canApplyWeekend, $"CanApply={canApplyWeekend} ({reasonWeekend})");

    // 10. After-Hours Test (7 Dec 2026 18:00:00 IST -> After 05:00 PM) -> Not allowed
    DateTime afterHoursTime = new(2026, 12, 7, 18, 0, 0);
    var (canApplyAfterHours, reasonAfterHours) = statusService.CanApply(testIpo, afterHoursTime);
    AssertTest("10. After-hours application restriction (06:00 PM IST)", !canApplyAfterHours, $"CanApply={canApplyAfterHours} ({reasonAfterHours})");

    // 11. Live Database Vertex Renewable Energy IPO synchronization test
    var vertexDb = await db.IPOs.FirstOrDefaultAsync(i => i.IPO_id == 19 || i.IPO_name.Contains("Vertex"));
    if (vertexDb != null)
    {
        Console.WriteLine("\n--- Live Database Synchronization Tests (Vertex Renewable Energy IPO) ---");

        // A. Set clock to 7 Dec 2026, 09:59 AM -> DB Status should become Upcoming
        clock.SetSimulatedIstTime(t1);
        await statusService.SynchronizeIpoStatusesAsync();
        var reloaded1 = await db.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == vertexDb.IPO_id);
        AssertTest("11A. DB Sync at 09:59 AM", reloaded1?.Status == "Upcoming", $"DB Status={reloaded1?.Status}");

        // B. Set clock to 7 Dec 2026, 10:00 AM -> DB Status should become Open
        clock.SetSimulatedIstTime(t2);
        await statusService.SynchronizeIpoStatusesAsync();
        var reloaded2 = await db.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == vertexDb.IPO_id);
        AssertTest("11B. DB Sync at 10:00 AM", reloaded2?.Status == "Open", $"DB Status={reloaded2?.Status}");

        // C. Set clock to 9 Dec 2026, 17:01 PM -> DB Status should become Closed
        clock.SetSimulatedIstTime(t5);
        await statusService.SynchronizeIpoStatusesAsync();
        var reloaded3 = await db.IPOs.AsNoTracking().FirstOrDefaultAsync(i => i.IPO_id == vertexDb.IPO_id);
        AssertTest("11C. DB Sync after closing (17:01 PM)", reloaded3?.Status == "Closed", $"DB Status={reloaded3?.Status}");

        // D. Verify AuditLog was recorded for transitions
        var auditCount = await db.AuditLogs.CountAsync(a => a.EntityType == "IPO" && a.EntityId == vertexDb.IPO_id.ToString());
        AssertTest("11D. Audit Log History Recorded", auditCount > 0, $"Recorded {auditCount} audit events for IPO {vertexDb.IPO_id}");

        // Reset clock
        clock.ResetSimulatedTime();
        // Return Vertex back to Upcoming for the upcoming 7 Dec 2026 schedule
        clock.SetSimulatedIstTime(t1);
        await statusService.SynchronizeIpoStatusesAsync();
        clock.ResetSimulatedTime();
    }

    // 12. Weekend and Live Market Trading Calendar Tests
    Console.WriteLine("\n--- Weekend & Live Market Trading Calendar Tests ---");

    // A. Saturday Market Closed Test (21 Nov 2026 is Saturday)
    DateTime satDate = new(2026, 11, 21, 11, 30, 0);
    bool isSatTradingDay = await calendar.IsTradingDayAsync(satDate);
    bool isSatMarketOpen = await calendar.IsMarketOpenAsync(satDate);
    AssertTest("12A. Saturday Live Market Closed (21 Nov 2026 11:30 AM)", !isSatTradingDay && !isSatMarketOpen, $"TradingDay={isSatTradingDay}, MarketOpen={isSatMarketOpen}");

    // B. Sunday Market Closed Test (22 Nov 2026 is Sunday)
    DateTime sunDate = new(2026, 11, 22, 14, 0, 0);
    bool isSunTradingDay = await calendar.IsTradingDayAsync(sunDate);
    bool isSunMarketOpen = await calendar.IsMarketOpenAsync(sunDate);
    AssertTest("12B. Sunday Live Market Closed (22 Nov 2026 02:00 PM)", !isSunTradingDay && !isSunMarketOpen, $"TradingDay={isSunTradingDay}, MarketOpen={isSunMarketOpen}");

    // C. Weekday Trading Hours Open Test (23 Nov 2026 is Monday at 10:30 AM)
    DateTime monOpenDate = new(2026, 11, 23, 10, 30, 0);
    bool isMonTradingDay = await calendar.IsTradingDayAsync(monOpenDate);
    bool isMonMarketOpen = await calendar.IsMarketOpenAsync(monOpenDate);
    AssertTest("12C. Monday In-Hours Market Open (23 Nov 2026 10:30 AM)", isMonTradingDay && isMonMarketOpen, $"TradingDay={isMonTradingDay}, MarketOpen={isMonMarketOpen}");

    // D. Weekday After-Hours Closed Test (23 Nov 2026 is Monday at 04:30 PM)
    DateTime monClosedDate = new(2026, 11, 23, 16, 30, 0);
    bool isMonAfterHoursMarketOpen = await calendar.IsMarketOpenAsync(monClosedDate);
    AssertTest("12D. Monday After-Hours Market Closed (23 Nov 2026 04:30 PM)", !isMonAfterHoursMarketOpen, $"MarketOpen={isMonAfterHoursMarketOpen}");

    Console.WriteLine("================================================================================");
    Console.WriteLine($"TEST RESULTS: {passedCount} / {totalTests} PASSED ({(passedCount == totalTests ? "ALL TESTS PASSED" : "FAILURES DETECTED")})");
    Console.WriteLine("================================================================================");
    return;
}

app.Run();