using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Networthy.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportDetectedCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetectedCurrency",
                schema: "finance",
                table: "statement_import_batches",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetectedCurrency",
                schema: "finance",
                table: "statement_import_batches");
        }
    }
}
