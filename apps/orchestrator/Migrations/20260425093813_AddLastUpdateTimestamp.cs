using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AudioOrchestrator.Migrations
{
    /// <inheritdoc />
    public partial class AddLastUpdateTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastUpdateTimestamp",
                table: "SpotifySources",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpdateTimestamp",
                table: "SpotifySources");
        }
    }
}
