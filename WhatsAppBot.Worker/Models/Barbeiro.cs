namespace WhatsAppBot.Worker.Models;

public class Barbeiro
{
    public int Id { get; set; }
    public int StoreId { get; set; } = 1;
    public string Nome { get; set; } = string.Empty;
    public bool Ativo { get; set; } = true;
    public string Cor { get; set; } = "#3498db";
    public string Especialidade { get; set; } = "Geral";
    public string Adicional { get; set; } = string.Empty;
    public TimeSpan WorkStart { get; set; } = TimeSpan.Zero;
    public TimeSpan WorkEnd { get; set; } = TimeSpan.Zero;
    public TimeSpan? LunchStart { get; set; }
    public TimeSpan? LunchEnd { get; set; }
    public string WorkingDays { get; set; } = "1,2,3,4,5,6";
    public string BlockedSlotsJson { get; set; } = "{}";
    public string CustomHoursJson { get; set; } = "{}";
}
