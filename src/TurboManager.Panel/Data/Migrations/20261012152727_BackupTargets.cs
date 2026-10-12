using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    User = table.Column<string>(type: "TEXT", nullable: false),
                    AccessKeyId = table.Column<string>(type: "TEXT", nullable: false),
                    SecretProtected = table.Column<string>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    HostKey = table.Column<string>(type: "TEXT", nullable: true),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Databases = table.Column<string>(type: "TEXT", nullable: false),
                    Folders = table.Column<string>(type: "TEXT", nullable: false),
                    PanelState = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeOfDay = table.Column<string>(type: "TEXT", nullable: false),
                    KeepDaily = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepWeekly = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepMonthly = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true),
                    LastSnapshot = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupPlans_BackupTargets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "BackupTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlans_Name",
                table: "BackupPlans",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupPlans_TargetId",
                table: "BackupPlans",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupTargets_Name",
                table: "BackupTargets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupPlans");

            migrationBuilder.DropTable(
                name: "BackupTargets");
        }
    }
}
