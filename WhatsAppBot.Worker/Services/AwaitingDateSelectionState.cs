using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class AwaitingDateSelectionState : ConversationStateBase
{
    public AwaitingDateSelectionState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<AwaitingDateSelectionState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        // No lava-jato a etapa anterior é o veículo; na barbearia é o profissional.
        // Exceção: se há apenas 1 barbeiro ativo, ele foi auto-selecionado sem interação do cliente
        // (ver AwaitingServiceSelectionState). "Voltar" deve ir para serviço, não para uma enquete
        // de barbeiro com 1 única opção — forçaria o cliente a re-selecionar algo que nunca escolheu.
        var usesDetails = await UsesSchedulingDetailsAsync();
        var labels = await GetBusinessLabelsAsync();
        ConversationState backState;
        string backLabel;
        if (usesDetails)
        {
            backState = ConversationState.AwaitingVehicle;
            backLabel = labels.DetailStepLabel.ToLowerInvariant();
        }
        else
        {
            var storeId = TenantService.GetTenantId();
            var barberCount = await Db.Set<Barbeiro>().CountAsync(b => b.StoreId == storeId && b.Ativo, ct);
            if (barberCount <= 1)
            {
                backState = ConversationState.AwaitingServiceSelection;
                backLabel = "servico";
            }
            else
            {
                backState = ConversationState.AwaitingBarberSelection;
                backLabel = "profissional";
            }
        }

        await HandlePollRequiredState(session, text, pollId, backState, backLabel, async () =>
        {
            if (!DateTime.TryParse(text, out var date))
            {
                await RegisterInvalidResponseAsync(session, "Escolha uma data valida na enquete.", ct);
                return;
            }

            session.SelectedDate = date.Date;
            session.State = ConversationState.AwaitingTimeSelection;
            session.TimeOffset = 0;

            var horarios = Agenda.GetHorariosDisponiveis(date, session.SelectedServiceId!.Value, session.SelectedBarberId);

            if (!horarios.Any())
            {
                DateTime? proximaData = null;
                var today = AgendaService.GetBrazilNow().Date;
                for (int i = 1; i <= 14; i++)
                {
                    var dTest = today.AddDays(i);
                    if (dTest.Date == date.Date) continue;
                    if (Agenda.GetHorariosDisponiveis(dTest, session.SelectedServiceId.Value, session.SelectedBarberId).Any())
                    {
                        proximaData = dTest;
                        break;
                    }
                }

                // Resets state BEFORE re-sending the date poll.
                // Without this, session stays in AwaitingTimeSelection while the date poll is shown,
                // causing the next vote (a date string like "2026-06-05") to be parsed as a time
                // → "Escolha um horário válido na enquete." — confusing and broken.
                session.State = ConversationState.AwaitingDateSelection;
                session.SelectedDate = null;

                var msg = $"Desculpe, nao ha horarios disponiveis para o dia {date:dd/MM}.";
                if (proximaData.HasValue) msg += $"\n\nDica: encontramos horarios livres para {proximaData.Value:dd/MM (ddd)}.";
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, msg + "\n\nEscolha uma nova data:", ct);
                await SendDatePollAsync(session, ct);
                return;
            }

            MarkValid(session);
            await SendTimePollAsync(session, date, ct);
        }, ct);
    }
}
