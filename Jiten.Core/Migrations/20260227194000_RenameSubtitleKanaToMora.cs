using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class RenameSubtitleKanaToMora : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SubtitleKanaCount",
                schema: "jiten",
                table: "Decks",
                newName: "SubtitleMoraCount");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SubtitleMoraCount",
                schema: "jiten",
                table: "Decks",
                newName: "SubtitleKanaCount");
        }
    }
}
