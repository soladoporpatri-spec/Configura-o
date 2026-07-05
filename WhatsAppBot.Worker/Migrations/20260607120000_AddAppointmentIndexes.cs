using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FIX 3: índices compostos em Appointments para acelerar as duas queries mais frequentes
            // do sistema de lembretes (CheckAndSendRemindersAsync e CheckAndSendRetentionAsync).
            //
            // (StoreId, PhoneNumber): otimiza lookup por cliente no fluxo de retenção,
            //   usado com GROUP BY + FirstOrDefaultAsync após a migração do full table scan.
            //   Também acelera AnyAsync(hasFuture) que filtra por telefone.
            //
            // (StoreId, DateTime): otimiza a query de lembretes futuros (Where a.DateTime > now)
            //   e range queries de data no painel de agendamentos.
            //
            // StoreId à esquerda porque o HasQueryFilter sempre inclui StoreId na WHERE clause;
            // sem ele no índice, o SQLite faz full-scan independente do segundo campo.
            migrationBuilder.CreateIndex(
                name: "IX_Appointments_StoreId_Phone",
                table: "Appointments",
                columns: new[] { "StoreId", "PhoneNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_StoreId_DateTime",
                table: "Appointments",
                columns: new[] { "StoreId", "DateTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_StoreId_Phone",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_StoreId_DateTime",
                table: "Appointments");
        }
    }
}
