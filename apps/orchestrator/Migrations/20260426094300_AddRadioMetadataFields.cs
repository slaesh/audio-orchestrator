using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddRadioMetadataFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentArtist",
                table: "RadioStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentCoverUrl",
                table: "RadioStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentTitle",
                table: "RadioStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataId",
                table: "RadioStreams",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataProvider",
                table: "RadioStreams",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentArtist",
                table: "RadioStreams");

            migrationBuilder.DropColumn(
                name: "CurrentCoverUrl",
                table: "RadioStreams");

            migrationBuilder.DropColumn(
                name: "CurrentTitle",
                table: "RadioStreams");

            migrationBuilder.DropColumn(
                name: "MetadataId",
                table: "RadioStreams");

            migrationBuilder.DropColumn(
                name: "MetadataProvider",
                table: "RadioStreams");
        }
    }
}
