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
        public DbSet<Investment> Investments { get; set; }
        public DbSet<InvestmentTransaction> InvestmentTransactions { get; set; }
        public DbSet<IPOWatchlist> IPOWatchlists { get; set; }
        public DbSet<SystemNotification> SystemNotifications { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Company>()
                .Property(company => company.Company_id)
                .ValueGeneratedOnAdd();

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
                .HasOne<Investment>()
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
        }
    }
}