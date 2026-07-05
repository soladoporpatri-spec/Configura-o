/// <summary>
/// MODELO PRINCIPAL: Representa um agendamento no sistema de agendamento multi-tenant.
/// Armazena todos os dados necessários para CRUD, lembretes e relatórios.
/// Tabela: Appointments no SQLite (agendamentos.db)
/// </summary>
namespace WhatsAppBot.Worker.Models;

public class Appointment
{
    /// <summary> ID primário auto-incrementado pelo EF Core </summary>
    public int Id { get; set; }

    /// <summary> ID da Loja/Unidade para Multi-tenancy </summary>
    public int StoreId { get; set; }

    /// <summary> Número do WhatsApp do cliente (chave para lookup por telefone) </summary>
    public string PhoneNumber { get; set; } = "";

    /// <summary> Nome do cliente (informado no agendamento) </summary>
    public string ContactName { get; set; } = "";

    /// <summary> Observacoes internas criadas pela dashboard/site </summary>
    public string? Notes { get; set; }

    /// <summary> Data e hora exata do agendamento </summary>
    public DateTime DateTime { get; set; }

    /// <summary>
    /// ID do serviço escolhido. Corresponde ao Id em ServicoItem.
    /// Lojas legado (barbearia sem ServicoItem) usam IDs 1-6 conforme o enum TipoServico.
    /// Lojas com ServicoItem podem ter IDs arbitrários — sem limitação de enum.
    /// </summary>
    public int ServiceId { get; set; } = 1;

    public int? BarberId { get; set; }
    public string? BarberName { get; set; }

    /// <summary> Duração em minutos (do ServicoItem ou default) </summary>
    public int DuracaoMinutos { get; set; } = 30;

    /// <summary> Preço do serviço (do ServicoItem) </summary>
    public decimal Preco { get; set; }

    /// <summary> Flag: Lembrete padrão enviado? </summary>
    public bool ReminderSent { get; set; } = false;

    /// <summary> Flag: Lembrete D-1 enviado (ReminderService) </summary>
    public bool ReminderDayBefore { get; set; } = false;

    /// <summary> Flag: Lembrete 1h enviado (ReminderService) </summary>
    public bool ReminderOneHour { get; set; } = false;

    /// <summary> Flag: mensagem de agradecimento pós-atendimento enviada (ReminderService) </summary>
    public bool ThanksSent { get; set; } = false;

    /// <summary> Flag: lembrete de retorno/retenção enviado após o atendimento </summary>
    public bool RetentionReminderSent { get; set; } = false;

    /// <summary> Cliente confirmou presença? (D-1 via bot) </summary>
    public bool PresencaConfirmada { get; set; } = false;

    /// <summary> Timestamp de criação (auto-set) </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary> Estado do agendamento: "ativo" (default) ou "cancelado" (soft-delete) </summary>
    public string Status { get; set; } = "ativo";

    /// <summary> Timestamp do cancelamento (null = não cancelado) </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary> Quem cancelou: "bot", "dashboard", etc. (null = não cancelado) </summary>
    public string? CancelledBy { get; set; }

    /// <summary>
    /// Placa/modelo do veículo atendido (lavajato). Campo de primeiro nível — distinto
    /// de <see cref="Notes"/> que é para observações livres internas.
    /// Ex.: "ABC1234 — Civic 2020 Prata"
    /// Null para barbearia, pizzaria, etc.
    /// </summary>
    public string? VehicleInfo { get; set; }

    /// <summary>
    /// Flag: agendamento walk-in (sem hora marcada, atendimento imediato).
    /// DateTime = momento do pedido; cliente está fisicamente presente.
    /// Quando true, lembretes de D-1 e 1h são suprimidos pelo ReminderService.
    /// </summary>
    public bool IsWalkIn { get; set; } = false;

}
