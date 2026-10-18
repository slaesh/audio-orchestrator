using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddErrorMessageToSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "SpotifySources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "RadioStreams",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "SpotifySources");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "RadioStreams");
        }
    }
}
