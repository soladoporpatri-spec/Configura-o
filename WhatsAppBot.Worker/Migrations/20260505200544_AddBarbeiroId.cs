using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppBot.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddBarbeiroId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BarbeiroId",
                table: "Appointments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Barbeiros",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Nome = table.Column<string>(type: "TEXT", nullable: false),
                    Especialidade = table.Column<string>(type: "TEXT", nullable: false),
                    Adicional = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Barbeiros", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_DateTime",
                table: "Appointments",
                column: "DateTime");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_PhoneNumber",
                table: "Appointments",
                column: "PhoneNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Barbeiros");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_DateTime",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_PhoneNumber",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "BarbeiroId",
                table: "Appointments");
        }
    }
}
