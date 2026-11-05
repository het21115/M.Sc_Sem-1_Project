using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Filters;
using IPOInvestmentManagement.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddScoped<AdminAuditFilter>();
builder.Services.AddControllersWithViews(options => options.Filters.AddService<AdminAuditFilter>());
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
builder.Services.AddScoped<IEmailNotificationService, SmtpEmailNotificationService>();
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
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Email_Unique' AND object_id = OBJECT_ID(N'[Users]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_Users_Email_Unique] ON [Users] ([Email])');
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IPOApplications_ApplicationNumber_Unique' AND object_id = OBJECT_ID(N'[IPOApplications]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_IPOApplications_ApplicationNumber_Unique] ON [IPOApplications] ([Application_number]) WHERE [Application_number] IS NOT NULL');
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IPOWatchlist_UserId_IpoId_Unique' AND object_id = OBJECT_ID(N'[IPOWatchlist]'))
            EXEC(N'CREATE UNIQUE INDEX [IX_IPOWatchlist_UserId_IpoId_Unique] ON [IPOWatchlist] ([User_id], [IPO_id])');
        IF COL_LENGTH(N'[PaymentRecords]', N'RefundProcessedAt') IS NULL
            ALTER TABLE [PaymentRecords] ADD [RefundProcessedAt] datetime2 NULL;
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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();