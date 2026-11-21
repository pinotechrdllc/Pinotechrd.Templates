using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyApp.Modules.Access.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenPurgeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_families_absolute_expires_at",
                table: "refresh_token_families",
                column: "absolute_expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_token_families_revoked_at",
                table: "refresh_token_families",
                column: "revoked_at",
                filter: "revoked_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_refresh_token_families_absolute_expires_at",
                table: "refresh_token_families");

            migrationBuilder.DropIndex(
                name: "ix_refresh_token_families_revoked_at",
                table: "refresh_token_families");
        }
    }
}
