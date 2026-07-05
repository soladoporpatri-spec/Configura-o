using System.Text.Json.Serialization;

namespace WhatsAppBot.Worker.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OptimizationTicketStatus
{
    Novo = 0,
    Triagem = 1,
    Agendado = 2,
    AguardandoCliente = 3,
    EmOtimizacao = 4,
    EmRevisao = 5,
    Pronto = 6,
    Concluido = 7,
    Cancelado = 8
}

/// <summary>
/// Atendimento operacional de otimizacao de computador.
/// </summary>
public class OptimizationTicket
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string TicketNumber { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public int? OptimizationDeviceId { get; set; }
    public int? AppointmentId { get; set; }
    public int? ServiceId { get; set; }
    public string ServiceMode { get; set; } = "Presencial";
    public string Goal { get; set; } = "Melhorar desempenho geral";
    public string ReportedProblem { get; set; } = "";
    public string Urgency { get; set; } = "Essa semana";
    public OptimizationTicketStatus Status { get; set; } = OptimizationTicketStatus.Novo;
    public string? BeforeNotes { get; set; }
    public string OptimizationChecklistJson { get; set; } = "[]";
    public string? AfterNotes { get; set; }
    public string? ResultSummary { get; set; }
    public decimal? EstimatedAmount { get; set; }
    public decimal? FinalAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
