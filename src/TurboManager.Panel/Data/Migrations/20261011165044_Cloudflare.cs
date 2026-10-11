using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class Cloudflare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CloudflareDns",
                table: "Sites",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CloudflareProxied",
                table: "Sites",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "System",
                table: "IpLists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CloudflareAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenProtected = table.Column<string>(type: "TEXT", nullable: false),
                    TokenId = table.Column<string>(type: "TEXT", nullable: true),
                    TokenStatus = table.Column<string>(type: "TEXT", nullable: true),
                    TokenExpiresOn = table.Column<long>(type: "INTEGER", nullable: true),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ZonesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    RealIpEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IpRangesUpdatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudflareAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PanelAddresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Domain = table.Column<string>(type: "TEXT", nullable: false),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: true),
                    VerificationCode = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SetupClosedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PanelAddresses", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudflareAccounts");

            migrationBuilder.DropTable(
                name: "PanelAddresses");

            migrationBuilder.DropColumn(
                name: "CloudflareDns",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "CloudflareProxied",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "System",
                table: "IpLists");
        }
    }
}
