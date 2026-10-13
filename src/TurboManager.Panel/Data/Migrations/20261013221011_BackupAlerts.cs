using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboManager.Panel.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAt",
                table: "BackupPlans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastSuccessAt",
                table: "BackupPlans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupDays",
                table: "AlertSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);
        
            // Plans that existed before: count from now, and their last good run is their last run.
            migrationBuilder.Sql("UPDATE BackupPlans SET CreatedAt = CAST(strftime('%s', 'now') AS INTEGER) * 1000");
            migrationBuilder.Sql("UPDATE BackupPlans SET LastSuccessAt = LastRunAt WHERE LastResult = 'OK'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "BackupPlans");

            migrationBuilder.DropColumn(
                name: "LastSuccessAt",
                table: "BackupPlans");

            migrationBuilder.DropColumn(
                name: "BackupDays",
                table: "AlertSettings");
        }
    }
}
