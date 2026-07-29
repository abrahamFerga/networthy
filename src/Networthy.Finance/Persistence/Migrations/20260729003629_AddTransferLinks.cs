using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Networthy.Finance.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TransferGroupId",
                schema: "finance",
                table: "transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_transactions_TransferGroupId",
                schema: "finance",
                table: "transactions",
                column: "TransferGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transactions_TransferGroupId",
                schema: "finance",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "TransferGroupId",
                schema: "finance",
                table: "transactions");
        }
    }
}
