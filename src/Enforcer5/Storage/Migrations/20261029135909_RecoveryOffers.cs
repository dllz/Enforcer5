using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Enforcer5.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryOffers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recovery_offers",
                columns: table => new
                {
                    offer_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    batch = table.Column<string>(type: "text", nullable: false),
                    period_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "held"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    edition = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_account_id = table.Column<long>(type: "bigint", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    copies = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_offers", x => x.offer_id);
                    table.CheckConstraint("recovery_offers_status_check", "status IN ('held', 'released', 'sent', 'applied', 'declined', 'expired')");
                    table.ForeignKey(
                        name: "FK_recovery_offers_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "group_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recovery_offers_telegram_accounts_decided_by_account_id",
                        column: x => x.decided_by_account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "recovery_items",
                columns: table => new
                {
                    offer_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    selected = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_items", x => new { x.offer_id, x.position });
                    table.CheckConstraint("recovery_items_outcome_check", "outcome IS NULL OR outcome IN ('applied', 'partly', 'skipped', 'unticked', 'declined', 'expired')");
                    table.ForeignKey(
                        name: "FK_recovery_items_recovery_offers_offer_id",
                        column: x => x.offer_id,
                        principalTable: "recovery_offers",
                        principalColumn: "offer_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_recovery_items_telegram_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "telegram_accounts",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_items_account_id",
                table: "recovery_items",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_offers_decided_by_account_id",
                table: "recovery_offers",
                column: "decided_by_account_id");

            migrationBuilder.CreateIndex(
                name: "recovery_offers_group_batch_idx",
                table: "recovery_offers",
                columns: new[] { "group_id", "batch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "recovery_offers_status_idx",
                table: "recovery_offers",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_items");

            migrationBuilder.DropTable(
                name: "recovery_offers");
        }
    }
}
