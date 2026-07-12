using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Networthy.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStatementReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing households (a settings row predating this migration) inherit the documented
            // default: monthly statement reminders, on. New rows carry the same values from the
            // HouseholdSettings model defaults, so migrated and freshly-created rows agree.
            migrationBuilder.AddColumn<string>(
                name: "StatementReminderCadence",
                schema: "finance",
                table: "household_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "monthly");

            migrationBuilder.AddColumn<bool>(
                name: "StatementRemindersEnabled",
                schema: "finance",
                table: "household_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "statement_reminders",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_statement_reminders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_statement_reminders_TenantId_PeriodStart",
                schema: "finance",
                table: "statement_reminders",
                columns: new[] { "TenantId", "PeriodStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "statement_reminders",
                schema: "finance");

            migrationBuilder.DropColumn(
                name: "StatementReminderCadence",
                schema: "finance",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "StatementRemindersEnabled",
                schema: "finance",
                table: "household_settings");
        }
    }
}
