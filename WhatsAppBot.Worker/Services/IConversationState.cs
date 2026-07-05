using System.Threading;
using System.Threading.Tasks;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.States;

public interface IConversationState
{
    Task HandleAsync(ConversationSession session, string text, string? pollId, CancellationToken ct);
}