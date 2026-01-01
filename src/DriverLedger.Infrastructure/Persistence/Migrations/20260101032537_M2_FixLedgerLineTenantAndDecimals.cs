using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_FixLedgerLineTenantAndDecimals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "EvidencePct",
                table: "SnapshotDetails",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "EstimatedPct",
                table: "SnapshotDetails",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "EvidencePct",
                table: "LedgerSnapshots",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "EstimatedPct",
                table: "LedgerSnapshots",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "LedgerLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // NEW: TenantId on LedgerSourceLinks
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // NEW: Backfill LedgerLines.TenantId from LedgerEntries
            migrationBuilder.Sql(@"
UPDATE l
SET l.TenantId = e.TenantId
FROM LedgerLines l
JOIN LedgerEntries e ON e.Id = l.LedgerEntryId;
");

            // NEW: Backfill LedgerSourceLinks.TenantId via LedgerLines -> LedgerEntries
            migrationBuilder.Sql(@"
UPDATE lsl
SET lsl.TenantId = e.TenantId
FROM LedgerSourceLinks lsl
JOIN LedgerLines ll ON ll.Id = lsl.LedgerLineId
JOIN LedgerEntries e ON e.Id = ll.LedgerEntryId;
");

            migrationBuilder.AlterColumn<decimal>(
                name: "DefaultBusinessUsePct",
                table: "DriverProfiles",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop LedgerSourceLinks.TenantId first
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "LedgerSourceLinks");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "LedgerLines");

            migrationBuilder.AlterColumn<decimal>(
                name: "EvidencePct",
                table: "SnapshotDetails",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "EstimatedPct",
                table: "SnapshotDetails",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "EvidencePct",
                table: "LedgerSnapshots",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "EstimatedPct",
                table: "LedgerSnapshots",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);

            migrationBuilder.AlterColumn<decimal>(
                name: "DefaultBusinessUsePct",
                table: "DriverProfiles",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);
        }
    }
}
