using System.ComponentModel.DataAnnotations.Schema;

namespace WhatsAppBot.Worker.Models;

public class ConversationSession
{
    // Chave primária composta (Phone, StoreId) definida em AppDbContext.OnModelCreating
    public string Phone { get; set; } = "";
    public int StoreId { get; set; }
    public ConversationState State { get; set; } = ConversationState.Idle;
    public string? CustomerName { get; set; }

    /// <summary>
    /// ID do serviço selecionado pelo cliente durante o fluxo de agendamento.
    /// Corresponde a <see cref="ServicoItem.Id"/> (inteiro arbitrário, não limitado ao enum TipoServico).
    /// Null enquanto o cliente ainda não escolheu o serviço.
    /// </summary>
    public int? SelectedServiceId { get; set; }

    /// <summary> Veículo/detalhe informado pelo cliente (fluxos CarWash, Pizzeria, ComputerOptimization). Vai para Appointment.Notes. </summary>
    public string? SelectedVehicle { get; set; }
    public int? SelectedBarberId { get; set; }
    public string? SelectedBarberName { get; set; }
    public DateTime? SelectedDate { get; set; }
    public DateTime LastInteraction { get; set; } = DateTime.Now;
    public string? LastPollId { get; set; } // Para validar se o voto pertence à última enquete enviada
    public int TimeOffset { get; set; } = 0;

    public int InvalidResponseCount { get; set; } = 0;

    /// <summary>
    /// ID do plano escolhido durante o fluxo de assinatura (AwaitingSubscription).
    /// Null quando não há fluxo de assinatura em andamento.
    /// </summary>
    public int? SubscriptionPendingPlanId { get; set; }

    /// <summary>
    /// ID do barbeiro escolhido durante o fluxo de assinatura.
    /// Null  = passo ainda não chegou (aguardando seleção de barbeiro).
    /// 0     = cliente escolheu "Sem preferência" (assinatura não é atrelada a nenhum barbeiro).
    /// &gt; 0  = barbeiro específico selecionado.
    /// </summary>
    public int? SubscriptionPendingBarberId { get; set; }

    /// <summary>Nome do barbeiro selecionado — snapshot para exibição na poll de confirmação.</summary>
    public string? SubscriptionPendingBarberNome { get; set; }

    /// <summary>
    /// ID do agendamento selecionado para cancelamento (AwaitingCancelConfirmation).
    /// Null = ainda aguardando seleção (múltiplos agendamentos); preenchido = aguardando confirmação.
    /// </summary>
    public int? PendingCancelAppointmentId { get; set; }

    /// <summary>
    /// Flag de sessão: cliente ativou o fluxo walk-in (⚡ Quero atendimento agora).
    /// Quando true, as etapas de data/hora são puladas e o agendamento é criado com
    /// <c>DateTime = agora</c> e <c>IsWalkIn = true</c>.
    /// Resetado para false após a confirmação ou cancelamento do fluxo.
    /// </summary>
    public bool IsWalkInMode { get; set; } = false;

    /// <summary>
    /// ID do agendamento que está sendo REAGENDADO (opção 4 do menu).
    /// O agendamento antigo só é removido DEPOIS que o novo é confirmado com sucesso —
    /// assim, se o cliente abandonar o fluxo, o horário original é preservado.
    /// <para>
    /// [NotMapped]: vive apenas em memória (a sessão é mantida em cache durante o fluxo,
    /// que dura segundos/minutos). Não persiste no banco — se o bot reiniciar no meio do
    /// reagendamento, no pior caso o cliente fica com 2 agendamentos (visível e removível
    /// pelo dono), o que é estritamente melhor que perder o agendamento.
    /// </para>
    /// </summary>
    [NotMapped]
    public int? RescheduleAppointmentId { get; set; }
}
