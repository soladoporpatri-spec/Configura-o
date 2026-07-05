using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Services.States;

/// <summary>
/// Gerencia o fluxo de adesão a planos de assinatura via WhatsApp.
/// Somente disponível para barbearia (Barbershop).
///
/// Fluxo completo:
///   0. Coleta nome (se ainda não conhecido)
///   1. Seleciona plano  → session.SubscriptionPendingPlanId
///   2. Seleciona barbeiro → session.SubscriptionPendingBarberId / BarberNome
///        0 = "sem preferência" (créditos valem com qualquer um)
///       >0 = barbeiro específico (créditos só valem com ele)
///   3. Confirmação (sim/não)
///   4. Cria assinatura Pending + instrução de PIX
/// </summary>
public class AwaitingSubscriptionState : ConversationStateBase
{
    private readonly SubscriptionService _subs;

    public AwaitingSubscriptionState(
        WhatsAppClient whatsapp,
        AgendaService agenda,
        SchedulerService scheduler,
        BusinessHours businessHours,
        AppDbContext db,
        ILogger<AwaitingSubscriptionState> logger,
        ITenantService tenantService,
        SubscriptionService subscriptionService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService)
    {
        _subs = subscriptionService;
    }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        if (IsBackCommand(text))
        {
            ClearSchedulingData(session);
            session.State = ConversationState.AwaitingMenuSelection;
            await SendMainMenuAsync(session, ct);
            return;
        }

        // ── Sub-estado 0: aguardando nome ─────────────────────────────────────
        if (string.IsNullOrWhiteSpace(session.CustomerName) && session.SubscriptionPendingPlanId == null)
        {
            await HandleNameCollectionAsync(session, text, pollId, ct);
            return;
        }

        // ── Sub-estado 1: aguardando seleção do plano ─────────────────────────
        if (session.SubscriptionPendingPlanId == null)
        {
            await HandlePlanSelectionAsync(session, text, pollId, ct);
            return;
        }

        // ── Sub-estado 2: aguardando seleção do barbeiro ──────────────────────
        if (session.SubscriptionPendingBarberId == null)
        {
            await HandleBarberSelectionAsync(session, text, pollId, ct);
            return;
        }

