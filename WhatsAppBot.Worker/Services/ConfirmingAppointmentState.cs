using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Endpoints;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class ConfirmingAppointmentState : ConversationStateBase
{
    private readonly SubscriptionService _subs;

    public ConfirmingAppointmentState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<ConfirmingAppointmentState> logger, ITenantService tenantService, SubscriptionService subscriptionService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService)
    {
        _subs = subscriptionService;
    }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        await HandlePollRequiredState(session, text, pollId, ConversationState.AwaitingTimeSelection, "horario", async () =>
        {
            var normalized = text.Trim().ToLowerInvariant();

            if (normalized == "nao" || normalized == "cancelar")
            {
                session.IsWalkInMode = false;
                ClearSchedulingData(session, preserveCustomerName: true);
                session.State = ConversationState.AwaitingMenuSelection;
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, "Agendamento cancelado. O que mais posso fazer por você?", ct);
                await SendMainMenuAsync(session, ct);
                return;
            }

            if (normalized != "sim")
            {
                await RegisterInvalidResponseAsync(session, "Escolha Confirmar, Voltar ou Cancelar na enquete.", ct);
                return;
            }

            // Validação preventiva: loja precisa estar ativa antes de criar o agendamento.
            if (!await EnsureStoreUsableAsync(session, ct)) return;

            int storeId = TenantService.GetTenantId();
            var sInfo = Catalog.Get(session.SelectedServiceId!.Value);
            if (sInfo == null)
            {
                ClearSchedulingData(session, preserveCustomerName: true);
                session.State = ConversationState.AwaitingMenuSelection;
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, "Este servico esta indisponivel no momento. Voltando ao menu.", ct);
                await SendMainMenuAsync(session, ct);
                return;
            }

            var customerName = ResolveCustomerName(session);
            if (string.IsNullOrWhiteSpace(customerName))
            {
                session.State = ConversationState.AwaitingName;
                session.LastPollId = null;
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                    $"{StepLabel(1, "Identificacao")}\nPerdi seu nome neste atendimento. Como voce se chama?", ct);
                return;
            }

            // Lava-jato: sem profissional/box (barberId null → disponibilidade por capacidade da loja).
            // O veículo informado vai para as observações do agendamento.
            var isBarbershop = await IsBarbershopAsync();
            var usesDetails = await UsesSchedulingDetailsAsync();
            var labels = await GetBusinessLabelsAsync();
            // SelectedBarberId é sempre setado para barbearia (auto-seleção 1 barbeiro ou escolha manual).
            // Fallback ?? 1 removido: em multi-tenant, ID 1 pode pertencer a outra loja.
            // Se por algum edge case for null, o backend trata como "capacidade da loja" (correto).
            int? barberId = isBarbershop ? session.SelectedBarberId : null;
            var detail = session.SelectedVehicle;
            string? notes = usesDetails && !string.IsNullOrWhiteSpace(detail) ? $"{labels.DetailNotesPrefix}: {detail}" : null;
            string? barberName = isBarbershop ? session.SelectedBarberName : null;
            // vehicleInfo: campo de primeiro nível para placa/modelo (lavajato).
            // Distinto de notes (legível) — facilita exibição na dashboard.
            string? vehicleInfo = usesDetails && !string.IsNullOrWhiteSpace(detail) ? detail : null;
            var wasWalkIn = session.IsWalkInMode;

            var appt = await Scheduler.SaveAsync(storeId, session.Phone, customerName, session.SelectedDate!.Value, session.SelectedServiceId!.Value, sInfo.DurationMinutes, sInfo.Price, barberId, barberName, notes, "WhatsApp/Bot", vehicleInfo, wasWalkIn, ct);
            if (appt == null)
            {
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, "Desculpe, esse horario ja nao esta disponivel. Escolha outro horario.", ct);
                session.State = ConversationState.AwaitingTimeSelection;
                if (session.SelectedDate.HasValue)
                    session.SelectedDate = session.SelectedDate.Value.Date;
                await SendTimePollAsync(session, session.SelectedDate!.Value, ct);
                return;
            }

            Logger.LogInformation("[Bot] Loja {StoreId}: AGENDAMENTO CRIADO #{ApptId} — {Service} em {Quando} (barber={BarberId}, details={UsesDetails}) por {Phone}",
                storeId, appt.Id, sInfo.Name, appt.DateTime, barberId, usesDetails, session.Phone);

            if (await GetBusinessTypeAsync() == BusinessType.ComputerOptimization)
            {
                try
                {
                    var ticketId = await OptimizationEndpoints.CreateTicketFromBotAsync(Db, storeId, appt, sInfo, detail, ct);
                    Logger.LogInformation("[Bot] Loja {StoreId}: ticket de otimizacao #{TicketId} criado a partir do agendamento #{ApptId}", storeId, ticketId, appt.Id);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[Bot] Falha ao criar ticket de otimizacao para agendamento #{ApptId}. O agendamento foi preservado.", appt.Id);
                }
            }

            // ── Reagendamento: remove o horário ANTIGO só agora que o novo foi criado ──
            // Diferido de propósito (opção 4 do menu): se o cliente tivesse abandonado o
            // fluxo antes, o agendamento original seria preservado. Best-effort — uma falha
            // aqui NUNCA quebra a confirmação do novo horário.
            if (session.RescheduleAppointmentId.HasValue && session.RescheduleAppointmentId.Value != appt.Id)
            {
                var oldId = session.RescheduleAppointmentId.Value;
                try
                {
                    await Scheduler.DeleteAsync(oldId, ct);
                    Logger.LogInformation("[Reagendamento] Antigo #{OldId} removido após criar #{NewId} para {Phone}", oldId, appt.Id, session.Phone);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[Reagendamento] Falha ao remover antigo #{OldId} após criar #{NewId} — cliente pode ter 2 agendamentos (removível pelo painel).", oldId, appt.Id);
                }
                session.RescheduleAppointmentId = null;
            }

            // ── Consumir crédito de assinatura (apenas barbearia) ─────────────
            // IMPORTANTE: O bloco de crédito é best-effort.
            // Se falhar por qualquer motivo (EF, concorrência, etc.), o agendamento
            // JÁ FOI criado e o cliente DEVE receber a confirmação normalmente.
            string subscriptionNote = "";
            if (isBarbershop)
            {
                try
                {
                    // Busca assinatura ativa filtrando por serviço E por barbeiro do agendamento.
                    // Regras: crédito de "Plano Corte" não cobre barba; crédito "com Itamar" não
                    // cobre agendamento com outro profissional.
                    var servicoAgendado = session.SelectedServiceId!.Value; // int, não enum
                    var barbeiroAgendado = barberId;
                    var activeSub = _subs.GetActiveByPhone(session.Phone, servicoAgendado, barbeiroAgendado);

                    if (activeSub != null)
                    {
                        // Captura ANTES de decrementar para cálculo correto de créditos restantes
                        var creditosAntes = activeSub.CreditosRestantes;

                        // Passa a entidade já rastreada — evita segunda query ao banco e
                        // conflito de identity map no EF Core
                        await _subs.UseCredentialAsync(activeSub, ct);

                        var restantes = creditosAntes - 1;
                        subscriptionNote = restantes > 0
                            ? $"\n\nCredito do clube de fidelidade utilizado! Restam *{restantes}* uso(s)."
                            : "\n\nUltimo credito do clube de fidelidade utilizado! Renove em breve.";

                        Logger.LogInformation(
                            "[Assinatura] Crédito consumido para {Phone} — {Antes}→{Depois} (sub #{SubId}, servico={Servico}, barbeiro={Barbeiro})",
                            session.Phone, creditosAntes, restantes, activeSub.Id, servicoAgendado, barbeiroAgendado);
                    }
                    else
                    {
                        // Sem crédito válido: verifica se o motivo é serviço ou barbeiro errado
                        // e avisa o cliente na mensagem de confirmação sem bloquear o agendamento.
                        var subAny = _subs.GetActiveByPhone(session.Phone);
                        if (subAny != null)
                        {
                            var motivos = new List<string>();
                            if (!SubscriptionService.IsServicoPermitido(subAny, servicoAgendado))
                            {
                                var cobertura = SubscriptionService.DescricaoServicosPermitidos(subAny.ServicosPermitidos);
                                motivos.Add($"serviço não coberto pelo plano ({cobertura})");
                            }
                            if (subAny.BarbeiroId is > 0 &&
                                (barbeiroAgendado == null || barbeiroAgendado == 0 || subAny.BarbeiroId != barbeiroAgendado))
                            {
                                motivos.Add($"plano vinculado a *{subAny.BarbeiroNome}*");
                            }

                            var motivoStr = motivos.Count > 0
                                ? string.Join(" e ", motivos)
                                : "restrições do plano";

                            subscriptionNote = $"\n\n⚠️ Seu plano *{subAny.PlanNome}* não foi descontado: {motivoStr}.";

                            Logger.LogInformation(
                                "[Assinatura] Crédito NÃO consumido para {Phone} sub #{SubId} — motivos: {Motivos}",
                                session.Phone, subAny.Id, motivoStr);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Falha no crédito NÃO cancela a confirmação — log e segue
                    Logger.LogError(ex,
                        "[Assinatura] Erro ao consumir crédito para {Phone} no agendamento #{ApptId}. " +
                        "Agendamento foi criado; crédito precisará ser ajustado manualmente.",
                        session.Phone, appt.Id);
                }
            }

            MarkValid(session);
            session.IsWalkInMode = false; // limpa o flag — sempre, antes de ClearSchedulingData
            ClearSchedulingData(session, preserveCustomerName: true);
            session.LastPollId = null;
            // Após confirmação, vai para Idle — o cliente encerra a conversa naturalmente.
            // O menu NÃO é reenviado automaticamente para não poluir a conversa.
            // Quando o cliente quiser fazer outra coisa, basta enviar "oi" para recomeçar.
            session.State = ConversationState.Idle;

            // Msg_Confirmation: template personalizável por loja (painel → Automações).
            // Suporta {nome}, {servico}, {data}, {hora}, {profissional}, {loja}.
            // Fallback: mensagem padrão com todos os dados do agendamento.
            var storeObj = await GetCurrentStoreAsync();
            var confirmTemplate = Settings.GetString("Msg_Confirmation");
            string confirmMsg;
            if (!string.IsNullOrWhiteSpace(confirmTemplate))
            {
                var ctx = new MessageTemplateContext(
                    Nome:         customerName,
                    Servico:      sInfo.Name,
                    Profissional: appt.BarberName ?? "nossa equipe",
                    Loja:         storeObj?.Name ?? "nossa empresa",
                    Data:         appt.DateTime.ToString("dd/MM/yyyy"),
                    Hora:         appt.DateTime.ToString("HH:mm")
                );
                confirmMsg = MessageTemplateService.Apply(confirmTemplate, ctx) + subscriptionNote;
            }
            else if (wasWalkIn)
            {
                // Walk-in: mensagem direta, sem data/hora futura
                confirmMsg = $"⚡ *Você está na fila!*\n\n🔧 {sInfo.Name}" +
                             (string.IsNullOrWhiteSpace(detail) ? "" : $"\n🚗 {detail}") +
                             "\n\nNossa equipe vai chamar você em breve! 😊";
            }
            else
            {
                confirmMsg = usesDetails
                    ? $"Agendamento confirmado!\n\n{sInfo.Name} em {appt.DateTime:dd/MM/yyyy} as {appt.DateTime:HH:mm}." +
                      (string.IsNullOrWhiteSpace(detail) ? "" : $"\n{labels.DetailLineLabel}: {detail}")
                    : $"Agendamento confirmado! ✅\n\n{sInfo.Name} em {appt.DateTime:dd/MM/yyyy} as {appt.DateTime:HH:mm} com {appt.BarberName ?? "nossa equipe"}.{subscriptionNote}";
            }
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, confirmMsg, ct);
        }, ct);
    }

    private string? ResolveCustomerName(ConversationSession session)
    {
        var name = session.CustomerName;

        // 1ª opção: nome da assinatura (cliente do clube de fidelidade).
        if (string.IsNullOrWhiteSpace(name))
            name = _subs.GetLatestByPhone(session.Phone)?.ClientName;

        // 2ª opção: nome do último agendamento (cliente recorrente já cadastrado no banco).
        // Garante que quem já está no banco NUNCA precise digitar o nome de novo.
        if (string.IsNullOrWhiteSpace(name))
            name = Scheduler.GetByPhone(session.Phone)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.ContactName))?.ContactName;

        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        session.CustomerName = name;
        return name;
    }
}
