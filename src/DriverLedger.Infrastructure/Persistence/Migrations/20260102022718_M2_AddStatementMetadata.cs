using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class M2_AddStatementMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
                table: "Statements",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StatementTotalAmount",
                table: "Statements",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Statements",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorName",
                table: "Statements",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "Statements");

            migrationBuilder.DropColumn(
                name: "StatementTotalAmount",
                table: "Statements");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Statements");

            migrationBuilder.DropColumn(
                name: "VendorName",
                table: "Statements");
        }
    }
}
