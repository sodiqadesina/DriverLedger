using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixConfidenceAndSourceLinkIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_ReceiptId_StatementLineId_FileObjectId",
                table: "LedgerSourceLinks");

            migrationBuilder.AlterColumn<decimal>(
                name: "Confidence",
                table: "ReceiptExtractions",
                type: "decimal(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_ReceiptId_FileObjectId",
                table: "LedgerSourceLinks",
                columns: new[] { "LedgerLineId", "ReceiptId", "FileObjectId" },
                unique: true,
                filter: "[ReceiptId] IS NOT NULL AND [FileObjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_StatementLineId_FileObjectId",
                table: "LedgerSourceLinks",
                columns: new[] { "LedgerLineId", "StatementLineId", "FileObjectId" },
                unique: true,
                filter: "[StatementLineId] IS NOT NULL AND [FileObjectId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_ReceiptId_FileObjectId",
                table: "LedgerSourceLinks");

            migrationBuilder.DropIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_StatementLineId_FileObjectId",
                table: "LedgerSourceLinks");

            migrationBuilder.AlterColumn<decimal>(
                name: "Confidence",
                table: "ReceiptExtractions",
                type: "decimal(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "decimal(5,4)",
                oldPrecision: 5,
                oldScale: 4);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_ReceiptId_StatementLineId_FileObjectId",
                table: "LedgerSourceLinks",
                columns: new[] { "LedgerLineId", "ReceiptId", "StatementLineId", "FileObjectId" },
                unique: true,
                filter: "[ReceiptId] IS NOT NULL AND [StatementLineId] IS NOT NULL AND [FileObjectId] IS NOT NULL");
        }
    }
}
