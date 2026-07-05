using Microsoft.AspNetCore.SignalR;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Hub SignalR para notificacoes em tempo real.
/// </summary>
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Cliente conectado ao NotificationHub: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Cliente desconectado do NotificationHub: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task MarkNotificationAsRead(string notificationId)
    {
        _logger.LogInformation("Notificacao marcada como lida: {NotificationId}", notificationId);
        await Clients.All.SendAsync("NotificationRead", notificationId);
    }
}