        // ── Sub-estado 3: aguardando confirmação (sim/não) ────────────────────
        await HandleConfirmationAsync(session, text, pollId, ct);
    }

    // ── Sub-estado 0: Coleta de nome ─────────────────────────────────────────

    private async Task HandleNameCollectionAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(pollId))
        {
            var store0 = await GetCurrentStoreAsync();
            var storeName0 = store0?.Name ?? "nossa empresa";
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                $"Por favor, escreva seu nome para continuar com a assinatura em *{storeName0}*.", ct);
            return;
        }

        var name = text.Trim();
        if (name.Length < 2 || name.Length > 60)
        {
            await RegisterInvalidResponseAsync(session,
                "Por favor, informe um nome válido (mínimo 2 letras).", ct);
            return;
        }

        session.CustomerName = name;
        // Garante que o fluxo começa do zero — defesa extra contra barberId residual
        // de sessões anteriores que poderia fazer o sub-estado 2 ser pulado.
        session.SubscriptionPendingPlanId    = null;
        session.SubscriptionPendingBarberId  = null;
        session.SubscriptionPendingBarberNome = null;
        session.LastPollId = null;
        MarkValid(session);

        var plans = _subs.GetPlans();
        if (!plans.Any())
        {
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "Nenhum plano disponível no momento. Voltando ao menu...", ct);
            session.State = ConversationState.AwaitingMenuSelection;
            await SendMainMenuAsync(session, ct);
            return;
        }

        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
            $"Olá, *{name}*! 👑 Veja nossos planos exclusivos:", ct);
        await SendSubscriptionPlansPollAsync(session, plans, ct);
    }

    // ── Sub-estado 1: Seleção de plano ───────────────────────────────────────

    private async Task HandlePlanSelectionAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        var plans = _subs.GetPlans();

        if (!plans.Any())
        {
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "Nenhum plano de assinatura disponível no momento. Voltando ao menu principal...", ct);
            session.State = ConversationState.AwaitingMenuSelection;
            await SendMainMenuAsync(session, ct);
            return;
        }

        if (string.IsNullOrEmpty(pollId))
        {
            if (int.TryParse(text.Trim(), out var directIdx) && directIdx >= 1 && directIdx <= plans.Count)
            {
                // Aceita seleção numérica direta — cai no processamento abaixo
            }
            else
            {
                if (session.InvalidResponseCount == 0 && string.IsNullOrEmpty(session.LastPollId))
                {
                    if (!string.IsNullOrWhiteSpace(session.CustomerName))
                        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                            $"👑 Olá, *{session.CustomerName}*! Escolha um plano para continuar:", ct);
                    await SendSubscriptionPlansPollAsync(session, plans, ct);
                }
                else
                {
                    var reset = await RegisterInvalidResponseAsync(session,
                        "Por favor, escolha um plano tocando em uma opção da lista abaixo.", ct);
                    if (!reset) await SendSubscriptionPlansPollAsync(session, plans, ct);
                }
                return;
            }
        }

        if (!string.IsNullOrEmpty(pollId) && !string.IsNullOrEmpty(session.LastPollId) && pollId != session.LastPollId)
        {
            await RegisterInvalidResponseAsync(session, "Use a enquete de planos enviada acima.", ct);
            return;
        }

        if (!int.TryParse(text, out var idx) || idx < 1 || idx > plans.Count)
        {
            await RegisterInvalidResponseAsync(session, "Escolha um dos planos da enquete acima.", ct);
            return;
        }

        var chosen = plans[idx - 1];
        session.SubscriptionPendingPlanId = chosen.Id;
        session.LastPollId = null;

        // Verifica assinatura ativa existente
        var existing = _subs.GetActiveByPhone(session.Phone);
        if (existing != null)
        {
            var existingStore = await GetCurrentStoreAsync();
            var existingStoreName = existingStore?.Name ?? "nossa empresa";
            var barberInfo = SubscriptionService.DescricaoBarbeiro(existing);
            var msg = $"👑 Você já é assinante de *{existingStoreName}*!\n\n" +
                      $"*{existing.PlanNome}*\n" +
                      $"✂️ Créditos restantes: {existing.CreditosRestantes}/{existing.CreditosTotal}\n" +
                      $"✂️ Profissional: {barberInfo}\n" +
                      $"📅 Válido até: {existing.EndDate:dd/MM/yyyy}\n\n" +
                      $"Seus créditos são usados automaticamente ao agendar. 💈";
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, msg, ct);
            session.SubscriptionPendingPlanId = null;
            session.LastPollId = null;
            session.State = ConversationState.AwaitingMenuSelection;
            await SendMainMenuAsync(session, ct);
            return;
        }

        // Verifica assinatura pendente (aguardando confirmação de pagamento pelo admin)
        // Bloqueia uma segunda adesão para evitar duplicatas que confundem o operador.
        var pendingExisting = _subs.GetPendingByPhone(session.Phone);
        if (pendingExisting != null)
        {
            var msg = $"⏳ Você já tem uma adesão ao *{pendingExisting.PlanNome}* aguardando confirmação de pagamento!\n\n" +
                      $"Assim que a equipe confirmar, você receberá uma mensagem de ativação. " +
                      $"Em caso de dúvidas, entre em contato com nossa equipe. 💈";
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, msg, ct);
            session.SubscriptionPendingPlanId = null;
            session.LastPollId = null;
            session.State = ConversationState.AwaitingMenuSelection;
            await SendMainMenuAsync(session, ct);
            return;
        }

        // Plano escolhido → próximo passo: barbeiro
        MarkValid(session);
        await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
            $"👑 *{chosen.Nome}* selecionado!\n\n" +
            $"Agora escolha com qual profissional você quer fazer o plano:", ct);
        await SendSubscriptionBarberPollAsync(session, ct);
    }

    // ── Sub-estado 2: Seleção de barbeiro ────────────────────────────────────

    private async Task HandleBarberSelectionAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        var plan = _subs.GetPlan(session.SubscriptionPendingPlanId!.Value);
        if (plan == null || !plan.Ativo)
        {
            // Plano sumiu enquanto cliente escolhia barbeiro — reinicia
            session.SubscriptionPendingPlanId = null;
            session.LastPollId = null;
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "Este plano não está mais disponível. Escolha outro:", ct);
            var plans = _subs.GetPlans();
            if (plans.Any()) await SendSubscriptionPlansPollAsync(session, plans, ct);
            else             { session.State = ConversationState.AwaitingMenuSelection; await SendMainMenuAsync(session, ct); }
            return;
        }

        // Voto de enquete de outro contexto — ignora
        if (!string.IsNullOrEmpty(pollId) && !string.IsNullOrEmpty(session.LastPollId) && pollId != session.LastPollId)
        {
            await RegisterInvalidResponseAsync(session, "Use a enquete de profissionais enviada acima.", ct);
            return;
        }

        // Sem voto e sem texto numérico válido → re-envia a enquete de barbeiros
        if (string.IsNullOrEmpty(pollId))
        {
            if (!int.TryParse(text.Trim(), out _))
            {
                if (session.InvalidResponseCount == 0 && string.IsNullOrEmpty(session.LastPollId))
                {
                    await SendSubscriptionBarberPollAsync(session, ct);
                }
                else
                {
                    var reset = await RegisterInvalidResponseAsync(session,
                        "Escolha um profissional tocando em uma opção da lista acima.", ct);
                    if (!reset) await SendSubscriptionBarberPollAsync(session, ct);
                }
                return;
            }
        }

        var rawValue = text.Trim();

        // "Voltar ao plano" — usa "sub_back" (não "voltar_etapa") para evitar que o IsBackCommand
        // externo em HandleAsync intercepte o voto antes de chegar aqui e mande ao menu principal.
        if (rawValue == "sub_back" || rawValue == "voltar_etapa")
        {
            session.SubscriptionPendingPlanId = null;
            session.LastPollId = null;
            var plans = _subs.GetPlans();
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                "Tudo bem! Escolha novamente o plano:", ct);
            if (plans.Any()) await SendSubscriptionPlansPollAsync(session, plans, ct);
            else             { session.State = ConversationState.AwaitingMenuSelection; await SendMainMenuAsync(session, ct); }
            return;
        }

        // "Sem preferência" = 0 — só aceita se a opção estiver habilitada.
        // Defesa contra poll antigo (stale): o admin pode ter desativado a opção enquanto
        // o cliente já tinha a enquete aberta no celular. Nesse caso, rejeita o voto "0",
        // re-envia a enquete sem a opção e orienta o cliente a escolher um barbeiro.
        if (rawValue == "0")
        {
            var sid = TenantService.GetTenantId();
            var anyBarberConfigs = await Db.SystemConfigs.AsNoTracking()
                .Where(c => c.Key == "Active_SubscriptionAnyBarber"
                         || c.Key == $"Store_{sid}_Active_SubscriptionAnyBarber")
                .ToDictionaryAsync(c => c.Key, c => c.Value, ct);
            var anyBarberEnabled = (anyBarberConfigs.GetValueOrDefault($"Store_{sid}_Active_SubscriptionAnyBarber")
                ?? anyBarberConfigs.GetValueOrDefault("Active_SubscriptionAnyBarber")
                ?? "true") == "true";

            if (!anyBarberEnabled)
            {
                // Opção removida — rejeita o voto e reenvia a enquete atualizada
                session.LastPollId = null;
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                    "Por favor, escolha um profissional específico para continuar.", ct);
                await SendSubscriptionBarberPollAsync(session, ct);
                return;
            }

            session.SubscriptionPendingBarberId = 0;
            session.SubscriptionPendingBarberNome = null;
            MarkValid(session);
            await SendConfirmPollAsync(session, plan, ct);
            return;
        }

        // ID de barbeiro específico
        if (!int.TryParse(rawValue, out var barberId) || barberId < 0)
        {
            await RegisterInvalidResponseAsync(session, "Escolha um profissional da lista acima.", ct);
            return;
        }

        // Valida se o barbeiro existe e está ativo nesta loja
        var storeId = TenantService.GetTenantId();
        var barbeiro = await Db.Set<Barbeiro>()
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == barberId && b.StoreId == storeId && b.Ativo, ct);

        if (barbeiro == null)
        {
            await RegisterInvalidResponseAsync(session,
                "Profissional não encontrado. Escolha um da lista acima.", ct);
            return;
        }

        session.SubscriptionPendingBarberId = barbeiro.Id;
        session.SubscriptionPendingBarberNome = barbeiro.Nome;
        MarkValid(session);
        await SendConfirmPollAsync(session, plan, ct);
    }

    // ── Sub-estado 3: Confirmação ────────────────────────────────────────────

    private async Task HandleConfirmationAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        var plan = _subs.GetPlan(session.SubscriptionPendingPlanId!.Value);

        // Reconexão: LastPollId nulo significa que a enquete sumiu — re-envia
        if (string.IsNullOrEmpty(pollId) && string.IsNullOrEmpty(session.LastPollId))
        {
            if (plan != null && plan.Ativo)
            {
                await SendConfirmPollAsync(session, plan, ct);
            }
            else
            {
                // Plano inativo → reinicia do zero
                session.SubscriptionPendingPlanId = null;
                session.SubscriptionPendingBarberId = null;
                session.SubscriptionPendingBarberNome = null;
                var plans = _subs.GetPlans();
                if (plans.Any()) await SendSubscriptionPlansPollAsync(session, plans, ct);
                else             { session.State = ConversationState.AwaitingMenuSelection; await SendMainMenuAsync(session, ct); }
            }
            return;
        }

        await HandlePollRequiredState(session, text, pollId,
            ConversationState.AwaitingSubscription, "planos", async () =>
        {
            var normalized = text.Trim().ToLowerInvariant();

            if (normalized == "nao" || normalized == "cancelar" || normalized == "voltar")
            {
                // Volta ao passo de barbeiro (mantém o plano escolhido)
                session.SubscriptionPendingBarberId = null;
                session.SubscriptionPendingBarberNome = null;
                session.LastPollId = null;
                if (plan != null && plan.Ativo)
                {
                    await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                        "Sem problema! Escolha o profissional novamente:", ct);
                    await SendSubscriptionBarberPollAsync(session, ct);
                }
                else
                {
                    session.SubscriptionPendingPlanId = null;
                    var plans = _subs.GetPlans();
                    if (plans.Any()) await SendSubscriptionPlansPollAsync(session, plans, ct);
                    else             { session.State = ConversationState.AwaitingMenuSelection; await SendMainMenuAsync(session, ct); }
                }
                return;
            }

            if (normalized != "sim" && normalized != "confirmar" && normalized != "assinar")
            {
                await RegisterInvalidResponseAsync(session, "Escolha Assinar ou Cancelar na enquete.", ct);
                return;
            }

            // ── Validações finais ─────────────────────────────────────────────
            if (plan == null || !plan.Ativo)
            {
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                    "Este plano não está mais disponível. Voltando ao menu.", ct);
                session.SubscriptionPendingPlanId = null;
                session.SubscriptionPendingBarberId = null;
                session.SubscriptionPendingBarberNome = null;
                session.State = ConversationState.AwaitingMenuSelection;
                await SendMainMenuAsync(session, ct);
                return;
            }

            // ── Criar assinatura pendente ─────────────────────────────────────
            var storeId = TenantService.GetTenantId();
            var barbId  = session.SubscriptionPendingBarberId;
            var barbNome = session.SubscriptionPendingBarberNome;

            var sub = await _subs.CreatePendingAsync(
                storeId, session.Phone, session.CustomerName ?? "Cliente",
                plan.Id, barbId, barbNome, ct);

            Logger.LogInformation(
                "[Assinatura] Nova assinatura PENDENTE #{Id} — {Phone} plano {Plano} barbeiro {Barbeiro} loja {StoreId}",
                sub.Id, session.Phone, plan.Nome,
                barbId > 0 ? barbNome : "qualquer", storeId);

            // Mensagem de confirmação com todos os detalhes
            // GetPixKey prioriza PIX do barbeiro > PIX da loja > PIX global
            var confirmStoreName = (await GetCurrentStoreAsync())?.Name ?? "nossa empresa";
            var pixKey = GetPixKey(storeId, barbId);
            var pixRecipient = barbId > 0 && !string.IsNullOrEmpty(pixKey)
                ? $" (para *{barbNome}*)"
                : "";
            var pixLine = string.IsNullOrEmpty(pixKey)
                ? "entre em contato com nossa equipe para enviar o pagamento."
                : $"faça o PIX de *R${plan.Preco:F2}*{pixRecipient} para a chave:\n*{pixKey}*";

            var cobertura = SubscriptionService.DescricaoServicosPermitidos(plan.ServicosPermitidos);
            var barberInfo = barbId > 0
                ? $"✂️ Profissional vinculado: *{barbNome}* (créditos só valem com ele)\n"
                : "✂️ Profissional: qualquer profissional da equipe\n";
            var nome = session.CustomerName ?? "Membro";

            var confirmMsg =
                $"🎉 Boa escolha, *{nome}*! Bem-vindo a *{confirmStoreName}*!\n\n" +
                $"👑 *{plan.Nome}*\n" +
                $"✂️ {plan.Creditos} uso(s) incluídos — {cobertura}\n" +
                barberInfo +
                $"⏱️ Validade: {plan.DuracaoDias} dias após ativação\n\n" +
                $"Para ativar seu acesso, {pixLine}\n\n" +
                $"Assim que confirmarmos seu pagamento, você receberá uma mensagem de ativação. 💈";

            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, confirmMsg, ct);

            MarkValid(session);
            session.SubscriptionPendingPlanId = null;
            session.SubscriptionPendingBarberId = null;
            session.SubscriptionPendingBarberNome = null;
            session.LastPollId = null;
            // Reabre o menu após a adesão — cliente pode agendar em seguida ou consultar agenda.
            session.State = ConversationState.AwaitingMenuSelection;
            await SendMainMenuAsync(session, ct);
        }, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SendConfirmPollAsync(ConversationSession session, SubscriptionPlan plan, CancellationToken ct)
    {
        var cobertura = SubscriptionService.DescricaoServicosPermitidos(plan.ServicosPermitidos);
        var barbId = session.SubscriptionPendingBarberId ?? 0;
        var barberLine = barbId > 0
            ? $"✂️ Profissional: *{session.SubscriptionPendingBarberNome}*\n"
            : "✂️ Profissional: qualquer profissional\n";

        var confirmStore = await GetCurrentStoreAsync();
        var confirmPollStoreName = confirmStore?.Name ?? "nossa empresa";
        var options = new List<PollOption>
        {
            new("✅ Assinar", "sim"),
            new("❌ Cancelar", "nao")
        };

        var poll = await WhatsApp.SendPollAsync(
            await GetBridgeUrlAsync(),
            session.Phone,
            $"👑 Confirmar assinatura em *{confirmPollStoreName}*?\n\n" +
            $"*{plan.Nome}*\n" +
            $"✂️ {plan.Creditos} uso(s) — {cobertura}\n" +
            barberLine +
            $"💰 R${plan.Preco:F2} · ⏱️ {plan.DuracaoDias} dias\n\n" +
            $"Após confirmar, realizamos o PIX e você já é membro!",
            options, ct);

        if (poll != null) session.LastPollId = poll.Id;
    }

    /// <summary>
    /// Resolve a chave PIX para a mensagem de confirmação da assinatura.
    /// Prioridade: PIX do barbeiro escolhido → PIX da loja → PIX global.
    /// </summary>
    private string? GetPixKey(int storeId, int? barbId = null)
    {
        try
        {
            // 1ª prioridade: chave individual do barbeiro (permite que cada profissional
            // receba pagamentos direto, sem passar pelo caixa da loja)
            if (barbId is > 0)
            {
                var perBarber = Db.Set<SystemConfig>()
                    .AsNoTracking()
                    .FirstOrDefault(c => c.Key == $"Barbeiro_{barbId}_PixKey");
                if (perBarber != null && !string.IsNullOrWhiteSpace(perBarber.Value))
                    return perBarber.Value;
            }

            // 2ª prioridade: chave da loja específica
            var perStore = Db.Set<SystemConfig>()
                .AsNoTracking()
                .FirstOrDefault(c => c.Key == $"Store_{storeId}_PixKey");
            if (perStore != null && !string.IsNullOrWhiteSpace(perStore.Value))
                return perStore.Value;

            // 3ª prioridade: chave global (fallback legacy)
            var global = Db.Set<SystemConfig>()
                .AsNoTracking()
                .FirstOrDefault(c => c.Key == "PixKey");
            return global?.Value;
        }
        catch { return null; }
    }
}
