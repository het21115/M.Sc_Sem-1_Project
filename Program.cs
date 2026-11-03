using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();
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
        IF COL_LENGTH(N'[IPOApplications]', N'Cancellation_admin_id') IS NULL
            ALTER TABLE [IPOApplications] ADD [Cancellation_admin_id] int NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Cancellation_reason') IS NULL
            ALTER TABLE [IPOApplications] ADD [Cancellation_reason] nvarchar(500) NULL;
        IF COL_LENGTH(N'[IPOApplications]', N'Cancellation_date') IS NULL
            ALTER TABLE [IPOApplications] ADD [Cancellation_date] datetime2 NULL;
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