using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Services.States;

public abstract class ConversationStateBase : IConversationState
{
    protected readonly WhatsAppClient WhatsApp;
    protected readonly AgendaService Agenda;
    protected readonly SchedulerService Scheduler;
    protected readonly BusinessHours BusinessHours;
    protected readonly AppDbContext Db;
    protected readonly ILogger Logger;
    protected readonly ITenantService TenantService;
    // Lazy: cria uma única instância por handler (cada handler é transiente por mensagem processada).
    private ServiceCatalogService? _catalog;
    protected ServiceCatalogService Catalog => _catalog ??= new(Db);

    // Lazy: StoreSettingsService usa o mesmo AppDbContext (já com TenantId configurado).
    private StoreSettingsService? _settings;
    protected StoreSettingsService Settings => _settings ??= new(Db);

    protected ConversationStateBase(
        WhatsAppClient whatsapp,
        AgendaService agenda,
        SchedulerService scheduler,
        BusinessHours businessHours,
        AppDbContext db,
        ILogger logger,
        ITenantService tenantService)
    {
        WhatsApp = whatsapp;
        Agenda = agenda;
        Scheduler = scheduler;
        BusinessHours = businessHours;
        Db = db;
        Logger = logger;
        TenantService = tenantService;
        Db.TenantId = tenantService.GetTenantId();
    }

