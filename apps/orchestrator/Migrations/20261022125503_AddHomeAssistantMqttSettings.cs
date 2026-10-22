using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddHomeAssistantMqttSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HomeAssistantEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MqttBaseTopic",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MqttDiscoveryPrefix",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MqttHost",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MqttPassword",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MqttPort",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "MqttUseTls",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MqttUsername",
                table: "Settings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeAssistantEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttBaseTopic",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttDiscoveryPrefix",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttHost",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttPassword",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttPort",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttUseTls",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MqttUsername",
                table: "Settings");
        }
    }
}
