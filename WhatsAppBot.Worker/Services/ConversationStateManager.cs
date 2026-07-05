using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services.States;

namespace WhatsAppBot.Worker.Services;

public class ConversationStateManager
{
    private readonly ConversationSessionStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly WhatsAppClient _whatsapp;
    private readonly ITenantService _tenantService;
    private readonly ILogger<ConversationStateManager> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PhoneLocks = new();
    private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessageIds = new();
    private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessageFingerprints = new();
    private static readonly ConcurrentDictionary<string, RateWindow> RateWindows = new();
    private static readonly TimeSpan MessageIdTtl = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FingerprintTtl = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(20);
    private const int RateLimitMaxMessages = 8;

    public ConversationStateManager(
        ConversationSessionStore store,
        IServiceProvider serviceProvider,
        WhatsAppClient whatsapp,
        ITenantService tenantService,
        ILogger<ConversationStateManager> logger)
    {
        _store = store;
        _serviceProvider = serviceProvider;
        _whatsapp = whatsapp;
        _tenantService = tenantService;
        _logger = logger;
    }


    public async Task HandleAsync(string phone, string text, string? pollId, CancellationToken ct)
    {
        await HandleAsync(phone, text, pollId, null, ct);
    }

    public async Task HandleAsync(string phone, string text, string? pollId, string? messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var tenantId = _tenantService.GetTenantId();
        var normalizedText = NormalizeForIdempotency(text);

        if (!string.IsNullOrWhiteSpace(messageId) && IsDuplicateMessage(messageId))
        {
            _logger.LogInformation("Mensagem duplicada ignorada para {Phone}. MessageId: {MessageId}", phone, messageId);
            return;
        }

        // Fingerprint deduplication restrita a votos de enquete.
        // Mensagens de texto livre têm messageId único — já cobertas pelo IsDuplicateMessage
        // (TTL 20 min) + rate limiter. Aplicar fingerprint a texto causa falsos positivos:
        // um nome digitado na assinatura e repetido segundos depois no agendamento
        // é uma mensagem LEGÍTIMA que seria silenciada indevidamente.
        if (!string.IsNullOrWhiteSpace(pollId))
        {
            var fingerprint = BuildFingerprint(tenantId, phone, normalizedText, pollId);
            if (IsDuplicateFingerprint(fingerprint))
            {
                _logger.LogInformation(
                    "Voto de enquete duplicado ignorado para {Phone}. PollId: {PollId}, Fingerprint: {Fingerprint}",
                    phone, pollId, fingerprint);
                return;
            }
        }

        // Rate limit aplicado APENAS a texto livre. Votos de enquete (pollId presente) são respostas
        // a enquetes que o próprio bot enviou — não são spam e representam o avanço legítimo do fluxo.
        // Assinar o clube de fidelidade e agendar em seguida gera ~10 toques em poucos segundos; contar os votos
        // estourava o limite de 8/20s e o toque em "Confirmar" era DESCARTADO EM SILÊNCIO, deixando o
        // cliente sem saber se foi agendado. Votos seguem protegidos por dedup de fingerprint (8s) +
        // trava por telefone (processamento serializado), então não há risco de flood real.
        if (string.IsNullOrWhiteSpace(pollId) && IsRateLimited(tenantId, phone))
        {
            _logger.LogWarning(
                "Anti-spam do chatbot ignorou mensagem de texto de {Phone}. MessageId: {MessageId}",
                phone,
                messageId ?? "-");
            return;
        }

        var phoneLock = PhoneLocks.GetOrAdd(phone, _ => new SemaphoreSlim(1, 1));
        await phoneLock.WaitAsync(ct);

        try
        {
            await HandleLockedAsync(phone, text, pollId, ct);
        }
        finally
        {
            phoneLock.Release();
        }
    }

    private static bool IsDuplicateMessage(string messageId)
    {
        var now = DateTime.UtcNow;
        PruneByAge(ProcessedMessageIds, now, MessageIdTtl);

        return !ProcessedMessageIds.TryAdd(messageId, now);
    }

    private static bool IsDuplicateFingerprint(string fingerprint)
    {
        var now = DateTime.UtcNow;
        PruneByAge(ProcessedMessageFingerprints, now, FingerprintTtl);

        return !ProcessedMessageFingerprints.TryAdd(fingerprint, now);
    }

    private static void PruneByAge(ConcurrentDictionary<string, DateTime> cache, DateTime now, TimeSpan ttl)
    {
        foreach (var item in cache)
        {
            if (now - item.Value > ttl)
            {
                cache.TryRemove(item.Key, out _);
            }
        }
    }

    private static string BuildFingerprint(int tenantId, string phone, string text, string? pollId)
    {
        var pollPart = string.IsNullOrWhiteSpace(pollId) ? "manual" : $"poll:{pollId.Trim()}";
        return $"{tenantId}|{phone.Trim().ToLowerInvariant()}|{pollPart}|{text}";
    }

