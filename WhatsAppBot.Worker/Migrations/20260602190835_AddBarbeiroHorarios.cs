using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddBarbeiroHorarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_DateTime",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_PhoneNumber",
                table: "Appointments");

            migrationBuilder.RenameColumn(
                name: "BarbeiroId",
                table: "Appointments",
                newName: "StoreId");

            migrationBuilder.AddColumn<bool>(
                name: "Ativo",
                table: "Barbeiros",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BlockedSlotsJson",
                table: "Barbeiros",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Cor",
                table: "Barbeiros",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CustomHoursJson",
                table: "Barbeiros",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "LunchEnd",
                table: "Barbeiros",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "LunchStart",
                table: "Barbeiros",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StoreId",
                table: "Barbeiros",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WorkEnd",
                table: "Barbeiros",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WorkStart",
                table: "Barbeiros",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            migrationBuilder.AddColumn<string>(
                name: "WorkingDays",
                table: "Barbeiros",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BarberId",
                table: "Appointments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BarberName",
                table: "Appointments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Appointments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetentionReminderSent",
                table: "Appointments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    User = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BarbeiroHorarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BarbeiroId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    DiaSemana = table.Column<int>(type: "INTEGER", nullable: false),
                    Folga = table.Column<bool>(type: "INTEGER", nullable: false),
                    Entrada = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    Saida = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    InicioAlmoco = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    FimAlmoco = table.Column<TimeSpan>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarbeiroHorarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientPhone = table.Column<string>(type: "TEXT", nullable: false),
                    ClientName = table.Column<string>(type: "TEXT", nullable: false),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanNome = table.Column<string>(type: "TEXT", nullable: false),
                    PlanPreco = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreditosTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    CreditosUsados = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConversationSessions",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedService = table.Column<int>(type: "INTEGER", nullable: true),
                    SelectedVehicle = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedBarberId = table.Column<int>(type: "INTEGER", nullable: true),
                    SelectedBarberName = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastInteraction = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPollId = table.Column<string>(type: "TEXT", nullable: true),
                    TimeOffset = table.Column<int>(type: "INTEGER", nullable: false),
                    InvalidResponseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SubscriptionPendingPlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingCancelAppointmentId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationSessions", x => new { x.Phone, x.StoreId });
                });

            migrationBuilder.CreateTable(
                name: "Servicos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nome = table.Column<string>(type: "TEXT", nullable: false),
                    DuracaoMinutos = table.Column<int>(type: "INTEGER", nullable: false),
                    Preco = table.Column<decimal>(type: "TEXT", nullable: false),
                    Ativo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Ordem = table.Column<int>(type: "INTEGER", nullable: false),
                    OcupaHorario = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servicos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Stores",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSuspended = table.Column<bool>(type: "INTEGER", nullable: false),
                    Plan = table.Column<string>(type: "TEXT", nullable: false),
                    SubscriptionExpiry = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAccess = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BotStatus = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    BackendUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BridgeUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BusinessType = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    Nome = table.Column<string>(type: "TEXT", nullable: false),
                    Descricao = table.Column<string>(type: "TEXT", nullable: false),
                    Preco = table.Column<decimal>(type: "TEXT", nullable: false),
                    Creditos = table.Column<int>(type: "INTEGER", nullable: false),
                    DuracaoDias = table.Column<int>(type: "INTEGER", nullable: false),
                    Ativo = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemConfigs",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConfigs", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "UnavailableDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    BarberId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnavailableDays", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Is2FAEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorSecret = table.Column<string>(type: "TEXT", nullable: true),
                    BarberId = table.Column<int>(type: "INTEGER", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    StoreId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BarbeiroHorarios_BarbeiroId_DiaSemana",
                table: "BarbeiroHorarios",
                columns: new[] { "BarbeiroId", "DiaSemana" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationSessions_Phone_StoreId",
                table: "ConversationSessions",
                columns: new[] { "Phone", "StoreId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnavailableDays_StoreId_Date_BarberId",
                table: "UnavailableDays",
                columns: new[] { "StoreId", "Date", "BarberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "BarbeiroHorarios");

            migrationBuilder.DropTable(
                name: "ClientSubscriptions");

            migrationBuilder.DropTable(
                name: "ConversationSessions");

            migrationBuilder.DropTable(
                name: "Servicos");

            migrationBuilder.DropTable(
                name: "Stores");

            migrationBuilder.DropTable(
                name: "SubscriptionPlans");

            migrationBuilder.DropTable(
                name: "SystemConfigs");

            migrationBuilder.DropTable(
                name: "UnavailableDays");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropColumn(
                name: "Ativo",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "BlockedSlotsJson",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "Cor",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "CustomHoursJson",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "LunchEnd",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "LunchStart",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "StoreId",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "WorkEnd",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "WorkStart",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "WorkingDays",
                table: "Barbeiros");

            migrationBuilder.DropColumn(
                name: "BarberId",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "BarberName",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "RetentionReminderSent",
                table: "Appointments");

            migrationBuilder.RenameColumn(
                name: "StoreId",
                table: "Appointments",
                newName: "BarbeiroId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_DateTime",
                table: "Appointments",
                column: "DateTime");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PhoneNumber",
                table: "Appointments",
                column: "PhoneNumber");
        }
    }
}
