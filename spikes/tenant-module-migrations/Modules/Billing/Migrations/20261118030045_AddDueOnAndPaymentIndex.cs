using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantModuleMigrations.Modules.Billing.Migrations
{
    /// <inheritdoc />
    public partial class AddDueOnAndPaymentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "due_on",
                table: "billing_invoices",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_payments_invoice_id",
                table: "billing_payments",
                column: "invoice_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_billing_payments_invoice_id",
                table: "billing_payments");

            migrationBuilder.DropColumn(
                name: "due_on",
                table: "billing_invoices");
        }
    }
}