    public abstract Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct);

    protected async Task<string> GetBridgeUrlAsync()
    {
        var storeId = TenantService.GetTenantId();
        var store = await Db.Stores.FindAsync(storeId);
        // Fallback pela convenção da bridge-factory (porta = 3000 + storeId) em vez de :3000 fixo,
        // garantindo que a resposta vá para a bridge correta da loja (ex: lava-jato → 3002).
        return string.IsNullOrWhiteSpace(store?.BridgeUrl)
            ? $"http://127.0.0.1:{3000 + (storeId <= 0 ? 1 : storeId)}"
            : store.BridgeUrl;
    }

    // Loja da mensagem atual, carregada uma única vez por instância (handler é transiente por mensagem).
    // Função reutilizável central para "buscar a loja" — todos os helpers de negócio passam por aqui.
    private Store? _cachedStore;
    private bool _storeLoaded;
    protected async Task<Store?> GetCurrentStoreAsync()
    {
        if (_storeLoaded) return _cachedStore;
        var storeId = TenantService.GetTenantId();
        _cachedStore = await Db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId);
        _storeLoaded = true;
        return _cachedStore;
    }

    protected async Task<BusinessLabels> GetBusinessLabelsAsync()
        => BusinessLabelsFactory.For((await GetCurrentStoreAsync())?.BusinessType ?? BusinessType.Barbershop);

    protected async Task<BusinessType> GetBusinessTypeAsync()
        => (await GetCurrentStoreAsync())?.BusinessType ?? BusinessType.Barbershop;

    protected async Task<bool> IsCarWashAsync() => await GetBusinessTypeAsync() == BusinessType.CarWash;
    protected async Task<bool> IsBarbershopAsync() => await GetBusinessTypeAsync() == BusinessType.Barbershop;
    protected async Task<bool> UsesSchedulingDetailsAsync()
        => await GetBusinessTypeAsync() is BusinessType.CarWash or BusinessType.Pizzeria or BusinessType.ComputerOptimization;

    protected async Task<string?> BuildAppointmentDetailLineAsync(Appointment appt)
    {
        var labels = await GetBusinessLabelsAsync();
        if (await UsesSchedulingDetailsAsync())
        {
            return string.IsNullOrWhiteSpace(appt.Notes) ? null : appt.Notes;
        }

        return string.IsNullOrWhiteSpace(appt.BarberName)
            ? $"{labels.ProfissionalSingular}: Equipe"
            : $"{labels.ProfissionalSingular}: {appt.BarberName}";
    }

    /// <summary>Valida que a loja existe e está ativa antes de qualquer operação sensível.</summary>
    protected async Task<bool> EnsureStoreUsableAsync(ConversationSession session, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync();
        var status = StoreAccessPolicy.Evaluate(store);
        if (status.CanOperate) return true;

        Logger.LogWarning("Loja {StoreId} indisponivel ({Reason}). Abortando fluxo para {Phone}.",
            TenantService.GetTenantId(), status.Reason, session.Phone);
        try
        {
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "Esta unidade esta temporariamente indisponivel. Tente novamente mais tarde.", ct);
        }
        catch { /* não deixa falha de envio quebrar o fluxo */ }
        ClearSchedulingData(session);
        session.State = ConversationState.Idle;
        return false;
    }

    /// <summary>Pergunta o veículo em texto livre — fallback quando não há histórico.</summary>
    protected async Task SendVehiclePromptAsync(ConversationSession session, CancellationToken ct)
    {
        var labels = await GetBusinessLabelsAsync();
        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(3, labels.DetailStepLabel)}\n{labels.DetailPrompt}", ct);
    }

    /// <summary>
    /// Etapa de captura de veículo inteligente:
    /// <list type="bullet">
    ///   <item>Lavajato + histórico disponível → enquete com até 3 veículos salvos + "Outro veículo".</item>
    ///   <item>Sem histórico ou outro negócio → prompt de texto (comportamento original).</item>
    /// </list>
    /// </summary>
    protected async Task SendVehicleSelectionAsync(ConversationSession session, CancellationToken ct)
    {
        if (await IsCarWashAsync())
        {
            var vehicles = await new VehicleService(Db).GetClientVehiclesAsync(session.Phone, ct);
            if (vehicles.Count > 0)
            {
                var labels = await GetBusinessLabelsAsync();
                var options = vehicles
                    .Select(v => new PollOption($"🚗 {VehicleService.FormatLabel(v)}", v.Plate))
                    .ToList();
                options.Add(new PollOption("➕ Outro veículo", "outro_veiculo"));
                options.Add(BackOption());
                var poll = await WhatsApp.SendPollAsync(
                    await GetBridgeUrlAsync(), session.Phone,
                    $"{StepLabel(3, labels.DetailStepLabel)}\nQual veículo será atendido?",
                    options, ct);
                if (poll != null) session.LastPollId = poll.Id;
                return;
            }
        }
        // Sem histórico ou não é lavajato: prompt de texto
        await SendVehiclePromptAsync(session, ct);
    }

    /// <summary>
    /// Enquete de confirmação walk-in (sem data/hora): mostra serviço + veículo.
    /// </summary>
    protected async Task SendWalkInConfirmPollAsync(ConversationSession session, CancellationToken ct)
    {
        var sInfo = Catalog.Get(session.SelectedServiceId!.Value);
        var vehicle = session.SelectedVehicle;

        var title = $"⚡ Confirmar atendimento agora?\n\n🔧 {sInfo?.Name ?? "Serviço"}";
        if (!string.IsNullOrWhiteSpace(vehicle))
            title += $"\n🚗 {vehicle}";

        var options = new List<PollOption>
        {
            new("✅ Confirmar", "sim"),
            new("❌ Cancelar", "cancelar"),
        };
        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, title, options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }

    protected async Task<string> GetMenuTitleAsync()
    {
        var store = await GetCurrentStoreAsync();
        return $"{store?.Name ?? "Atendimento"}\nO que deseja fazer?";
    }

    protected static string StepLabel(int step, string label) => $"📍 Etapa {step}/5 - {label}";

    protected static PollOption BackOption(string label = "↩️ Voltar etapa anterior") => new(label, "voltar_etapa");

    protected static bool IsBackCommand(string text)
    {
        var normalized = (text ?? "").Trim().ToLowerInvariant();
        return normalized is "0" or "voltar" or "voltar_etapa" or "anterior" or "back";
    }

    protected void MarkValid(ConversationSession session)
    {
        session.InvalidResponseCount = 0;
    }

    protected async Task<bool> RegisterInvalidResponseAsync(ConversationSession session, string guidance, CancellationToken ct)
    {
        session.InvalidResponseCount++;
        if (session.InvalidResponseCount >= 3)
        {
            await ResetToMainMenuAsync(session, ct);
            return true;
        }

        await WhatsApp.SendAsync(
            await GetBridgeUrlAsync(),
            session.Phone,
            $"{guidance}\n\nSe preferir, digite *menu* para recomecar.",
            ct);
        return false;
    }

    protected async Task ResetToMainMenuAsync(ConversationSession session, CancellationToken ct)
    {
        ClearSchedulingData(session);
        session.State = ConversationState.AwaitingMenuSelection;
        session.InvalidResponseCount = 0;
        await WhatsApp.SendAsync(
            await GetBridgeUrlAsync(),
            session.Phone,
            "Nao consegui entender sua resposta. Vamos voltar ao menu principal para continuar seu atendimento.",
            ct);
        await SendMainMenuAsync(session, ct);
    }

    protected void ClearSchedulingData(ConversationSession session, bool preserveCustomerName = true)
    {
        if (!preserveCustomerName)
            session.CustomerName = null;
        session.SelectedServiceId = null;
        session.SubscriptionPendingBarberId = null;
        session.SubscriptionPendingBarberNome = null;
        session.SelectedVehicle = null;
        session.SelectedBarberId = null;
        session.SelectedBarberName = null;
        session.SelectedDate = null;
        session.LastPollId = null;
        session.TimeOffset = 0;
        session.SubscriptionPendingPlanId = null;
        session.PendingCancelAppointmentId = null;
        // Abandonar o fluxo (voltar ao menu, reset) cancela o reagendamento pendente —
        // o agendamento original NÃO é removido, pois só seria removido na confirmação.
        session.RescheduleAppointmentId = null;
    }

    protected async Task HandlePollRequiredState(
        ConversationSession session,
        string text,
        string? pollId,
        ConversationState backState,
        string backStepName,
        Func<Task> processor,
        CancellationToken ct)
    {
        if (IsBackCommand(text))
        {
            await GoBackAsync(session, backState, backStepName, ct);
            return;
        }

        if (string.IsNullOrEmpty(pollId))
        {
            if (string.IsNullOrEmpty(session.LastPollId))
            {
                await processor();
                return;
            }

            await RegisterInvalidResponseAsync(
                session,
                "Toque em uma opcao da lista acima para eu continuar.",
                ct);
            return;
        }

        if (!string.IsNullOrEmpty(session.LastPollId) && session.LastPollId != pollId)
        {
            Logger.LogWarning("Poll invalida recebida para {Phone}. Esperado {Expected}, recebido {Received}", session.Phone, session.LastPollId, pollId);
            await RegisterInvalidResponseAsync(
                session,
                "Essa lista ja expirou. Use a lista mais recente ou digite *menu*.",
                ct);
            return;
        }

        await processor();
    }

    protected async Task GoBackAsync(ConversationSession session, ConversationState backState, string backStepName, CancellationToken ct)
    {
        session.State = backState;
        session.LastPollId = null;
        session.InvalidResponseCount = 0;
        ResetSelectionsForState(session, backState);
        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, $"Voltando para {backStepName}...", ct);
        await SendCurrentStepPromptAsync(session, ct);
    }

    protected void ResetSelectionsForState(ConversationSession session, ConversationState state)
    {
        session.LastPollId = null;

        // AwaitingVehicle tem valor de enum no FINAL (8), então não entra na ordenação por (int)state.
        // Tratado explicitamente: ao voltar para o veículo, limpa data/horário mas mantém nome/serviço/veículo.
        if (state == ConversationState.AwaitingVehicle)
        {
            session.SelectedDate = null;
            session.TimeOffset = 0;
            return;
        }

        var step = (int)state;

        if (state == ConversationState.AwaitingName)
        {
            ClearSchedulingData(session, preserveCustomerName: false);
            return;
        }

        if (step < (int)ConversationState.AwaitingName)
        {
            ClearSchedulingData(session, preserveCustomerName: true);
            return;
        }

        if (step <= (int)ConversationState.AwaitingServiceSelection)
        {
            session.SelectedServiceId = null;
            session.SelectedBarberId = null;
            session.SelectedBarberName = null;
            session.SelectedDate = null;
            session.TimeOffset = 0;
            return;
        }

        if (step <= (int)ConversationState.AwaitingBarberSelection)
        {
            session.SelectedBarberId = null;
            session.SelectedBarberName = null;
            session.SelectedDate = null;
            session.TimeOffset = 0;
            return;
        }

        if (step <= (int)ConversationState.AwaitingDateSelection)
        {
            session.SelectedDate = null;
            session.TimeOffset = 0;
            return;
        }

        if (step <= (int)ConversationState.AwaitingTimeSelection)
        {
            if (session.SelectedDate.HasValue)
                session.SelectedDate = session.SelectedDate.Value.Date;
            session.TimeOffset = 0;
        }
    }

    protected async Task SendCurrentStepPromptAsync(ConversationSession session, CancellationToken ct)
    {
        switch (session.State)
        {
            case ConversationState.AwaitingMenuSelection:
                await SendMainMenuAsync(session, ct);
                break;
            case ConversationState.AwaitingName:
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(1, "Identificacao")}\n{AutoResponder.AskName}", ct);
                break;
            case ConversationState.AwaitingServiceSelection:
                await SendServicePollAsync(session, ct);
                break;
            case ConversationState.AwaitingVehicle:
                await SendVehiclePromptAsync(session, ct);
                break;
            case ConversationState.AwaitingBarberSelection:
                await SendBarberPollAsync(session, ct);
                break;
            case ConversationState.AwaitingDateSelection:
                await SendDatePollAsync(session, ct);
                break;
            case ConversationState.AwaitingTimeSelection:
                if (session.SelectedDate.HasValue)
                    await SendTimePollAsync(session, session.SelectedDate.Value, ct);
                break;
            case ConversationState.ConfirmingAppointment:
                await SendConfirmationPollAsync(session, ct);
                break;
            case ConversationState.AwaitingSubscription:
                // Re-envia o prompt adequado ao sub-estado atual (usado em GoBack e reconexão):
                // - Sem nome: pede nome
                // - Com nome, plano pendente: re-envia confirmação
                // - Com nome, sem plano: re-envia enquete de planos
                session.LastPollId = null;
                var subStore = await GetCurrentStoreAsync();
                var subStoreName = subStore?.Name ?? "nossa empresa";
                if (string.IsNullOrWhiteSpace(session.CustomerName))
                {
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        $"👑 *Clube de fidelidade — {subStoreName}*\n\nPara continuar, preciso do seu nome. Como você se chama?", ct);
                }
                else if (session.SubscriptionPendingPlanId.HasValue)
                {
                    var svc2 = new SubscriptionService(Db);
                    var p = svc2.GetPlan(session.SubscriptionPendingPlanId.Value);
                    if (p != null && p.Ativo)
                    {
                        if (session.SubscriptionPendingBarberId == null)
                        {
                            // Plano escolhido mas barbeiro ainda não — retoma na seleção de barbeiro
                            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                                $"👑 *{p.Nome}* selecionado! Agora escolha o profissional:", ct);
                            await SendSubscriptionBarberPollAsync(session, ct);
                        }
                        else
                        {
                            // Barbeiro já escolhido — retoma na confirmação final
                            var barberLabel = session.SubscriptionPendingBarberId > 0
                                ? $"com *{session.SubscriptionPendingBarberNome}*"
                                : "qualquer profissional";
                            var cobertura = SubscriptionService.DescricaoServicosPermitidos(p.ServicosPermitidos);
                            var opts = new List<PollOption>
                            {
                                new("✅ Assinar", "sim"),
                                new("❌ Cancelar", "nao")
                            };
                            var confirmPoll = await WhatsApp.SendPollAsync(
                                await GetBridgeUrlAsync(), session.Phone,
                                $"👑 Confirmar assinatura em *{subStoreName}*?\n\n" +
                                $"*{p.Nome}*\n" +
                                $"✂️ {p.Creditos} uso(s) — {cobertura}\n" +
                                $"💰 R${p.Preco:F2} · ⏱️ {p.DuracaoDias} dias\n" +
                                $"✂️ Profissional: {barberLabel}",
                                opts, ct);
                            if (confirmPoll != null) session.LastPollId = confirmPoll.Id;
                        }
                    }
                    else
                    {
                        // Plano inativo/removido — reinicia seleção
                        session.SubscriptionPendingPlanId = null;
                        session.SubscriptionPendingBarberId = null;
                        session.SubscriptionPendingBarberNome = null;
                        var plansGoBack = new SubscriptionService(Db).GetPlans();
                        if (plansGoBack.Any())
                        {
                            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                                $"Olá, *{session.CustomerName}*! 👑 Escolha seu plano:", ct);
                            await SendSubscriptionPlansPollAsync(session, plansGoBack, ct);
                        }
                        else
                        {
                            session.State = ConversationState.AwaitingMenuSelection;
                            await SendMainMenuAsync(session, ct);
                        }
                    }
                }
                else
                {
                    var plansResume = new SubscriptionService(Db).GetPlans();
                    if (plansResume.Any())
                    {
                        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                            $"Olá, *{session.CustomerName}*! 👑 Escolha seu plano:", ct);
                        await SendSubscriptionPlansPollAsync(session, plansResume, ct);
                    }
                    else
                    {
                        session.State = ConversationState.AwaitingMenuSelection;
                        await SendMainMenuAsync(session, ct);
                    }
                }
                break;
            case ConversationState.AwaitingCancelConfirmation:
                // Se interrompido no meio do fluxo de cancelamento, volta ao menu
                session.PendingCancelAppointmentId = null;
                session.LastPollId = null;
                await SendMainMenuAsync(session, ct);
                break;
            default:
                await SendMainMenuAsync(session, ct);
                break;
        }
    }

    /// <summary>
    /// Envia a enquete de planos de assinatura e atualiza session.LastPollId.
    /// Compartilhado entre AwaitingMenuSelectionState (disparo imediato ao selecionar opção 5)
    /// e AwaitingSubscriptionState (re-exibição após cancelar confirmação).
    /// </summary>
    protected async Task SendSubscriptionPlansPollAsync(
        ConversationSession session,
        List<SubscriptionPlan> plans,
        CancellationToken ct)
    {
        var options = plans
            .Select((p, i) => new PollOption(
                $"👑 {p.Nome} — R${p.Preco:F2} ({p.Creditos} uso{(p.Creditos == 1 ? "" : "s")}/{p.DuracaoDias}d)",
                (i + 1).ToString()))
            .ToList();
        options.Add(new PollOption("↩️ Voltar ao menu", "voltar_etapa"));

        var plansStore = await GetCurrentStoreAsync();
        var plansStoreName = plansStore?.Name ?? "nossa empresa";
        var poll = await WhatsApp.SendPollAsync(
            await GetBridgeUrlAsync(),
            session.Phone,
            $"👑 Clube de fidelidade — {plansStoreName}\nEscolha seu plano:",
            options, ct);

        if (poll != null) session.LastPollId = poll.Id;
    }

    /// <summary>
    /// Envia a enquete de seleção de barbeiro durante o fluxo de assinatura.
    /// A opção "Sem preferência" (qualquer barbeiro) é incluída apenas quando
    /// <c>Active_SubscriptionAnyBarber == "true"</c> (padrão), podendo ser desativada
    /// pela dashboard para forçar o vínculo com um barbeiro específico.
    /// Suporta override por loja: chave <c>Store_{id}_Active_SubscriptionAnyBarber</c>
    /// tem prioridade sobre a chave global.
    /// </summary>
    protected async Task SendSubscriptionBarberPollAsync(ConversationSession session, CancellationToken ct)
    {
        var storeId = TenantService.GetTenantId();
        var barbers = await Db.Set<Barbeiro>()
            .Where(b => b.StoreId == storeId && b.Ativo)
            .OrderBy(b => b.Nome)
            .ToListAsync(ct);

        var options = barbers
            .Select(b => new PollOption($"✂️ {b.Nome}", b.Id.ToString()))
            .ToList();

        // Lê a config respeitando o override por loja (Store_{id}_Active_SubscriptionAnyBarber)
        // com fallback para a chave global. Padrão: true (backwards-compatible).
        var anyBarberConfigs = await Db.SystemConfigs.AsNoTracking()
            .Where(c => c.Key == "Active_SubscriptionAnyBarber"
                     || c.Key == $"Store_{storeId}_Active_SubscriptionAnyBarber")
            .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
        var anyBarberEnabled = (anyBarberConfigs.GetValueOrDefault($"Store_{storeId}_Active_SubscriptionAnyBarber")
            ?? anyBarberConfigs.GetValueOrDefault("Active_SubscriptionAnyBarber")
            ?? "true") == "true";

        if (anyBarberEnabled)
            options.Add(new PollOption("👥 Sem preferência (qualquer profissional)", "0"));

        // IMPORTANTE: usa "sub_back" em vez de "voltar_etapa" para NÃO ser interceptado pelo
        // IsBackCommand externo em AwaitingSubscriptionState.HandleAsync (que captura "voltar_etapa"
        // e manda ao menu principal). "sub_back" é verificado explicitamente em HandleBarberSelectionAsync.
        options.Add(new PollOption("↩️ Voltar ao plano", "sub_back"));

        // Texto da enquete muda conforme a opção estiver disponível ou não
        var pollTitle = anyBarberEnabled
            ? "✂️ Com qual profissional você quer fazer o plano?\n(Créditos só valem com ele)"
            : "✂️ Escolha o profissional para o seu plano:";

        var poll = await WhatsApp.SendPollAsync(
            await GetBridgeUrlAsync(),
            session.Phone,
            pollTitle,
            options, ct);

        if (poll != null) session.LastPollId = poll.Id;
    }

    protected async Task SendMainMenuAsync(ConversationSession session, CancellationToken ct)
    {
        var title = await GetMenuTitleAsync();
        var labels = await GetBusinessLabelsAsync();
        // Menu adaptado ao ramo: a primeira opção usa o rótulo do negócio (ex.: "Agendar lavagem").
        var menuOptions = new List<PollOption>
        {
            new(labels.AgendarLabel, "1"),
            new("📋 Meus agendamentos", "2"),
            new("❌ Cancelar agendamento", "3"),
            new("🔄 Reagendar", "4"),
        };
        // Opção de assinatura apenas para barbearia
        if (await IsBarbershopAsync())
        {
            var menuStore = await GetCurrentStoreAsync();
            var menuStoreName = menuStore?.Name ?? "nossa empresa";
            menuOptions.Add(new PollOption($"👑 Clube de fidelidade — {menuStoreName}", "5"));
        }

        // Walk-in: apenas para lava-jato (e similares) com Active_WalkIn habilitado pelo dono.
        // Cria agendamento imediato sem seleção de data/hora.
        if (await IsCarWashAsync() && Settings.GetBool("Active_WalkIn"))
        {
            menuOptions.Add(new PollOption("⚡ Quero atendimento agora", "6"));
        }

        var menuPoll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, title, menuOptions, ct);
        if (menuPoll != null) session.LastPollId = menuPoll.Id;
    }

    protected async Task SendServicePollAsync(ConversationSession session, CancellationToken ct)
    {
        var labels = await GetBusinessLabelsAsync();
        var serviceOptions = Catalog.GetAll()
            .Select(item => new PollOption($"{item.Name} ({item.DurationMinutes}min) - R${item.Price:0.##}", item.Id.ToString()))
            .ToList();
        var options = serviceOptions.Concat(new[] { BackOption("↩️ Voltar ao nome") }).ToList();
        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(2, "Escolha o servico")}\n{labels.ServicoQuestion}", options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }

    protected async Task SendBarberPollAsync(ConversationSession session, CancellationToken ct)
    {
        var storeId = TenantService.GetTenantId();
        var barbers = await Db.Set<Barbeiro>()
            .Where(b => b.StoreId == storeId && b.Ativo)
            .OrderBy(b => b.Nome)
            .ToListAsync(ct);

        var labels = await GetBusinessLabelsAsync();
        var options = barbers.Select(b => new PollOption($"{labels.ProfissionalEmoji} {b.Nome}", b.Id.ToString())).ToList();
        options.Add(BackOption("↩️ Voltar ao servico"));
        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(3, labels.StepEscolhaProfissional)}\n{labels.PerguntaProfissional}", options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }

    protected async Task SendDatePollAsync(ConversationSession session, CancellationToken ct)
    {
        var options = new List<PollOption>();
        var today = AgendaService.GetBrazilNow().Date;

        int daysChecked = 0;
        while (options.Count < 7 && daysChecked < 14 && session.SelectedServiceId.HasValue)
        {
            var d = today.AddDays(daysChecked);
            var disponiveis = Agenda.GetHorariosDisponiveis(d, session.SelectedServiceId.Value, session.SelectedBarberId);

            if (disponiveis.Any())
            {
                var label = daysChecked == 0 ? "📌 Hoje" : daysChecked == 1 ? "🌤️ Amanha" : $"📅 {d:dd/MM (ddd)}";
                options.Add(new PollOption(label, d.ToString("yyyy-MM-dd")));
            }
            daysChecked++;
        }

        var usesDetails = await UsesSchedulingDetailsAsync();
        var labels = await GetBusinessLabelsAsync();

        // Mesmo critério do AwaitingDateSelectionState: com 1 barbeiro auto-selecionado,
        // o botão "Voltar" na enquete de datas vai para serviço, não para profissional.
        string backLabel;
        ConversationState noDatesBackState;
        if (usesDetails)
        {
            backLabel = $"↩️ Voltar a {labels.DetailStepLabel.ToLowerInvariant()}";
            noDatesBackState = ConversationState.AwaitingVehicle;
        }
        else
        {
            var storeIdForBack = TenantService.GetTenantId();
            var barberCountForBack = await Db.Set<Barbeiro>().CountAsync(b => b.StoreId == storeIdForBack && b.Ativo);
            if (barberCountForBack <= 1)
            {
                backLabel = "↩️ Voltar ao servico";
                noDatesBackState = ConversationState.AwaitingServiceSelection;
            }
            else
            {
                backLabel = "↩️ Voltar ao profissional";
                noDatesBackState = ConversationState.AwaitingBarberSelection;
            }
        }

        // Se nenhuma data disponível nos próximos 14 dias: informa o cliente e volta ao passo anterior
        // em vez de enviar uma enquete com 1 opção (o WhatsApp rejeita enquetes com <2 opções).
        if (options.Count == 0)
        {
            // Mostra "tente outro profissional" apenas quando há múltiplos barbeiros ativos
            // (noDatesBackState == AwaitingBarberSelection). Com 1 barbeiro auto-selecionado,
            // o cliente não pode "escolher outro" — mensagem genérica é mais adequada.
            var motivo = noDatesBackState == ConversationState.AwaitingBarberSelection
                ? "Não encontrei horários disponíveis para este profissional nos próximos 14 dias.\n\nTente escolher outro profissional ou entre em contato com a barbearia."
                : "Não encontrei horários disponíveis nos próximos 14 dias.\n\nEntre em contato com a barbearia para verificar a agenda.";
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, motivo, ct);
            // Retorna ao passo anterior correto (considera auto-seleção de 1 barbeiro)
            session.State = noDatesBackState;
            session.SelectedDate = null;
            session.TimeOffset = 0;
            session.LastPollId = null;
            await SendCurrentStepPromptAsync(session, ct);
            return;
        }

        options.Add(BackOption(backLabel));
        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(4, "Escolha a data")}\n📅 Para qual dia deseja agendar?", options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }

    /// <summary>
    /// Slots de horário exibidos por página na enquete.
    /// Mantido em 9 (e não 10) porque a enquete pode acumular até 3 controles
    /// ("Horários anteriores" + "Ver mais" + "Voltar a data") e o WhatsApp aceita
    /// no máximo 12 opções por enquete. 9 + 3 = 12 → nunca estoura o limite.
    /// IMPORTANTE: o incremento de paginação em AwaitingTimeSelectionState DEVE usar
    /// esta mesma constante, senão a paginação pula ou repete horários.
    /// </summary>
    protected const int TimeSlotsPerPage = 9;

    protected async Task SendTimePollAsync(ConversationSession session, DateTime date, CancellationToken ct)
    {
        // useCache: true — a paginação ("ver mais"/"anteriores") reusa o mesmo cálculo por até 45s
        // em vez de recomputar 5-6 queries a cada toque. A seleção final revalida com dados frescos.
        var horarios = Agenda.GetHorariosDisponiveis(date.Date, session.SelectedServiceId!.Value, session.SelectedBarberId, useCache: true);
        var displaySlots = horarios.Skip(session.TimeOffset).Take(TimeSlotsPerPage).ToList();
        var options = displaySlots.Select(h => new PollOption($"⏰ {h:hh\\:mm}", h.ToString(@"hh\:mm"))).ToList();

        if (horarios.Count > session.TimeOffset + TimeSlotsPerPage) options.Add(new PollOption("➡️ Ver mais horarios", "mais"));
        // Valor "voltar_pagina" (não "voltar") para não ser confundido com IsBackCommand,
        // que interceptaria o clique e jogaria o cliente de volta à seleção de data.
        if (session.TimeOffset > 0) options.Insert(0, new PollOption("⬅️ Horarios anteriores", "voltar_pagina"));
        options.Add(BackOption("↩️ Voltar a data"));

        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(5, "Escolha o horario")}\n{AutoResponder.AskTime}", options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }

    protected async Task SendConfirmationPollAsync(ConversationSession session, CancellationToken ct)
    {
        if (!session.SelectedDate.HasValue) return;
        var options = new List<PollOption>
        {
            new("✅ Confirmar", "sim"),
            BackOption("↩️ Voltar ao horario"),
            new("❌ Cancelar", "nao")
        };

        var labels = await GetBusinessLabelsAsync();
        var usesDetails = await UsesSchedulingDetailsAsync();
        var service = session.SelectedServiceId.HasValue
            ? Catalog.Get(session.SelectedServiceId.Value, includeInactive: true)?.Name ?? $"Serviço #{session.SelectedServiceId.Value}"
            : "-";
        var serviceEmoji = labels.ServiceEmoji;
        // Lava-jato: mostra o veículo no lugar do profissional/box. Barbearia: mostra o profissional.
        var detailLine = usesDetails
            ? $"{labels.DetailLineLabel}: {session.SelectedVehicle ?? "-"}"
            : $"{labels.ProfissionalSingular}: {session.SelectedBarberName ?? "Equipe"}";
        var resumo = $"{serviceEmoji} Servico: {service}\n" +
                     $"{detailLine}\n" +
                     $"📅 Data: {session.SelectedDate.Value:dd/MM/yyyy}\n" +
                     $"⏰ Horario: {session.SelectedDate.Value:HH:mm}";
        var poll = await WhatsApp.SendPollAsync(await GetBridgeUrlAsync(), session.Phone, $"{StepLabel(5, "Confirmacao")}\n{resumo}\n\n✅ Deseja confirmar o agendamento?", options, ct);
        if (poll != null) session.LastPollId = poll.Id;
    }
}
