using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <summary>
    /// Adiciona suporte a lava-jato avançado:
    /// 1. Tabela ClientVehicles — histórico de veículos por cliente por loja.
    /// 2. Appointments.VehicleInfo — campo dedicado para placa/modelo (distinto de Notes).
    /// 3. Appointments.IsWalkIn — flag de atendimento imediato (sem hora marcada).
    /// 4. ConversationSessions.IsWalkInMode — flag de sessão para o fluxo walk-in.
    /// </summary>
    public partial class AddVehicleCrmAndWalkIn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Tabela ClientVehicles ──────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "ClientVehicles",
                columns: table => new
                {
                    Id          = table.Column<int>(type: "INTEGER", nullable: false)
                                       .Annotation("Sqlite:Autoincrement", true),
                    StoreId     = table.Column<int>(type: "INTEGER", nullable: false),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Plate       = table.Column<string>(type: "TEXT", nullable: false),
                    Model       = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsed    = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientVehicles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientVehicles_StoreId_Phone",
                table: "ClientVehicles",
                columns: ["StoreId", "PhoneNumber"]);

            // ── 2. Appointments.VehicleInfo ───────────────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "VehicleInfo",
                table: "Appointments",
                type: "TEXT",
                nullable: true);

            // ── 3. Appointments.IsWalkIn ──────────────────────────────────────────
            migrationBuilder.AddColumn<bool>(
                name: "IsWalkIn",
                table: "Appointments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // ── 4. ConversationSessions.IsWalkInMode ──────────────────────────────
            migrationBuilder.AddColumn<bool>(
                name: "IsWalkInMode",
                table: "ConversationSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ClientVehicles");

            migrationBuilder.DropColumn(name: "VehicleInfo",    table: "Appointments");
            migrationBuilder.DropColumn(name: "IsWalkIn",       table: "Appointments");
            migrationBuilder.DropColumn(name: "IsWalkInMode",   table: "ConversationSessions");
        }
    }
}
