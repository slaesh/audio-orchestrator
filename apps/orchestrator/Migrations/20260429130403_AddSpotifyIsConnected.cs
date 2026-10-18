using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotifyIsConnected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsConnected",
                table: "SpotifySources",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsConnected",
                table: "SpotifySources");
        }
    }
}
