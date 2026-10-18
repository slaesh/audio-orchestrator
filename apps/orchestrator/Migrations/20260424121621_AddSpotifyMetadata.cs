using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotifyMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "InitialVolume",
                table: "SpotifySources",
                newName: "MaxVolume");

            migrationBuilder.AddColumn<string>(
                name: "CurrentAlbum",
                table: "SpotifySources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentArtist",
                table: "SpotifySources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentCoverUrl",
                table: "SpotifySources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentDurationMs",
                table: "SpotifySources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentTitle",
                table: "SpotifySources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentTrackId",
                table: "SpotifySources",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentAlbum",
                table: "SpotifySources");

            migrationBuilder.DropColumn(
                name: "CurrentArtist",
                table: "SpotifySources");

            migrationBuilder.DropColumn(
                name: "CurrentCoverUrl",
                table: "SpotifySources");

            migrationBuilder.DropColumn(
                name: "CurrentDurationMs",
                table: "SpotifySources");

            migrationBuilder.DropColumn(
                name: "CurrentTitle",
                table: "SpotifySources");

            migrationBuilder.DropColumn(
                name: "CurrentTrackId",
                table: "SpotifySources");

            migrationBuilder.RenameColumn(
                name: "MaxVolume",
                table: "SpotifySources",
                newName: "InitialVolume");
        }
    }
}
