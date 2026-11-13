using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace rabotyagi.DataAccess.Migrations
{
    /// <inheritdoc />
    public partial class OrderPriceAgreement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceAcceptedAt",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAmount",
                table: "Orders",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PriceExecutorId",
                table: "Orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceScope",
                table: "Orders",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceStatus",
                table: "Orders",
                type: "text",
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<decimal>(
                name: "PriceAmount",
                table: "OrderHistoryEvents",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceAcceptedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceExecutorId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceScope",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceStatus",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PriceAmount",
                table: "OrderHistoryEvents");
        }
    }
}
