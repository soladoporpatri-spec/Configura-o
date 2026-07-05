using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class IdleState : ConversationStateBase
{
    public IdleState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<IdleState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        if (!await EnsureStoreUsableAsync(session, ct)) return;

        // ── Intercepção de confirmação de presença ────────────────────────────
        // O ReminderService envia "Confirme presença respondendo *sim* ou *nao*."
        // Se o cliente responder antes de abrir o menu, tratamos aqui para evitar
        // que o bot abra o menu ignorando a resposta, confundindo o cliente.
        var normalized = text.Trim().ToLowerInvariant();
        if (normalized is "sim" or "nao" or "não" or "s" or "n")
        {
            var pending = await Db.Appointments
                .Where(a => a.PhoneNumber == session.Phone
                         && a.ReminderDayBefore
                         && !a.PresencaConfirmada
                         && a.DateTime >= AgendaService.GetBrazilNow()
                         && a.DateTime <= AgendaService.GetBrazilNow().AddDays(2))
                .OrderBy(a => a.DateTime)
                .FirstOrDefaultAsync(ct);

            if (pending != null)
            {
                var bridge = await GetBridgeUrlAsync();
                var svc = Catalog.Get(pending.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{pending.ServiceId}";

                if (normalized is "sim" or "s")
                {
                    pending.PresencaConfirmada = true;
                    await Db.SaveChangesAsync(ct);
                    Logger.LogInformation("[Reminder] Presença confirmada para #{ApptId} por {Phone}", pending.Id, session.Phone);
                    await WhatsApp.SendAsync(bridge, session.Phone,
                        $"✅ Presença confirmada!\n\n{svc}\n{pending.DateTime:dd/MM} às {pending.DateTime:HH:mm}\n\nTe esperamos! 😊", ct);
                    // Mostra menu na sequência — cliente pode querer fazer outra coisa
                    session.State = ConversationState.AwaitingMenuSelection;
                    await SendMainMenuAsync(session, ct);
                }
                else
                {
                    // Cliente disse "não" — informa que o agendamento permanece e oferece o menu para cancelar
                    Logger.LogInformation("[Reminder] Cliente {Phone} recusou presença para #{ApptId}", session.Phone, pending.Id);
                    await WhatsApp.SendAsync(bridge, session.Phone,
                        $"Entendido! Seu agendamento de {svc} em {pending.DateTime:dd/MM} às {pending.DateTime:HH:mm} está mantido.\n\n" +
                        $"Se quiser cancelar, use a opção ❌ do menu abaixo.", ct);
                    session.State = ConversationState.AwaitingMenuSelection;
                    await SendMainMenuAsync(session, ct);
                }

                MarkValid(session);
                return;
            }
        }

        // ── Mensagem de mídia (foto, áudio, vídeo) ──────────────────────────
        // O bridge converte mídia sem legenda em "[image]", "[video]", "[ptt]", etc.
        // Também protege contra body vazio direto. Nunca abrimos o menu para isso —
        // confundiria quem está enviando comprovante de pagamento.
        if (string.IsNullOrWhiteSpace(text) || IsBridgeMediaPlaceholder(text))
        {
            // Assinatura pendente deste cliente → agradece o envio do comprovante
            var hasPendingSub = await Db.Set<ClientSubscription>()
                .Where(s => s.ClientPhone == session.Phone
                         && s.Status == SubscriptionStatus.Pending)
                .AnyAsync(ct);

            if (hasPendingSub)
            {
                var subStore = await GetCurrentStoreAsync();
                var subStoreName = subStore?.Name ?? "nossa empresa";
                await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone,
                    $"✅ Comprovante recebido! Nossa equipe vai confirmar e ativar seu plano em *{subStoreName}* em breve. 💈", ct);
            }
            // Sem assinatura pendente: ignora silenciosamente — mídia não inicia menu
            return;
        }

        // ── Fluxo normal: abre o menu principal ───────────────────────────────
        var store = await GetCurrentStoreAsync();
        Logger.LogInformation("[Bot] Inicio de atendimento — Loja {StoreId} ({StoreName}, {Type}) para {Phone}",
            session.StoreId, store?.Name, store?.BusinessType, session.Phone);

        session.State = ConversationState.AwaitingMenuSelection;
        MarkValid(session);

        // Msg_Welcome: saudação personalizada por loja (configurável no painel → Automações).
        // Se vazia ou não configurada, vai direto para o menu sem mensagem extra.
        var welcomeTemplate = Settings.GetString("Msg_Welcome");
        if (!string.IsNullOrWhiteSpace(welcomeTemplate))
        {
            var ctx = new MessageTemplateContext(
                Nome:         session.CustomerName ?? "",
                Servico:      "",
                Profissional: "",
                Loja:         store?.Name ?? "nossa empresa",
                Data:         "",
                Hora:         ""
            );
            var welcomeMsg = MessageTemplateService.Apply(welcomeTemplate, ctx);
            await WhatsApp.SendAsync(await GetBridgeUrlAsync(), session.Phone, welcomeMsg, ct);
        }

        await SendMainMenuAsync(session, ct);
    }

    /// <summary>
    /// Detecta os placeholders que o bridge insere para mídia sem legenda:
    /// "[image]", "[video]", "[ptt]", "[sticker]", "[document]", "[midia]", etc.
    /// Formato garantido pelo bridge: colchetes sem espaços internos.
    /// </summary>
    private static bool IsBridgeMediaPlaceholder(string text)
    {
        var t = text.Trim();
        return t.Length >= 3
            && t[0] == '['
            && t[^1] == ']'
            && !t.AsSpan(1, t.Length - 2).Contains(' ');
    }
}
