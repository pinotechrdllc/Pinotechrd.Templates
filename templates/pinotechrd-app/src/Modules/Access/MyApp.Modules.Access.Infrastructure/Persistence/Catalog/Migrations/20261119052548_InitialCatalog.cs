using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "subscription_plans",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    monthly_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_plan_features",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plan_features", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_plan_features_subscription_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "catalog",
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscription_plan_modules",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plan_modules", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_plan_modules_subscription_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "catalog",
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "system_subscriptions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    tenant_name = table.Column<string>(type: "text", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    contact_email = table.Column<string>(type: "text", nullable: true),
                    contact_phone = table.Column<string>(type: "text", nullable: true),
                    activated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    suspended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_system_subscriptions_subscription_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "catalog",
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    db_schema = table.Column<string>(type: "text", nullable: true),
                    db_name = table.Column<string>(type: "text", nullable: true),
                    connection_string = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    provisioning_error = table.Column<string>(type: "text", nullable: true),
                    provisioning_attempts = table.Column<int>(type: "integer", nullable: false),
                    provisioning_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    provisioning_completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    suspended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    current_schema_version = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenants_system_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "catalog",
                        principalTable: "system_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_features_plan_id",
                schema: "catalog",
                table: "subscription_plan_features",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_modules_module_id",
                schema: "catalog",
                table: "subscription_plan_modules",
                column: "module_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_modules_plan_id",
                schema: "catalog",
                table: "subscription_plan_modules",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plans_code",
                schema: "catalog",
                table: "subscription_plans",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_subscriptions_plan_id",
                schema: "catalog",
                table: "system_subscriptions",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_identifier",
                schema: "catalog",
                table: "tenants",
                column: "identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_subscription_id",
                schema: "catalog",
                table: "tenants",
                column: "subscription_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscription_plan_features",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "subscription_plan_modules",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "system_subscriptions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "subscription_plans",
                schema: "catalog");
        }
    }
}