    private static string NormalizeForIdempotency(string text) =>
        string.Join(' ', (text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsRateLimited(int tenantId, string phone)
    {
        var now = DateTime.UtcNow;
        PruneRateWindows(now);

        var key = $"{tenantId}|{phone.Trim().ToLowerInvariant()}";
        var window = RateWindows.GetOrAdd(key, _ => new RateWindow());
        lock (window.SyncRoot)
        {
            window.LastSeenAt = now;
            while (window.Events.Count > 0 && now - window.Events.Peek() > RateLimitWindow)
            {
                window.Events.Dequeue();
            }

            if (window.Events.Count >= RateLimitMaxMessages)
            {
                return true;
            }

            window.Events.Enqueue(now);
            return false;
        }
    }

    private static void PruneRateWindows(DateTime now)
    {
        foreach (var item in RateWindows)
        {
            if (now - item.Value.LastSeenAt > RateLimitWindow.Add(TimeSpan.FromMinutes(2)))
            {
                RateWindows.TryRemove(item.Key, out _);
            }
        }
    }

    private static void PruneRuntimeCaches(DateTime now)
    {
        PruneByAge(ProcessedMessageIds, now, MessageIdTtl);
        PruneByAge(ProcessedMessageFingerprints, now, FingerprintTtl);
        PruneRateWindows(now);
    }

    private async Task HandleLockedAsync(string phone, string text, string? pollId, CancellationToken ct)
    {
        var tenantId = _tenantService.GetTenantId();
        var session = await _store.GetOrAddAsync(phone, tenantId,
            p => new ConversationSession { Phone = p, StoreId = tenantId }, ct);
        session.StoreId = tenantId;
        session.LastInteraction = DateTime.UtcNow;

        var normalizedText = text.Trim().ToLowerInvariant();

        try
        {
            if (session.State == ConversationState.Idle && !string.IsNullOrWhiteSpace(pollId))
            {
                _logger.LogInformation("Voto de enquete antigo ignorado para {Phone} em estado Idle. PollId: {PollId}", phone, pollId);
                return;
            }

            // Interrupção global: "oi" ou "menu" reiniciam o fluxo em qualquer estágio
            if ((normalizedText == "oi" || normalizedText == "menu") && session.State != ConversationState.Idle)
            {
                ResetSchedulingData(session);
                session.State = ConversationState.Idle;
                await _whatsapp.SendAsync(GetBridgeUrlForSession(session), session.Phone,
                    "Tudo bem, vou te levar de volta ao menu principal.", ct);
            }

            if (!Enum.IsDefined(typeof(ConversationState), session.State))
            {
                _logger.LogWarning("Estado invalido detectado para {Phone}: {State}. Resetando fluxo.", phone, session.State);
                ResetSchedulingData(session);
                session.State = ConversationState.Idle;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(35));
            var stateHandler = GetStateHandler(session.State);
            _logger.LogInformation(
                "Processando mensagem para {Phone}. Estado: {State}, PollId: {PollId}, LastPollId: {LastPollId}",
                phone,
                session.State,
                pollId ?? "-",
                session.LastPollId ?? "-");
            // EXECUÇÃO ÚNICA — sem re-executar o handler em falha transitória.
            // Handlers têm efeitos colaterais NÃO-idempotentes (consumo de crédito VIP,
            // envio de mensagens, criação de assinatura). Re-executá-los duplicava a
            // confirmação e consumia crédito duas vezes quando um envio falhava no meio.
            // As falhas transitórias de HTTP já são reabsorvidas pelo retry interno do
            // WhatsAppClient (Polly 3x); uma falha que chega aqui é tratada pelo catch
            // abaixo (reset gracioso ao menu), sem repetir os efeitos já aplicados.
            await ExecuteOnceAsync(
                () => stateHandler.HandleAsync(session, text, pollId, timeoutCts.Token),
                phone,
                session.State,
                timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar mensagem para {Phone}", phone);
            ResetSchedulingData(session);
            session.State = ConversationState.Idle;
            try
            {
                await _whatsapp.SendAsync(GetBridgeUrlForSession(session), session.Phone,
                    "Tive uma instabilidade aqui. Vamos recomecar pelo menu principal para continuar com seguranca.", ct);
            }
            catch (Exception sendEx)
            {
                _logger.LogWarning(sendEx, "Falha ao avisar usuario apos erro de fluxo para {Phone}", phone);
            }
        }
        finally
        {
            try
            {
                await _store.UpsertAsync(session, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao persistir sessão de conversa para {Phone}", phone);
            }
        }
    }

    /// <summary>
    /// Executa o handler do estado exatamente UMA vez.
    /// Não há retry de handler: re-executar um fluxo que já enviou mensagens ou consumiu
    /// crédito duplicaria esses efeitos. Falhas transitórias de rede já são absorvidas pelo
    /// retry interno do WhatsAppClient; o que escapar daqui é capturado pelo catch do chamador.
    /// </summary>
    private async Task ExecuteOnceAsync(Func<Task> operation, string phone, ConversationState state, CancellationToken ct)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout no processamento do bot para {Phone} no estado {State}", phone, state);
            throw;
        }
    }

    /// <summary>
    /// Resolve a URL da bridge para a loja da sessão.
    /// Tenta a URL configurada no banco; fallback para a convenção porta = 3000 + storeId.
    /// Usado nos poucos pontos (reset "oi"/"menu" e recovery de erro) onde não há
    /// um ConversationStateBase disponível para chamar GetBridgeUrlAsync().
    /// </summary>
    private string GetBridgeUrlForSession(ConversationSession session)
    {
        var storeId = session.StoreId > 0 ? session.StoreId : 1;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TenantId = storeId;
            // FindAsync síncrono via DbContext identity map (sem await para manter o método síncrono
            // e evitar overhead de async em hot-path raro)
            var store = db.Stores.Find(storeId);
            if (!string.IsNullOrWhiteSpace(store?.BridgeUrl))
                return store.BridgeUrl;
        }
        catch { /* fallback abaixo */ }

        return $"http://127.0.0.1:{3000 + storeId}";
    }

    private static void ResetSchedulingData(ConversationSession session, bool preserveCustomerName = true)
    {
        if (!preserveCustomerName)
            session.CustomerName = null;
        session.SelectedServiceId = null;
        session.SelectedVehicle = null;
        session.SelectedBarberId = null;
        session.SelectedBarberName = null;
        session.SelectedDate = null;
        session.LastPollId = null;
        session.TimeOffset = 0;
        session.InvalidResponseCount = 0;
        session.SubscriptionPendingPlanId = null;
        // Limpa também os campos do barbeiro de assinatura — mesma consistência que ClearSchedulingData.
        // Sem isso, um reset por timeout/erro deixava BarberId residual que podia pular a etapa
        // de seleção de barbeiro na próxima tentativa de assinar o clube de fidelidade.
        session.SubscriptionPendingBarberId = null;
        session.SubscriptionPendingBarberNome = null;
        session.PendingCancelAppointmentId = null;
        // Interrupção ("oi"/"menu") ou recuperação de erro cancela o reagendamento pendente —
        // o agendamento original permanece intacto (só seria removido na confirmação do novo).
        session.RescheduleAppointmentId = null;
    }

    private IConversationState GetStateHandler(ConversationState state)
    {
        return state switch
        {
            ConversationState.Idle => _serviceProvider.GetRequiredService<IdleState>(),
            ConversationState.AwaitingMenuSelection => _serviceProvider.GetRequiredService<AwaitingMenuSelectionState>(),
            ConversationState.AwaitingName => _serviceProvider.GetRequiredService<AwaitingNameState>(),
            ConversationState.AwaitingServiceSelection => _serviceProvider.GetRequiredService<AwaitingServiceSelectionState>(),
            ConversationState.AwaitingVehicle => _serviceProvider.GetRequiredService<AwaitingVehicleState>(),
            ConversationState.AwaitingBarberSelection => _serviceProvider.GetRequiredService<AwaitingBarberSelectionState>(),
            ConversationState.AwaitingDateSelection => _serviceProvider.GetRequiredService<AwaitingDateSelectionState>(),
            ConversationState.AwaitingTimeSelection => _serviceProvider.GetRequiredService<AwaitingTimeSelectionState>(),
            ConversationState.ConfirmingAppointment => _serviceProvider.GetRequiredService<ConfirmingAppointmentState>(),
            ConversationState.AwaitingSubscription => _serviceProvider.GetRequiredService<AwaitingSubscriptionState>(),
            ConversationState.AwaitingCancelConfirmation => _serviceProvider.GetRequiredService<AwaitingCancelConfirmationState>(),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    public async Task CheckTimeoutsAsync(CancellationToken ct)
    {

        var activeTimeout = TimeSpan.FromMinutes(30);
        var idleTimeout = TimeSpan.FromHours(2);
        var now = DateTime.UtcNow;


        foreach (var kv in _store.Sessions)
        {
            var session = kv.Value;
            var timeout = session.State == ConversationState.Idle ? idleTimeout : activeTimeout;
            if ((now - session.LastInteraction) > timeout)
            {
                _logger.LogInformation("Sessao expirada para {Phone} (Store {StoreId}). Estado: {State}. Removendo.",
                    session.Phone, session.StoreId, session.State);
                await _store.RemoveAsync(session.Phone, session.StoreId, ct);
            }
        }

        var removed = await _store.RemoveExpiredAsync(activeTimeout, idleTimeout, ct);
        if (removed > 0)
        {
            _logger.LogInformation("{Count} sessoes antigas removidas do banco durante limpeza do chatbot.", removed);
        }

        PruneRuntimeCaches(now);
    }

    private sealed class RateWindow
    {
        public object SyncRoot { get; } = new();
        public Queue<DateTime> Events { get; } = new();
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    }
}
