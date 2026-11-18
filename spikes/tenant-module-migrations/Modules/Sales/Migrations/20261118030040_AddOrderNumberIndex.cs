using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantModuleMigrations.Modules.Sales.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderNumberIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_number",
                table: "sales_orders",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sales_orders_number",
                table: "sales_orders");
        }
    }
}
