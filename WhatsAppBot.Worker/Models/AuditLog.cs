namespace WhatsAppBot.Worker.Models;

public class AuditLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Details { get; set; }
    /// <summary>
    /// 0 = operação global/superadmin. &gt;0 = loja específica.
    /// </summary>
    public int StoreId { get; set; } = 0;
}
