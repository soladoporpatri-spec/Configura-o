namespace WhatsAppBot.Worker.Models;

public class UnavailableDay
{
    public int Id { get; set; }
    public int StoreId { get; set; } = 1;
    public DateTime Date { get; set; }
    public string Type { get; set; } = "fechado";
    public string Reason { get; set; } = "";
    public int? BarberId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
