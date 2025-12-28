using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M1_CoreFinancialTruth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PostedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PostedByType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PeriodKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CalculatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AuthorityScore = table.Column<int>(type: "int", nullable: false),
                    EvidencePct = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    EstimatedPct = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptExtractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NormalizedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ExtractedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptExtractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiptExtractions_Receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HoldReason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QuestionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResolutionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceiptReviews_Receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LedgerLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    GstHst = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DeductiblePct = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Memo = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AccountCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerLines_LedgerEntries_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotDetails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MetricKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    EvidencePct = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    EstimatedPct = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapshotDetails_LedgerSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "LedgerSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LedgerSourceLinks",
                columns: table => new
                {
                    LedgerLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatementLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerSourceLinks", x => new { x.LedgerLineId, x.ReceiptId, x.StatementLineId, x.FileObjectId });
                    table.ForeignKey(
                        name: "FK_LedgerSourceLinks_LedgerLines_LedgerLineId",
                        column: x => x.LedgerLineId,
                        principalTable: "LedgerLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_TenantId_EntryDate",
                table: "LedgerEntries",
                columns: new[] { "TenantId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_TenantId_SourceType_SourceId",
                table: "LedgerEntries",
                columns: new[] { "TenantId", "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerLines_LedgerEntryId",
                table: "LedgerLines",
                column: "LedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSnapshots_TenantId_PeriodType_PeriodKey",
                table: "LedgerSnapshots",
                columns: new[] { "TenantId", "PeriodType", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_FileObjectId",
                table: "LedgerSourceLinks",
                column: "FileObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_ReceiptId",
                table: "LedgerSourceLinks",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_StatementLineId",
                table: "LedgerSourceLinks",
                column: "StatementLineId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_CreatedAt",
                table: "Notifications",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptExtractions_ReceiptId",
                table: "ReceiptExtractions",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptExtractions_TenantId_ReceiptId_ExtractedAt",
                table: "ReceiptExtractions",
                columns: new[] { "TenantId", "ReceiptId", "ExtractedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptReviews_ReceiptId",
                table: "ReceiptReviews",
                column: "ReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptReviews_TenantId_ReceiptId",
                table: "ReceiptReviews",
                columns: new[] { "TenantId", "ReceiptId" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotDetails_SnapshotId_MetricKey",
                table: "SnapshotDetails",
                columns: new[] { "SnapshotId", "MetricKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerSourceLinks");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "ReceiptExtractions");

            migrationBuilder.DropTable(
                name: "ReceiptReviews");

            migrationBuilder.DropTable(
                name: "SnapshotDetails");

            migrationBuilder.DropTable(
                name: "LedgerLines");

            migrationBuilder.DropTable(
                name: "LedgerSnapshots");

            migrationBuilder.DropTable(
                name: "LedgerEntries");
        }
    }
}
