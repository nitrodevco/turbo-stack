using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class Deployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SystemUser = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryName = table.Column<string>(type: "TEXT", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", nullable: false),
                    Auth = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: true),
                    KeyFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    Submodules = table.Column<bool>(type: "INTEGER", nullable: false),
                    WebhookSecretProtected = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastCommit = table.Column<string>(type: "TEXT", nullable: true),
                    LastCommitMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SubmoduleKeys = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Event = table.Column<string>(type: "TEXT", nullable: false),
                    Ref = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Apps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SystemUser = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    BuildScript = table.Column<string>(type: "TEXT", nullable: false),
                    Runtimes = table.Column<string>(type: "TEXT", nullable: false),
                    EnvironmentProtected = table.Column<string>(type: "TEXT", nullable: false),
                    StaticOutput = table.Column<string>(type: "TEXT", nullable: true),
                    ServiceCommand = table.Column<string>(type: "TEXT", nullable: true),
                    ServicePort = table.Column<int>(type: "INTEGER", nullable: true),
                    HealthPath = table.Column<string>(type: "TEXT", nullable: true),
                    MemoryMaxMb = table.Column<int>(type: "INTEGER", nullable: false),
                    StopTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepReleases = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoDeploy = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeployPaths = table.Column<string>(type: "TEXT", nullable: false),
                    PurgeCloudflare = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentRelease = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SharedPaths = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Apps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Apps_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Commit = table.Column<string>(type: "TEXT", nullable: true),
                    CommitMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CommitAuthor = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    TriggeredBy = table.Column<string>(type: "TEXT", nullable: true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Releases_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Name",
                table: "Apps",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Apps_RepositoryId",
                table: "Apps",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_AppId_Name",
                table: "Releases",
                columns: new[] { "AppId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_SystemUser_Name",
                table: "Repositories",
                columns: new[] { "SystemUser", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_ReceivedAt",
                table: "WebhookDeliveries",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Releases");

            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropTable(
                name: "Apps");

            migrationBuilder.DropTable(
                name: "Repositories");
        }
    }
}
