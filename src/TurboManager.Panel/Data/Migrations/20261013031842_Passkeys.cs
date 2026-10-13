using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class Passkeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PasskeyHandle",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Passkeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignCount = table.Column<uint>(type: "INTEGER", nullable: false),
                    RpId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passkeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Passkeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_CredentialId",
                table: "Passkeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Passkeys_UserId",
                table: "Passkeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Passkeys");

            migrationBuilder.DropColumn(
                name: "PasskeyHandle",
                table: "Users");
        }
    }
}
