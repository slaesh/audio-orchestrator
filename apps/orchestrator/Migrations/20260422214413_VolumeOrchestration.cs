using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class VolumeOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename RoomNames to Rooms
            migrationBuilder.RenameTable(
                name: "RoomNames",
                newName: "Rooms");

            migrationBuilder.AddColumn<bool>(
                name: "IsMuted",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MasterVolume",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100); // Default to 100

            migrationBuilder.AddColumn<int>(
                name: "MixVolume",
                table: "Rooms",
                type: "INTEGER",
                nullable: false,
                defaultValue: 100); // Default to 100
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MixVolume",
                table: "Rooms");

            migrationBuilder.DropColumn(
                name: "IsMuted",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "MasterVolume",
                table: "Groups");

            migrationBuilder.RenameTable(
                name: "Rooms",
                newName: "RoomNames");
        }
    }
}
