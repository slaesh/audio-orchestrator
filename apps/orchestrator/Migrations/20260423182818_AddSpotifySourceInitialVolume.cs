using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotifySourceInitialVolume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InitialVolume",
                table: "SpotifySources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitialVolume",
                table: "SpotifySources");
        }
    }
}
