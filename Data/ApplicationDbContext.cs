using IPOInvestmentManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace IPOInvestmentManagement.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // Tables
        public DbSet<User> Users { get; set; }
        public DbSet<Company> Companies { get; set; }
        public DbSet<IPO> IPOs { get; set; }
        public DbSet<IPOCategory> IPOCategories { get; set; }
        public DbSet<IPOAnalysis> IPOAnalyses { get; set; }
        public DbSet<IPOFinancial> IPOFinancials { get; set; }
        public DbSet<IPOListing> IPOListings { get; set; }
        public DbSet<IPOApplication> IPOApplications { get; set; }
        public DbSet<IPOAllotment> IPOAllotments { get; set; }
        public DbSet<Investment> Investments { get; set; }
        public DbSet<InvestmentTransaction> InvestmentTransactions { get; set; }
        public DbSet<IPOWatchlist> IPOWatchlists { get; set; }
        public DbSet<SystemNotification> SystemNotifications { get; set; }
        public DbSet<PaymentRecord> PaymentRecords { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Company>()
                .Property(company => company.Company_id)
                .ValueGeneratedOnAdd();

            modelBuilder.Entity<User>()
                .Property(user => user.First_name)
                .IsRequired(false);
            modelBuilder.Entity<User>()
                .Property(user => user.Last_name)
                .IsRequired(false);
            modelBuilder.Entity<User>()
                .Property(user => user.DematAccountNumber)
                .IsRequired(false);
            modelBuilder.Entity<User>()
                .Property(user => user.UpiId)
                .IsRequired(false);
            modelBuilder.Entity<User>()
                .Property(user => user.Phone)
                .IsRequired(false);

            // Company → IPO
            modelBuilder.Entity<IPO>()
                .HasOne<Company>()
                .WithMany()
                .HasForeignKey(i => i.Company_id)
                .OnDelete(DeleteBehavior.Restrict);

            // IPO → IPOAnalysis
            modelBuilder.Entity<IPOAnalysis>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(a => a.IPO_id)
                .OnDelete(DeleteBehavior.Cascade);

            // IPO → IPOFinancial
            modelBuilder.Entity<IPOFinancial>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(f => f.IPO_id)
                .OnDelete(DeleteBehavior.Cascade);

            // IPO → IPOListing
            modelBuilder.Entity<IPOListing>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(l => l.IPO_id)
                .OnDelete(DeleteBehavior.Cascade);

            // User → IPOApplication
            modelBuilder.Entity<IPOApplication>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(a => a.User_id)
                .OnDelete(DeleteBehavior.Restrict);

            // IPO → IPOApplication
            modelBuilder.Entity<IPOApplication>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(a => a.IPO_id)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<IPOAllotment>()
                .HasOne<IPOApplication>()
                .WithMany()
                .HasForeignKey(a => a.Application_id)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<IPOAllotment>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(a => a.User_id)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<IPOAllotment>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(a => a.IPO_id)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<IPOAllotment>()
                .HasIndex(a => a.Application_id)
                .IsUnique();

            // User → Investment
            modelBuilder.Entity<Investment>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(i => i.User_id)
                .OnDelete(DeleteBehavior.Restrict);

            // IPO → Investment
            modelBuilder.Entity<Investment>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(i => i.IPO_id)
                .OnDelete(DeleteBehavior.Restrict);

            // Investment → InvestmentTransaction
            modelBuilder.Entity<InvestmentTransaction>()
                .HasOne(transaction => transaction.Investment)
                .WithMany()
                .HasForeignKey(t => t.Investment_id)
                .OnDelete(DeleteBehavior.Cascade);

            // User → IPOWatchlist
            modelBuilder.Entity<IPOWatchlist>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(w => w.User_id)
                .OnDelete(DeleteBehavior.Cascade);

            // IPO → IPOWatchlist
            modelBuilder.Entity<IPOWatchlist>()
                .HasOne<IPO>()
                .WithMany()
                .HasForeignKey(w => w.IPO_id)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PaymentRecord>()
                .HasIndex(payment => payment.RazorpayOrderId)
                .IsUnique();
            modelBuilder.Entity<PaymentRecord>()
                .HasIndex(payment => payment.RazorpayPaymentId)
                .IsUnique()
                .HasFilter("[RazorpayPaymentId] IS NOT NULL");

            modelBuilder.Entity<IPOApplication>()
                .HasIndex(application => application.Razorpay_order_id)
                .IsUnique()
                .HasFilter("[Razorpay_order_id] IS NOT NULL");
            modelBuilder.Entity<IPOApplication>()
                .HasIndex(application => application.Razorpay_payment_id)
                .IsUnique()
                .HasFilter("[Razorpay_payment_id] IS NOT NULL");
            modelBuilder.Entity<User>()
                .HasIndex(user => user.Email)
                .IsUnique();
            modelBuilder.Entity<IPOApplication>()
                .HasIndex(application => application.Application_number)
                .IsUnique()
                .HasFilter("[Application_number] IS NOT NULL");
            modelBuilder.Entity<IPOWatchlist>()
                .HasIndex(watchlist => new { watchlist.User_id, watchlist.IPO_id })
                .IsUnique();
        }
    }
}