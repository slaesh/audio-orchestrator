using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class RemoveObsoleteRadioMetadataFields业务 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataId",
                table: "RadioStreams");

            migrationBuilder.DropColumn(
                name: "MetadataProvider",
                table: "RadioStreams");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
    }
}
