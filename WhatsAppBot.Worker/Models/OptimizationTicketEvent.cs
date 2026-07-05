namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Historico simples do atendimento de otimizacao.
/// </summary>
public class OptimizationTicketEvent
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public int OptimizationTicketId { get; set; }
    public string Type { get; set; } = "note";
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string Message { get; set; } = "";
    public string CreatedBy { get; set; } = "Dashboard";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public bool VisibleToCustomer { get; set; } = false;
}
