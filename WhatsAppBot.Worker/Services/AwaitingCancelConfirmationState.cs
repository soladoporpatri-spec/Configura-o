using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

/// <summary>
/// Gerencia o cancelamento de agendamentos com confirmação prévia.
///
/// Fluxo:
///   — 1 agendamento futuro  → Menu transfere PendingCancelAppointmentId já preenchido
///                             → Este estado só lida com a confirmação (sim/manter).
///   — N agendamentos futuros → Menu transfere PendingCancelAppointmentId = null
///                             → Este estado mostra lista, aguarda seleção, depois confirmação.
/// </summary>
public class AwaitingCancelConfirmationState : ConversationStateBase
{
    public AwaitingCancelConfirmationState(
        WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler,
        BusinessHours businessHours, AppDbContext db,
        ILogger<AwaitingCancelConfirmationState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        // "Voltar ao menu" ou qualquer comando de back cancela toda a operação
        if (IsBackCommand(text))
        {
            await AbortAsync(session, ct);
            return;
        }

        if (session.PendingCancelAppointmentId == null)
            await HandleSelectionPhaseAsync(session, text, pollId, ct);
        else
            await HandleConfirmationPhaseAsync(session, text, pollId, ct);
    }

    // ── Fase 1: selecionar qual agendamento (quando há múltiplos) ─────────────

    private async Task HandleSelectionPhaseAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        // Poll enviado previamente por AwaitingMenuSelectionState; valida que o voto pertence a ele
        if (!string.IsNullOrEmpty(session.LastPollId) && !string.IsNullOrEmpty(pollId) && pollId != session.LastPollId)
        {
            await RegisterInvalidResponseAsync(session, "Use a lista de agendamentos enviada acima.", ct);
            return;
        }

        if (!int.TryParse(text, out var apptId))
        {
            await RegisterInvalidResponseAsync(session, "Escolha um dos agendamentos na lista acima.", ct);
            return;
        }

        var appt = await Scheduler.GetByIdAsync(apptId, ct);
        if (appt == null || appt.PhoneNumber != session.Phone || appt.DateTime < AgendaService.GetBrazilNow())
        {
            await RegisterInvalidResponseAsync(session, "Agendamento não encontrado. Escolha um da lista.", ct);
            return;
        }

        MarkValid(session);
        session.PendingCancelAppointmentId = apptId;
        session.LastPollId = null;
        await SendConfirmCancelPollAsync(session, appt, ct);
    }

    // ── Fase 2: confirmar o cancelamento ─────────────────────────────────────

    private async Task HandleConfirmationPhaseAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(session.LastPollId) && !string.IsNullOrEmpty(pollId) && pollId != session.LastPollId)
        {
            await RegisterInvalidResponseAsync(session, "Use a enquete de confirmação acima.", ct);
            return;
        }

        var normalized = text.Trim().ToLowerInvariant();

        // "manter" = botão de manter agendamento (não usa IsBackCommand para não confundir com "voltar a data")
        if (normalized == "manter")
        {
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "👍 Agendamento mantido! Até lá.", ct);
            await AbortAsync(session, ct);
            return;
        }

        if (normalized != "sim_cancelar")
        {
            await RegisterInvalidResponseAsync(session, "Use a enquete acima para confirmar ou manter o agendamento.", ct);
            return;
        }

        // Confirma o cancelamento
        var appt = await Scheduler.GetByIdAsync(session.PendingCancelAppointmentId!.Value, ct);
        if (appt == null || appt.PhoneNumber != session.Phone)
        {
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "Este agendamento não foi encontrado — pode já ter sido cancelado.", ct);
            await AbortAsync(session, ct);
            return;
        }

        await Scheduler.CancelAsync(appt.Id, "bot", ct);

        var servico = Catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}";
        var detailLine = await BuildAppointmentDetailLineAsync(appt);
        var detalhe = string.IsNullOrWhiteSpace(detailLine) ? "" : $"\n{detailLine}";

        var msg = $"✅ Agendamento cancelado!\n\n" +
                  $"{servico}\n" +
                  $"{appt.DateTime:dd/MM/yyyy} às {appt.DateTime:HH:mm}" +
                  detalhe;

        Logger.LogInformation("[Bot] Loja {StoreId}: agendamento #{ApptId} cancelado pelo cliente {Phone}",
            session.StoreId, appt.Id, session.Phone);

        MarkValid(session);
        session.PendingCancelAppointmentId = null;
        session.LastPollId = null;
        session.State = ConversationState.AwaitingMenuSelection;

        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, msg, ct);
        await SendMainMenuAsync(session, ct);
    }

    // ── Helper: abortar e voltar ao menu ─────────────────────────────────────

    private async Task AbortAsync(ConversationSession session, CancellationToken ct)
    {
        session.PendingCancelAppointmentId = null;
        session.LastPollId = null;
        session.State = ConversationState.AwaitingMenuSelection;
        await SendMainMenuAsync(session, ct);
    }

    // ── Helper: enviar enquete de confirmação de cancelamento ─────────────────

    private async Task SendConfirmCancelPollAsync(ConversationSession session, Appointment appt, CancellationToken ct)
    {
        var servico = Catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}";
        var labels = await GetBusinessLabelsAsync();
        var emoji = labels.ServiceEmoji;
        var detailLine = await BuildAppointmentDetailLineAsync(appt);
        var detalhe = string.IsNullOrWhiteSpace(detailLine) ? "" : $"\n{detailLine}";

        var title = $"Cancelar este agendamento?\n\n" +
                    $"{emoji} {servico}\n" +
                    $"📅 {appt.DateTime:dd/MM/yyyy} às {appt.DateTime:HH:mm}" +
                    detalhe;

        var options = new List<PollOption>
        {
            new("✅ Sim, cancelar",       "sim_cancelar"),
            new("↩️ Manter agendamento", "manter"),
        };

        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, title, options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }
}
