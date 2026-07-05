using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

/// <summary>
/// Etapa 3 do fluxo CarWash (lava-jato): captura o veículo do cliente.
/// <para>
/// Comportamento adaptado conforme contexto:
/// <list type="bullet">
///   <item>
///     <b>Histórico disponível</b>: exibe enquete com até 3 veículos salvos +
///     "Outro veículo". O cliente seleciona em vez de redigitar.
///   </item>
///   <item>
///     <b>Sem histórico</b>: prompt de texto livre (comportamento original).
///   </item>
///   <item>
///     <b>Walk-in</b> (<see cref="ConversationSession.IsWalkInMode"/>):
///     após capturar o veículo, avança direto para a confirmação imediata
///     (sem etapas de data/hora).
///   </item>
/// </list>
/// </para>
/// Novos veículos são persistidos em <see cref="ClientVehicle"/> para reutilização futura.
/// </summary>
public class AwaitingVehicleState : ConversationStateBase
{
    public AwaitingVehicleState(
        WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler,
        BusinessHours businessHours, AppDbContext db,
        ILogger<AwaitingVehicleState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(
        ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        if (IsBackCommand(text))
        {
            session.IsWalkInMode = false; // cancela walk-in se havia
            await GoBackAsync(session, ConversationState.AwaitingServiceSelection, "servico", ct);
            return;
        }

        // ── Resposta da enquete de histórico de veículos ─────────────────────
        if (pollId != null && pollId == session.LastPollId)
        {
            if (text == "outro_veiculo")
            {
                // Cliente quer informar um novo veículo manualmente
                session.LastPollId = null;
                await SendVehiclePromptAsync(session, ct);
                return;
            }

            // Selecionou veículo salvo: value = placa normalizada
            var vehicleSvc = new VehicleService(Db);
            var saved = (await vehicleSvc.GetClientVehiclesAsync(session.Phone, ct))
                .FirstOrDefault(v => v.Plate == text);

            if (saved != null)
            {
                // Atualiza LastUsed para que este apareça primeiro nas próximas visitas
                await vehicleSvc.UpsertAsync(session.Phone, saved.Plate, saved.Model, ct);
                MarkValid(session);
                session.SelectedVehicle = VehicleService.FormatLabel(saved);
                Logger.LogInformation(
                    "[Bot] Loja {StoreId}: veículo do histórico selecionado por {Phone}: {Vehicle}",
                    session.StoreId, session.Phone, session.SelectedVehicle);
                await AdvanceFromVehicleAsync(session, ct);
                return;
            }

            await RegisterInvalidResponseAsync(session,
                "Selecione um veículo da lista ou escolha '➕ Outro veículo'.", ct);
            return;
        }

        // ── Entrada de texto livre (veículo novo ou sem histórico) ───────────
        var detail = (text ?? "").Trim();
        var type    = await GetBusinessTypeAsync();
        var maxLength = type is BusinessType.Pizzeria or BusinessType.ComputerOptimization ? 120 : 60;
        var labels  = await GetBusinessLabelsAsync();

        if (detail.Length < 2 || detail.Length > maxLength)
        {
            await RegisterInvalidResponseAsync(session, labels.DetailInvalidHint, ct);
            return;
        }

        MarkValid(session);

        // Lavajato: persiste no histórico para as próximas visitas
        if (await IsCarWashAsync())
        {
            var (plate, model) = VehicleService.ParseFreeText(detail);
            // Placa mínima: 4 chars (ex.: "GOL" seria muito curto para ser uma placa)
            if (plate.Length >= 4)
                await new VehicleService(Db).UpsertAsync(session.Phone, plate, model, ct);
        }

        session.SelectedVehicle = detail;
        Logger.LogInformation(
            "[Bot] Loja {StoreId}: detalhe de agendamento informado por {Phone}: {Detail}",
            session.StoreId, session.Phone, detail);
        await AdvanceFromVehicleAsync(session, ct);
    }

    // ── Helpers privados ─────────────────────────────────────────────────────

    /// <summary>
    /// Decide a próxima etapa após capturar o veículo:
    /// <list type="bullet">
    ///   <item>Walk-in → define DateTime = agora e vai para confirmação imediata.</item>
    ///   <item>Normal → vai para seleção de data.</item>
    /// </list>
    /// </summary>
    private async Task AdvanceFromVehicleAsync(ConversationSession session, CancellationToken ct)
    {
        if (session.IsWalkInMode)
        {
            session.SelectedDate = AgendaService.GetBrazilNow();
            session.State = ConversationState.ConfirmingAppointment;
            await SendWalkInConfirmPollAsync(session, ct);
        }
        else
        {
            session.State = ConversationState.AwaitingDateSelection;
            await SendDatePollAsync(session, ct);
        }
    }
}
