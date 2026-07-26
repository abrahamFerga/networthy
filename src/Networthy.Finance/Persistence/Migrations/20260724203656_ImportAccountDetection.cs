using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Networthy.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportAccountDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "finance",
                table: "statement_import_batches",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12);

            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId",
                schema: "finance",
                table: "statement_import_batches",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "DetectedAccountMask",
                schema: "finance",
                table: "statement_import_batches",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedInstitution",
                schema: "finance",
                table: "statement_import_batches",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LinesFrom",
                schema: "finance",
                table: "statement_import_batches",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LinesTo",
                schema: "finance",
                table: "statement_import_batches",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectedAccountMask",
                schema: "finance",
                table: "statement_import_batches");

            migrationBuilder.DropColumn(
                name: "DetectedInstitution",
                schema: "finance",
                table: "statement_import_batches");

            migrationBuilder.DropColumn(
                name: "LinesFrom",
                schema: "finance",
                table: "statement_import_batches");

            migrationBuilder.DropColumn(
                name: "LinesTo",
                schema: "finance",
                table: "statement_import_batches");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                schema: "finance",
                table: "statement_import_batches",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId",
                schema: "finance",
                table: "statement_import_batches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
