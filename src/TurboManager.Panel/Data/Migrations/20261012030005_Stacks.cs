using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class Stacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipeId",
                table: "Sites",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeRendered",
                table: "Sites",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StackId",
                table: "Sites",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StackRole",
                table: "Sites",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeStateProtected",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StackId",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StackRole",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Stacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Preset = table.Column<string>(type: "TEXT", nullable: false),
                    SystemUser = table.Column<string>(type: "TEXT", nullable: false),
                    Domain = table.Column<string>(type: "TEXT", nullable: false),
                    TlsMode = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CloudflareDns = table.Column<bool>(type: "INTEGER", nullable: false),
                    Hosts = table.Column<string>(type: "TEXT", nullable: false),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    Overrides = table.Column<string>(type: "TEXT", nullable: false),
                    Sources = table.Column<string>(type: "TEXT", nullable: false),
                    DatabaseEngine = table.Column<string>(type: "TEXT", nullable: true),
                    AdminSetupLinkProtected = table.Column<string>(type: "TEXT", nullable: true),
                    InstallJobId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Stacks_Name",
                table: "Stacks",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Stacks");

            migrationBuilder.DropColumn(
                name: "RecipeId",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "RecipeRendered",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "StackId",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "StackRole",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "RecipeStateProtected",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "StackId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "StackRole",
                table: "Apps");
        }
    }
}
