using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddServicosPermitidos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adiciona ServicosPermitidos em SubscriptionPlans
            // DEFAULT '*' → planos existentes continuam aceitando todos os serviços
            migrationBuilder.AddColumn<string>(
                name: "ServicosPermitidos",
                table: "SubscriptionPlans",
                type: "TEXT",
                nullable: false,
                defaultValue: "*");

            // Adiciona snapshot ServicosPermitidos em ClientSubscriptions
            // DEFAULT '*' → assinaturas existentes continuam sem restrição de serviço
            migrationBuilder.AddColumn<string>(
                name: "ServicosPermitidos",
                table: "ClientSubscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "*");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServicosPermitidos",
                table: "SubscriptionPlans");

            migrationBuilder.DropColumn(
                name: "ServicosPermitidos",
                table: "ClientSubscriptions");
        }
    }
}
