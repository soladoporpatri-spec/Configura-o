namespace WhatsAppBot.Worker.Models;

public class Store
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = ""; // Ex: clinica-hair
    public bool IsActive { get; set; } = true;
    public bool IsSuspended { get; set; } = false;
    public string Plan { get; set; } = "Professional"; // Starter, Professional, Premium, Enterprise
    public DateTime SubscriptionExpiry { get; set; } = DateTime.Now.AddMonths(1);
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastAccess { get; set; }
    public string? BotStatus { get; set; } // connected, disconnected, qr
    public string? ApiKey { get; set; }
    public string? BackendUrl { get; set; }
    public string? BridgeUrl { get; set; }
    public BusinessType BusinessType { get; set; } = BusinessType.Barbershop;
}
