using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomRoleId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Permissions = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomRoles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_CustomRoleId",
                table: "Users",
                column: "CustomRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomRoles_Name",
                table: "CustomRoles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_CustomRoles_CustomRoleId",
                table: "Users",
                column: "CustomRoleId",
                principalTable: "CustomRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_CustomRoles_CustomRoleId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "CustomRoles");

            migrationBuilder.DropIndex(
                name: "IX_Users_CustomRoleId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CustomRoleId",
                table: "Users");
        }
    }
}
