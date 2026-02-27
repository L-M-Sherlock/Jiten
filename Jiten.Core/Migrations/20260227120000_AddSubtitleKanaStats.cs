using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSubtitleKanaStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SubtitleDurationMs",
                schema: "jiten",
                table: "Decks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SubtitleKanaCount",
                schema: "jiten",
                table: "Decks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtitleDurationMs",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "SubtitleKanaCount",
                schema: "jiten",
                table: "Decks");
        }
    }
}
