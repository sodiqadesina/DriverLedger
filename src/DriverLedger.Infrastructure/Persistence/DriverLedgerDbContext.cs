using DriverLedger.Application.Common;
using DriverLedger.Domain.Auditing;
using DriverLedger.Domain.Common;
using DriverLedger.Domain.Drivers;
using DriverLedger.Domain.Files;
using DriverLedger.Domain.Identity;
using DriverLedger.Domain.Ledger;
using DriverLedger.Domain.Notifications;
using DriverLedger.Domain.Ops;
using DriverLedger.Domain.Receipts;
using DriverLedger.Domain.Receipts.Extraction;
using DriverLedger.Domain.Receipts.Review;
using DriverLedger.Domain.Statements.Snapshots;

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
        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

        public DbSet<ReceiptExtraction> ReceiptExtractions => Set<ReceiptExtraction>();
        public DbSet<ReceiptReview> ReceiptReviews => Set<ReceiptReview>();

        public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
        public DbSet<LedgerLine> LedgerLines => Set<LedgerLine>();
        public DbSet<LedgerSnapshot> LedgerSnapshots => Set<LedgerSnapshot>();
        public DbSet<SnapshotDetail> SnapshotDetails => Set<SnapshotDetail>();

        public DbSet<Notification> Notifications => Set<Notification>();


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
                b.Property(x => x.DefaultBusinessUsePct);
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

            // Auditing
            modelBuilder.Entity<AuditEvent>(b =>
            {
                b.ToTable("AuditEvents");
                b.HasIndex(x => new { x.TenantId, x.OccurredAt });
                b.Property(x => x.ActorUserId).HasMaxLength(100).IsRequired();
                b.Property(x => x.Action).HasMaxLength(200).IsRequired();
                b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
                b.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
                b.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
                b.Property(x => x.MetadataJson).HasColumnType("nvarchar(max)");
            });

            // Receipt Extraction & Review
            modelBuilder.Entity<ReceiptExtraction>(b =>
            {
                b.HasIndex(x => new { x.TenantId, x.ReceiptId, x.ExtractedAt });
                b.Property(x => x.RawJson).HasColumnType("nvarchar(max)");
                b.Property(x => x.NormalizedFieldsJson).HasColumnType("nvarchar(max)");
            });

            // Receipt Review
            modelBuilder.Entity<ReceiptReview>(b =>
            {
                b.HasIndex(x => new { x.TenantId, x.ReceiptId });
                b.Property(x => x.QuestionsJson).HasColumnType("nvarchar(max)");
                b.Property(x => x.ResolutionJson).HasColumnType("nvarchar(max)");
            });

            // Ledger
            modelBuilder.Entity<LedgerEntry>(b =>
            {
                b.HasIndex(x => new { x.TenantId, x.EntryDate });
                b.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId }).IsUnique();
                b.Property(x => x.SourceType).HasMaxLength(50);
                b.Property(x => x.PostedByType).HasMaxLength(20);
                b.Property(x => x.CorrelationId).HasMaxLength(128);
            });

            modelBuilder.Entity<LedgerLine>(b =>
            {
                b.HasIndex(x => x.LedgerEntryId);
                b.Property(x => x.Memo).HasMaxLength(400);
                b.Property(x => x.AccountCode).HasMaxLength(64);
            });

            modelBuilder.Entity<LedgerSourceLink>(b =>
            {
                b.ToTable("LedgerSourceLinks");

                // Surrogate PK
                b.HasKey(x => x.Id);

                b.HasOne(x => x.LedgerLine)
                    .WithMany(x => x.SourceLinks)
                    .HasForeignKey(x => x.LedgerLineId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Optional FKs
                b.Property(x => x.ReceiptId).IsRequired(false);
                b.Property(x => x.StatementLineId).IsRequired(false);
                b.Property(x => x.FileObjectId).IsRequired(false);

                // Prevent duplicates (for M1: you’ll mostly use ReceiptId+FileObjectId)
                b.HasIndex(x => new { x.LedgerLineId, x.ReceiptId, x.StatementLineId, x.FileObjectId })
                    .IsUnique();

                b.HasIndex(x => x.ReceiptId);
                b.HasIndex(x => x.StatementLineId);
                b.HasIndex(x => x.FileObjectId);
            });



            modelBuilder.Entity<LedgerSnapshot>(b =>
            {
                b.HasIndex(x => new { x.TenantId, x.PeriodType, x.PeriodKey }).IsUnique();
                b.Property(x => x.TotalsJson).HasColumnType("nvarchar(max)");
            });

            modelBuilder.Entity<SnapshotDetail>(b =>
            {
                b.HasIndex(x => new { x.SnapshotId, x.MetricKey }).IsUnique();
                b.Property(x => x.MetricKey).HasMaxLength(64);
            });

            // Notifications
            modelBuilder.Entity<Notification>(b =>
            {
                b.HasIndex(x => new { x.TenantId, x.CreatedAt });
                b.Property(x => x.DataJson).HasColumnType("nvarchar(max)");
                b.Property(x => x.Type).HasMaxLength(64);
                b.Property(x => x.Severity).HasMaxLength(16);
                b.Property(x => x.Status).HasMaxLength(16);
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

        // Helper method to set the tenant filter
        private static void SetTenantFilter<TEntity>(ModelBuilder modelBuilder, DriverLedgerDbContext ctx)
            where TEntity : class, ITenantScoped
        {
            modelBuilder.Entity<TEntity>()
                .HasQueryFilter(e => ctx._tenantProvider.TenantId.HasValue && e.TenantId == ctx._tenantProvider.TenantId.Value);
        }
    }
}
