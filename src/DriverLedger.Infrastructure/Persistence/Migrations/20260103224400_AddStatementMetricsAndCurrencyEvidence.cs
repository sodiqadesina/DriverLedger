using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStatementMetricsAndCurrencyEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- Remove legacy columns (old model) ----
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Statements");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "StatementLines");

            // ---- Statements: add statement-level currency + evidence ----
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "Statements",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrencyEvidence",
                table: "Statements",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inferred");

            // ---- StatementLines: add new money + metric model ----

            // Currency
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "StatementLines",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrencyEvidence",
                table: "StatementLines",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inferred");

            // Classification evidence
            migrationBuilder.AddColumn<string>(
                name: "ClassificationEvidence",
                table: "StatementLines",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inferred");

            // Money vs Metric split
            migrationBuilder.AddColumn<bool>(
                name: "IsMetric",
                table: "StatementLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MoneyAmount",
                table: "StatementLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MetricValue",
                table: "StatementLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetricKey",
                table: "StatementLines",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "StatementLines",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ---- Remove new columns ----
            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "Statements");

            migrationBuilder.DropColumn(
                name: "CurrencyEvidence",
                table: "Statements");

            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "CurrencyEvidence",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "ClassificationEvidence",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "IsMetric",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "MoneyAmount",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "MetricValue",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "MetricKey",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "StatementLines");

            // ---- Restore legacy columns ----
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Statements",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "StatementLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "StatementLines",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: true);
        }
    }
}
