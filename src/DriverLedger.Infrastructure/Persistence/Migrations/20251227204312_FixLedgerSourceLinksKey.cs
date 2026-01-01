using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriverLedger.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixLedgerSourceLinksKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_LedgerSourceLinks",
                table: "LedgerSourceLinks");

            migrationBuilder.AlterColumn<Guid>(
                name: "FileObjectId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "StatementLineId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReceiptId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_LedgerSourceLinks",
                table: "LedgerSourceLinks",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_ReceiptId_StatementLineId_FileObjectId",
                table: "LedgerSourceLinks",
                columns: new[] { "LedgerLineId", "ReceiptId", "StatementLineId", "FileObjectId" },
                unique: true,
                filter: "[ReceiptId] IS NOT NULL AND [StatementLineId] IS NOT NULL AND [FileObjectId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_LedgerSourceLinks",
                table: "LedgerSourceLinks");

            migrationBuilder.DropIndex(
                name: "IX_LedgerSourceLinks_LedgerLineId_ReceiptId_StatementLineId_FileObjectId",
                table: "LedgerSourceLinks");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "LedgerSourceLinks");

            migrationBuilder.AlterColumn<Guid>(
                name: "StatementLineId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ReceiptId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FileObjectId",
                table: "LedgerSourceLinks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_LedgerSourceLinks",
                table: "LedgerSourceLinks",
                columns: new[] { "LedgerLineId", "ReceiptId", "StatementLineId", "FileObjectId" });
        }
    }
}
