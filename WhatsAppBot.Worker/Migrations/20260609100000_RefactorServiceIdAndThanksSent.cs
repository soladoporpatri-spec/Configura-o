using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <inheritdoc />
    public partial class RefactorServiceIdAndThanksSent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Renomeia Appointment.Servico → ServiceId ─────────────────────────
            // O tipo subjacente já era INTEGER (enum armazenado como int) — sem perda de dados.
            migrationBuilder.RenameColumn(
                name: "Servico",
                table: "Appointments",
                newName: "ServiceId");

            // ── Adiciona Appointment.ThanksSent ──────────────────────────────────
            // defaultValue: false — todos os agendamentos existentes iniciam sem agradecimento enviado.
            migrationBuilder.AddColumn<bool>(
                name: "ThanksSent",
                table: "Appointments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // ── Renomeia ConversationSession.SelectedService → SelectedServiceId ─
            // O tipo subjacente já era INTEGER NULL (enum? armazenado como int?) — sem perda de dados.
            migrationBuilder.RenameColumn(
                name: "SelectedService",
                table: "ConversationSessions",
                newName: "SelectedServiceId");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ServiceId",
                table: "Appointments",
                newName: "Servico");

            migrationBuilder.DropColumn(
                name: "ThanksSent",
                table: "Appointments");

            migrationBuilder.RenameColumn(
                name: "SelectedServiceId",
                table: "ConversationSessions",
                newName: "SelectedService");
        }
    }
}
