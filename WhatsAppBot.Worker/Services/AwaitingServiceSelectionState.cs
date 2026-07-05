using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class AwaitingServiceSelectionState : ConversationStateBase
{
    public AwaitingServiceSelectionState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<AwaitingServiceSelectionState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        await HandlePollRequiredState(session, text, pollId, ConversationState.AwaitingName, "nome", async () =>
        {
            if (!int.TryParse(text, out var serviceId) || Catalog.Get(serviceId) == null)
            {
                await RegisterInvalidResponseAsync(session, "Escolha um servico valido na enquete.", ct);
                return;
            }

            MarkValid(session);
            session.SelectedServiceId = serviceId;
            Logger.LogInformation("[Bot] Loja {StoreId}: servico escolhido {ServiceId} ({ServiceName}) por {Phone}",
                session.StoreId, serviceId, Catalog.Get(serviceId, includeInactive: true)?.Name, session.Phone);

            // Lava-jato: não há profissional/box. Próxima etapa é o veículo.
            // SendVehicleSelectionAsync mostra histórico salvo (poll) ou prompt de texto.
            if (await UsesSchedulingDetailsAsync())
            {
                session.State = ConversationState.AwaitingVehicle;
                await SendVehicleSelectionAsync(session, ct);
                return;
            }

            var storeId = TenantService.GetTenantId();
            var barbers = await Db.Set<Barbeiro>()
                .Where(b => b.StoreId == storeId && b.Ativo)
                .OrderBy(b => b.Nome)
                .ToListAsync(ct);

            var labels = await GetBusinessLabelsAsync();

            if (barbers.Count == 0)
            {
                Logger.LogWarning("Nenhum profissional ativo encontrado para StoreId {StoreId}", storeId);
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, labels.SemProfissionaisMsg, ct);
                // Volta ao menu principal em vez de Idle: com Idle o cliente ficaria sem menu visível
                // e qualquer voto remanescente seria descartado silenciosamente.
                ClearSchedulingData(session, preserveCustomerName: true);
                session.State = ConversationState.AwaitingMenuSelection;
                await SendMainMenuAsync(session, ct);
                return;
            }

            if (barbers.Count == 1)
            {
                var only = barbers[0];
                session.SelectedBarberId = only.Id;
                session.SelectedBarberName = only.Nome;
                session.State = ConversationState.AwaitingDateSelection;
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, $"{labels.ProfissionalSingular} selecionado: {only.Nome}.", ct);
                await SendDatePollAsync(session, ct);
                return;
            }

            session.State = ConversationState.AwaitingBarberSelection;
            await SendBarberPollAsync(session, ct);
        }, ct);
    }
}
