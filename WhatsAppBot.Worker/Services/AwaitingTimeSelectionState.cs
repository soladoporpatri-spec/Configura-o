using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class AwaitingTimeSelectionState : ConversationStateBase
{
    public AwaitingTimeSelectionState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<AwaitingTimeSelectionState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        await HandlePollRequiredState(session, text, pollId, ConversationState.AwaitingDateSelection, "data", async () =>
        {
            if (text == "mais")
            {
                MarkValid(session);
                // Avança exatamente o tamanho da página (TimeSlotsPerPage) — manter sincronizado
                // com o Take() em SendTimePollAsync evita pular ou repetir horários.
                session.TimeOffset += TimeSlotsPerPage;
                await SendTimePollAsync(session, session.SelectedDate!.Value, ct);
                return;
            }

            // "voltar_pagina" = paginação de horários (página anterior).
            // Separado de IsBackCommand ("voltar") para não triggerar GoBackAsync.
            if (text == "voltar_pagina")
            {
                MarkValid(session);
                session.TimeOffset = Math.Max(0, session.TimeOffset - TimeSlotsPerPage);
                await SendTimePollAsync(session, session.SelectedDate!.Value, ct);
                return;
            }

            if (!TimeSpan.TryParse(text, out var time))
            {
                await RegisterInvalidResponseAsync(session, "Escolha um horario valido na enquete.", ct);
                return;
            }

            var selected = session.SelectedDate!.Value.Date.Add(time);
            if (!Scheduler.IsSlotAvailable(selected, session.SelectedServiceId!.Value, session.SelectedBarberId))
            {
                // Verifica se RegisterInvalidResponseAsync já resetou ao menu (3ª inválida).
                // Se sim, NÃO enviar SendTimePollAsync — isso sobrescreveria session.LastPollId
                // com o ID da enquete de horário enquanto o estado já é AwaitingMenuSelection,
                // corrompendo a associação poll→estado e aceitando votos de horário como opções de menu.
                var resetToMenu = await RegisterInvalidResponseAsync(session, "Esse horario acabou de ficar indisponivel. Escolha outro horario.", ct);
                if (!resetToMenu)
                    await SendTimePollAsync(session, session.SelectedDate!.Value, ct);
                return;
            }

            MarkValid(session);
            session.SelectedDate = selected;
            session.State = ConversationState.ConfirmingAppointment;
            await SendConfirmationPollAsync(session, ct);
        }, ct);
    }
}
