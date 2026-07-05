using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public class AwaitingNameState : ConversationStateBase
{
    public AwaitingNameState(WhatsAppClient whatsapp, AgendaService agenda, SchedulerService scheduler, BusinessHours businessHours, AppDbContext db, ILogger<AwaitingNameState> logger, ITenantService tenantService)
        : base(whatsapp, agenda, scheduler, businessHours, db, logger, tenantService) { }

    public override async Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct)
    {
        if (IsBackCommand(text))
        {
            await GoBackAsync(session, ConversationState.AwaitingMenuSelection, "menu principal", ct);
            return;
        }

        // Mínimo 2 chars: alinha com AwaitingSubscriptionState (que aceita ≥2) e cobre nomes
        // curtos como "Jo", "Li", "Bê" que são nomes reais em português.
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2 || text.Trim().Length > 50)
        {
            await RegisterInvalidResponseAsync(session, "Por favor, digite um nome valido (minimo 2 letras).", ct);
            return;
        }

        MarkValid(session);
        session.CustomerName = text.Trim();

        // Se o nome foi pedido NO MEIO de um agendamento já montado (ex.: a confirmação
        // perdeu o nome e voltou aqui), NÃO reinicia o fluxo do zero. Retoma exatamente de
        // onde parou: com serviço e data/hora já escolhidos, volta direto à confirmação.
        // Evita o sintoma de "o bot pede o nome de novo e o agendamento nunca confirma".
        if (session.SelectedServiceId.HasValue && session.SelectedDate.HasValue)
        {
            session.State = ConversationState.ConfirmingAppointment;
            session.LastPollId = null;
            await SendConfirmationPollAsync(session, ct);
            return;
        }

        session.State = ConversationState.AwaitingServiceSelection;
        await SendServicePollAsync(session, ct);
    }
}
