using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddRadioPluginCurrentVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentVersion",
                table: "RadioPlugins",
                type: "TEXT",
                nullable: false,
                defaultValue: "0.1.0");

            migrationBuilder.Sql("UPDATE RadioPlugins SET CurrentVersion = '0.1.0' WHERE CurrentVersion IS NULL OR CurrentVersion = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentVersion",
                table: "RadioPlugins");
        }
    }
}
