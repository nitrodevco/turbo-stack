using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class Recipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Ports",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RecipeId",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeInputs",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ServiceDirectory",
                table: "Apps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ports",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RecipeId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RecipeInputs",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ServiceDirectory",
                table: "Apps");
        }
    }
}
