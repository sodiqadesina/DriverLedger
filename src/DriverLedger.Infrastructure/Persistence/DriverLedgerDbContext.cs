using DriverLedger.Application.Common;
using DriverLedger.Domain.Common;
using DriverLedger.Domain.Drivers;
using DriverLedger.Domain.Files;
using DriverLedger.Domain.Identity;
using DriverLedger.Domain.Ops;
using DriverLedger.Domain.Receipts;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace DriverLedger.Infrastructure.Persistence
{
    public sealed class DriverLedgerDbContext : DbContext
    {
        private readonly ITenantProvider _tenantProvider;

        public DriverLedgerDbContext(DbContextOptions<DriverLedgerDbContext> options, ITenantProvider tenantProvider)
            : base(options)
        {
            _tenantProvider = tenantProvider;
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<Role> Roles => Set<Role>();
        public DbSet<UserRole> UserRoles => Set<UserRole>();
        public DbSet<DriverProfile> DriverProfiles => Set<DriverProfile>();

        public DbSet<FileObject> FileObjects => Set<FileObject>();
        public DbSet<Receipt> Receipts => Set<Receipt>();
        public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Identity
            modelBuilder.Entity<User>(b =>
            {
                b.ToTable("Users");
                b.HasIndex(x => x.Email).IsUnique();
                b.Property(x => x.Email).HasMaxLength(256).IsRequired();
                b.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
                b.Property(x => x.Status).HasMaxLength(30).IsRequired();
            });

            modelBuilder.Entity<Role>(b =>
            {
                b.ToTable("Roles");
                b.HasIndex(x => x.Name).IsUnique();
                b.Property(x => x.Name).HasMaxLength(50).IsRequired();
            });

            modelBuilder.Entity<UserRole>(b =>
            {
                b.ToTable("UserRoles");
                b.HasKey(x => new { x.UserId, x.RoleId });
            });

            modelBuilder.Entity<DriverProfile>(b =>
            {
                b.ToTable("DriverProfiles");
                b.Property(x => x.Province).HasMaxLength(10);
                b.Property(x => x.PolicyJson).HasColumnType("nvarchar(max)");
                b.HasIndex(x => x.TenantId).IsUnique();
            });

            // Files
            modelBuilder.Entity<FileObject>(b =>
            {
                b.ToTable("FileObjects");
                b.HasIndex(x => new { x.TenantId, x.Sha256 });
                b.Property(x => x.BlobPath).HasMaxLength(400).IsRequired();
                b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
                b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
                b.Property(x => x.OriginalName).HasMaxLength(260).IsRequired();
                b.Property(x => x.Source).HasMaxLength(50).IsRequired();
            });

            // Receipts
            modelBuilder.Entity<Receipt>(b =>
            {
                b.ToTable("Receipts");
                b.HasIndex(x => new { x.TenantId, x.Status });
                b.Property(x => x.Status).HasMaxLength(30).IsRequired();
            });

            // Ops
            modelBuilder.Entity<ProcessingJob>(b =>
            {
                b.ToTable("ProcessingJobs");
                b.HasIndex(x => new { x.TenantId, x.JobType, x.DedupeKey }).IsUnique();
                b.Property(x => x.JobType).HasMaxLength(80).IsRequired();
                b.Property(x => x.DedupeKey).HasMaxLength(200).IsRequired();
                b.Property(x => x.Status).HasMaxLength(30).IsRequired();
            });

            // Global tenant filter (applies to any entity implementing ITenantScoped)
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
                {
                    // e => ((ITenantScoped)e).TenantId == _tenantProvider.TenantId.Value
                    var method = typeof(DriverLedgerDbContext)
                        .GetMethod(nameof(SetTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .MakeGenericMethod(entityType.ClrType);

                    method.Invoke(null, new object[] { modelBuilder, this });
                }
            }
        }

        private static void SetTenantFilter<TEntity>(ModelBuilder modelBuilder, DriverLedgerDbContext ctx)
            where TEntity : class, ITenantScoped
        {
            modelBuilder.Entity<TEntity>()
                .HasQueryFilter(e => ctx._tenantProvider.TenantId.HasValue && e.TenantId == ctx._tenantProvider.TenantId.Value);
        }
    }
}
