using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class AwaitingMenuSelectionState : ConversationStateBase
{
    private readonly SubscriptionService _subs;

    public AwaitingMenuSelectionState(
        WhatsAppClient whatsapp,
        AgendaService agenda,
        SchedulerService scheduler,
        BusinessHours businessHours,
        AppDbContext db,
        ILogger<AwaitingMenuSelectionState> logger,
        ITenantService tenantService,
        SubscriptionService subscriptionService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService)
    {
        _subs = subscriptionService;
    }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        // backState = AwaitingMenuSelection (não Idle): se o cliente digitar "voltar"/"0"/"back"
        // no menu, GoBackAsync seta o estado correto e reenvia o menu. Com Idle, o StateManager
        // descartava silenciosamente o próximo voto (Idle + pollId = ignorado).
        await HandlePollRequiredState(session, text, pollId, ConversationState.AwaitingMenuSelection, "menu principal", async () =>
        {
            if (text == "1")
            {
                MarkValid(session);

                // Se o nome já foi coletado (ex.: assinatura feita na mesma sessão),
                // pula o passo de identificação e vai direto para a escolha do serviço.
                // Comportamento idêntico ao da opção 4 (Reagendar).
                var knownName = ResolveKnownCustomerName(session);
                if (!string.IsNullOrWhiteSpace(knownName))
                {
                    session.State = ConversationState.AwaitingServiceSelection;
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"Perfeito, vou usar o nome *{knownName}* neste agendamento.", ct);
                    await SendServicePollAsync(session, ct);
                }
                else
                {
                    session.State = ConversationState.AwaitingName;
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"{StepLabel(1, "Identificacao")}\n{AutoResponder.AskName}", ct);
                }
                return;
            }

            if (text == "2")
            {
                MarkValid(session);
                var appointments = Scheduler.GetByPhone(session.Phone, activeOnly: true)
                    .Where(a => a.DateTime >= AgendaService.GetBrazilNow())
                    .OrderBy(a => a.DateTime)
                    .ToList();

                if (!appointments.Any())
                {
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, AutoResponder.NoAgendamentos, ct);
                }
                else
                {
                    var msg = "Seus proximos agendamentos:\n\n";
                    foreach (var appt in appointments)
                    {
                        var servico = Catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}";
                        // Lava-jato: mostra o veículo (gravado em Notes). Barbearia: mostra o profissional.
                        var detailLine = await BuildAppointmentDetailLineAsync(appt);
                        var detail = string.IsNullOrWhiteSpace(detailLine) ? "" : $"{detailLine}\n";
                        msg += $"{servico}\n" +
                               $"{appt.DateTime:dd/MM/yyyy} as {appt.DateTime:HH:mm}\n" +
                               detail + "\n";
                    }
                    msg += "Para cancelar ou reagendar, use as opcoes do menu principal.";
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, msg, ct);
                }
                // Mantém AwaitingMenuSelection (não Idle) para que o cliente possa tocar
                // imediatamente outra opção do menu que ainda está visível na tela.
                // Com Idle + pollId, o StateManager descartaria o voto silenciosamente.
                // LastPollId = null permite aceitar voto do menu antigo sem exigir um poll específico.
                session.State = ConversationState.AwaitingMenuSelection;
                session.LastPollId = null;
                return;
            }

            if (text == "3")
            {
                MarkValid(session);
                var appointments = Scheduler.GetByPhone(session.Phone, activeOnly: true)
                    .Where(a => a.DateTime >= AgendaService.GetBrazilNow())
                    .OrderBy(a => a.DateTime)
                    .ToList();

                if (!appointments.Any())
                {
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, AutoResponder.NoAgendamentos, ct);
                    // Mantém AwaitingMenuSelection (não Idle) para que votos do menu ainda visível
                    // sejam processados normalmente. Idle + pollId = descarte silencioso.
                    session.State = ConversationState.AwaitingMenuSelection;
                    session.LastPollId = null;
                    return;
                }

                session.State = ConversationState.AwaitingCancelConfirmation;
                session.PendingCancelAppointmentId = null;
                session.LastPollId = null;

                if (appointments.Count == 1)
                {
                    // Só 1 agendamento: pula a seleção e vai direto para a confirmação
                    session.PendingCancelAppointmentId = appointments[0].Id;
                    await SendSingleCancelConfirmPollAsync(session, appointments[0], ct);
                }
                else
                {
                    // Múltiplos: mostra a lista para o cliente escolher qual cancelar
                    await SendCancelSelectionPollAsync(session, appointments, ct);
                }
                return;
            }

            if (text == "4")
            {
                MarkValid(session);
                var appt = Scheduler.GetProximoByPhone(session.Phone);
                if (appt != null)
                {
                    // NÃO apaga agora: guarda o ID para remover SOMENTE após o novo horário ser
                    // confirmado (ver ConfirmingAppointmentState). Se o cliente desistir no meio,
                    // o agendamento original permanece reservado — sem risco de ficar sem nada.
                    session.RescheduleAppointmentId = appt.Id;
                    var svc = Catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}";
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"Vamos remarcar seu agendamento de *{svc}* ({appt.DateTime:dd/MM} às {appt.DateTime:HH:mm}).\n" +
                        "Seu horário atual segue reservado até você confirmar o novo. 👍", ct);
                }
                else
                {
                    // Sem agendamento futuro: vira um agendamento normal (sem nada a remover).
                    session.RescheduleAppointmentId = null;
                }

                // Reutiliza o nome já salvo na sessão — evita pedir novamente para quem já passou por aqui
                var knownName = ResolveKnownCustomerName(session);
                if (!string.IsNullOrWhiteSpace(knownName))
                {
                    session.State = ConversationState.AwaitingServiceSelection;
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"Perfeito, vou usar o nome *{knownName}* neste reagendamento.", ct);
                    await SendServicePollAsync(session, ct);
                }
                else
                {
                    session.State = ConversationState.AwaitingName;
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"{StepLabel(1, "Identificacao")}\n{AutoResponder.AskName}", ct);
                }
                return;
            }

            if (text == "5" && await IsBarbershopAsync())
            {
                MarkValid(session);
                session.State = ConversationState.AwaitingSubscription;
                // Limpa TODOS os campos de estado do fluxo VIP.
                // SubscriptionPendingBarberId omitido aqui causava o bug de barber-skip:
                // se uma sessão anterior deixou barberId=0 (sem preferência), o check
                // `SubscriptionPendingBarberId == null` em HandleAsync retornava false,
                // pulando a seleção de barbeiro e indo direto para a confirmação.
                session.SubscriptionPendingPlanId    = null;
                session.SubscriptionPendingBarberId  = null;
                session.SubscriptionPendingBarberNome = null;
                session.LastPollId = null;

                // Se ainda não temos o nome do cliente, pedimos antes de mostrar os planos
                var knownName = ResolveKnownCustomerName(session);
                if (string.IsNullOrWhiteSpace(knownName))
                {
                    var vipStore = await GetCurrentStoreAsync();
                    var vipStoreName = vipStore?.Name ?? "nossa empresa";
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"*Clube de fidelidade — {vipStoreName}*\n\nPara registrar sua assinatura, preciso do seu nome. Como você se chama?", ct);
                    return;
                }

                // Nome já disponível: envia a enquete de planos imediatamente
                var plans = _subs.GetPlans();
                if (!plans.Any())
                {
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        "Nenhum plano disponível no momento. Voltando ao menu principal...", ct);
                    session.State = ConversationState.AwaitingMenuSelection;
                    await SendMainMenuAsync(session, ct);
                    return;
                }

                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                    $"Olá, *{knownName}*! Veja nossos planos de fidelidade:", ct);
                await SendSubscriptionPlansPollAsync(session, plans, ct);
                return;
            }

            // ── Opção 6: Walk-in (atendimento imediato, lava-jato) ───────────────
            if (text == "6" && await IsCarWashAsync() && Settings.GetBool("Active_WalkIn"))
            {
                MarkValid(session);
                session.IsWalkInMode = true;

                var knownName = ResolveKnownCustomerName(session);
                if (!string.IsNullOrWhiteSpace(knownName))
                {
                    session.State = ConversationState.AwaitingServiceSelection;
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"⚡ *Atendimento agora!*\nOlá, *{knownName}*! Qual serviço?", ct);
                    await SendServicePollAsync(session, ct);
                }
                else
                {
                    session.State = ConversationState.AwaitingName;
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"⚡ *Atendimento imediato!*\n{StepLabel(1, "Identificacao")}\n{AutoResponder.AskName}", ct);
                }
                return;
            }

            await RegisterInvalidResponseAsync(session, "Escolha uma opcao valida do menu.", ct);
        }, ct);
    }

    // ── Helpers de cancelamento ──────────────────────────────────────────────

    private string? ResolveKnownCustomerName(ConversationSession session)
    {
        var name = session.CustomerName;
        if (string.IsNullOrWhiteSpace(name))
            name = _subs.GetLatestByPhone(session.Phone)?.ClientName;

        // Fallback: nome do último agendamento — cliente recorrente não digita o nome de novo.
        if (string.IsNullOrWhiteSpace(name))
            name = Scheduler.GetByPhone(session.Phone)
                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.ContactName))?.ContactName;

        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        session.CustomerName = name;
        return name;
    }

    /// <summary>Confirmação direta quando há apenas 1 agendamento futuro.</summary>
    private async Task SendSingleCancelConfirmPollAsync(ConversationSession session, Appointment appt, CancellationToken ct)
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

    /// <summary>Lista de seleção quando há múltiplos agendamentos futuros.</summary>
    private async Task SendCancelSelectionPollAsync(ConversationSession session, List<Appointment> appointments, CancellationToken ct)
    {
        var options = appointments.Select(a =>
        {
            var svc = Catalog.Get(a.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{a.ServiceId}";
            // Trunca para caber em opção de enquete
            var label = $"{svc} — {a.DateTime:dd/MM} às {a.DateTime:HH:mm}";
            return new PollOption(label, a.Id.ToString());
        }).ToList();
        options.Add(new PollOption("↩️ Voltar ao menu", "voltar_etapa"));

        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone,
            "❌ Qual agendamento deseja cancelar?", options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }
}
