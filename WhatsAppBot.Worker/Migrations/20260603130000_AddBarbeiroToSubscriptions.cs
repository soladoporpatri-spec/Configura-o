using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddBarbeiroToSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── ClientSubscriptions: barbeiro atrelado à assinatura ───────────
            // NULL = sem restrição de barbeiro (compatível com assinaturas existentes)
            migrationBuilder.AddColumn<int>(
                name: "BarbeiroId",
                table: "ClientSubscriptions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BarbeiroNome",
                table: "ClientSubscriptions",
                type: "TEXT",
                nullable: true);

            // ── ConversationSessions: estado pendente do fluxo de assinatura ──
            migrationBuilder.AddColumn<int>(
                name: "SubscriptionPendingBarberId",
                table: "ConversationSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionPendingBarberNome",
                table: "ConversationSessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BarbeiroId",                    table: "ClientSubscriptions");
            migrationBuilder.DropColumn(name: "BarbeiroNome",                  table: "ClientSubscriptions");
            migrationBuilder.DropColumn(name: "SubscriptionPendingBarberId",   table: "ConversationSessions");
            migrationBuilder.DropColumn(name: "SubscriptionPendingBarberNome", table: "ConversationSessions");
        }
    }
}
