using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CartService.DAL.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestSessionToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_carts_UserId",
                table: "carts");

            migrationBuilder.AddColumn<Guid>(
                name: "GuestSessionToken",
                table: "carts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Carts_GuestSessionToken",
                table: "carts",
                column: "GuestSessionToken",
                unique: true,
                filter: "\"GuestSessionToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Carts_GuestSessionToken_UpdatedAt",
                table: "carts",
                column: "UpdatedAt",
                filter: "\"GuestSessionToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_carts_UserId",
                table: "carts",
                column: "UserId",
                unique: true,
                filter: "\"GuestSessionToken\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Carts_GuestSessionToken",
                table: "carts");

            migrationBuilder.DropIndex(
                name: "IX_Carts_GuestSessionToken_UpdatedAt",
                table: "carts");

            migrationBuilder.DropIndex(
                name: "IX_carts_UserId",
                table: "carts");

            migrationBuilder.DropColumn(
                name: "GuestSessionToken",
                table: "carts");

            migrationBuilder.CreateIndex(
                name: "IX_carts_UserId",
                table: "carts",
                column: "UserId",
                unique: true);
        }
    }
}
