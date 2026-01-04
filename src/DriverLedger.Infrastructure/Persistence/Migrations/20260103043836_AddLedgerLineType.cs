using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerLineType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LineType",
                table: "LedgerLines",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Fee");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LineType",
                table: "LedgerLines");
        }
    }
}
