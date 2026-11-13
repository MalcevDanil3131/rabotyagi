using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace rabotyagi.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class ExecutorReassignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PreviousExecutorId",
                table: "OrderHistoryEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousExecutorNameSnapshot",
                table: "OrderHistoryEvents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousExecutorId",
                table: "OrderHistoryEvents");

            migrationBuilder.DropColumn(
                name: "PreviousExecutorNameSnapshot",
                table: "OrderHistoryEvents");
        }
    }
}
