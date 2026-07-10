using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Networthy.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomeSourcesAndGoalReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedAnnualReturnPct",
                schema: "finance",
                table: "goals",
                type: "numeric(6,3)",
                precision: 6,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "income_sources",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Cadence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_income_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_income_sources_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_income_sources_AccountId",
                schema: "finance",
                table: "income_sources",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_income_sources_TenantId_Name",
                schema: "finance",
                table: "income_sources",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "income_sources",
                schema: "finance");

            migrationBuilder.DropColumn(
                name: "ExpectedAnnualReturnPct",
                schema: "finance",
                table: "goals");
        }
    }
}
