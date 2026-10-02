using IPOInvestmentManagement.Data;
using IPOInvestmentManagement.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();
builder.Services.AddScoped<IEmailNotificationService, SmtpEmailNotificationService>();

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