using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class AwaitingBarberSelectionState : ConversationStateBase
{
    public AwaitingBarberSelectionState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<AwaitingBarberSelectionState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        await HandlePollRequiredState(session, text, pollId, ConversationState.AwaitingServiceSelection, "servico", async () =>
        {
            if (!int.TryParse(text, out var barberId))
            {
                await RegisterInvalidResponseAsync(session, "Escolha um profissional valido na enquete.", ct);
                return;
            }

            var storeId = TenantService.GetTenantId();
            var barber = await Db.Set<Barbeiro>().FirstOrDefaultAsync(b => b.Id == barberId && b.StoreId == storeId && b.Ativo, ct);
            if (barber == null)
            {
                await RegisterInvalidResponseAsync(session, "Esse profissional nao esta disponivel. Escolha uma opcao valida.", ct);
                return;
            }

            MarkValid(session);
            session.SelectedBarberId = barber.Id;
            session.SelectedBarberName = barber.Nome;
            Logger.LogInformation("[Bot] Loja {StoreId}: profissional escolhido {BarberId} ({BarberName}) por {Phone}",
                session.StoreId, barber.Id, barber.Nome, session.Phone);
            session.State = ConversationState.AwaitingDateSelection;
            await SendDatePollAsync(session, ct);
        }, ct);
    }
}
