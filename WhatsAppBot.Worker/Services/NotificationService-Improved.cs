using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Serviço de notificações em tempo real para o barbeiro.
/// Suporta: email, toast no dashboard, som e vibração no navegador.
/// </summary>
public class NotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private readonly string _sendGridApiKey;
    private readonly string _adminEmail;
    private readonly string _adminPhone;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ITenantService _tenantService;


    public NotificationService(
        ILogger<NotificationService> logger,
        IConfiguration config,
        IHubContext<NotificationHub> hubContext,
        ITenantService tenantService)
    {
        _logger = logger;
        _sendGridApiKey = config["SendGrid:ApiKey"] ?? "";
        _adminEmail = config["AdminEmail"] ?? "admin@barbearia.com";
        _adminPhone = config["AdminPhone"] ?? "";
        _hubContext = hubContext;
        _tenantService = tenantService;
    }


    /// <summary>
    /// Notifica novo agendamento para o barbeiro
    /// </summary>
    public async Task NotifyNewAppointment(string clientName, DateTime dateTime, string service, string phoneNumber = "", string barberName = "", string origin = "WhatsApp/Bot", int? appointmentId = null)
    {
        try
        {
            var eventKey = $"new:{appointmentId?.ToString() ?? phoneNumber}:{dateTime:yyyyMMddHHmm}:{service}:{barberName}";
            var notification = new NotificationMessage
            {
                Id = Guid.NewGuid().ToString(),
                Type = NotificationType.NewAppointment,
                Title = "Novo agendamento",
                Message = $"{clientName} - {service} às {dateTime:HH:mm}",
                Data = new Dictionary<string, string>
                {
                    { "eventKey", eventKey },
                    { "clientName", clientName },
                    { "service", service },
                    { "dateTime", dateTime.ToString("o") },
                    { "date", dateTime.ToString("dd/MM/yyyy") },
                    { "time", dateTime.ToString("HH:mm") },
                    { "phoneNumber", phoneNumber },
                    { "barberName", barberName },
                    { "origin", origin },
                    { "appointmentId", appointmentId?.ToString() ?? "" }
                },
                Timestamp = DateTime.UtcNow,
                Priority = NotificationPriority.High
            };

            // Enviar para Dashboard em tempo real (WebSocket)
            await SendRealtimeNotification(notification);

            // Enviar Email
            await SendEmailNotification(clientName, dateTime, service, phoneNumber);

            // Log
            _logger.LogInformation(
                "Notificacao enviada: Novo agendamento de {ClientName} em {DateTime}. AppointmentId: {AppointmentId}, EventKey: {EventKey}",
                clientName, dateTime, appointmentId, eventKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao enviar notificação de novo agendamento");
        }
    }

    /// <summary>
    /// Notifica confirmação de agendamento
    /// </summary>
    public async Task NotifyAppointmentConfirmed(string clientName, DateTime dateTime, string service, int? appointmentId = null)
    {
        var eventKey = $"confirmed:{appointmentId?.ToString() ?? clientName}:{dateTime:yyyyMMddHHmm}:{service}";
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = NotificationType.ConfirmedAppointment,
            Title = "Agendamento confirmado",
            Message = $"{clientName} confirmou presença em {dateTime:HH:mm}",
            Data = new Dictionary<string, string>
            {
                { "clientName", clientName },
                { "service", service },
                { "dateTime", dateTime.ToString("o") },
                { "eventKey", eventKey },
                { "appointmentId", appointmentId?.ToString() ?? "" }
            },
            Timestamp = DateTime.UtcNow,
            Priority = NotificationPriority.Medium
        };

        await SendRealtimeNotification(notification);
    }

    /// <summary>
    /// Notifica cancelamento de agendamento
    /// </summary>
    public async Task NotifyAppointmentCancelled(string clientName, DateTime dateTime, string service, int? appointmentId = null)
    {
        var eventKey = $"cancelled:{appointmentId?.ToString() ?? clientName}:{dateTime:yyyyMMddHHmm}:{service}";
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = NotificationType.CancelledAppointment,
            Title = "Agendamento cancelado",
            Message = $"Cliente {clientName} cancelou para {dateTime:HH:mm}",
            Data = new Dictionary<string, string>
            {
                { "clientName", clientName },
                { "service", service },
                { "dateTime", dateTime.ToString("o") },
                { "eventKey", eventKey },
                { "appointmentId", appointmentId?.ToString() ?? "" }
            },
            Timestamp = DateTime.UtcNow,
            Priority = NotificationPriority.Medium
        };

        await SendRealtimeNotification(notification);
    }

    public async Task NotifyAppointmentRescheduled(string clientName, DateTime oldDateTime, DateTime newDateTime, string service, int? appointmentId = null)
    {
        var eventKey = $"rescheduled:{appointmentId?.ToString() ?? clientName}:{oldDateTime:yyyyMMddHHmm}:{newDateTime:yyyyMMddHHmm}:{service}";
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = NotificationType.RescheduledAppointment,
            Title = "Agendamento reagendado",
            Message = $"{clientName}: {oldDateTime:dd/MM HH:mm} -> {newDateTime:dd/MM HH:mm}",
            Data = new Dictionary<string, string>
            {
                { "clientName", clientName },
                { "service", service },
                { "oldDateTime", oldDateTime.ToString("o") },
                { "newDateTime", newDateTime.ToString("o") },
                { "eventKey", eventKey },
                { "appointmentId", appointmentId?.ToString() ?? "" }
            },
            Timestamp = DateTime.UtcNow,
            Priority = NotificationPriority.Medium
        };

        await SendRealtimeNotification(notification);
    }

    public async Task NotifySystemAlert(string title, string message, NotificationPriority priority = NotificationPriority.High)
    {
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = NotificationType.SystemAlert,
            Title = title,
            Message = message,
            Timestamp = DateTime.UtcNow,
            Priority = priority
        };

        await SendRealtimeNotification(notification);
    }

    public async Task NotifyIncomingMessage(string phoneNumber, string message, string origin = "WhatsApp")
    {
        var preview = string.IsNullOrWhiteSpace(message)
            ? "Nova mensagem recebida"
            : (message.Length > 120 ? message[..120] + "..." : message);
        var eventKey = $"msg:{phoneNumber}:{DateTime.UtcNow:yyyyMMddHHmmss}";
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = NotificationType.IncomingMessage,
            Title = "Nova mensagem no WhatsApp",
            Message = preview,
            Data = new Dictionary<string, string>
            {
                { "eventKey", eventKey },
                { "phoneNumber", phoneNumber },
                { "origin", origin },
                { "message", preview }
            },
            Timestamp = DateTime.UtcNow,
            Priority = NotificationPriority.Medium
        };

        await SendRealtimeNotification(notification);
    }

    /// <summary>
    /// Notifica falta de cliente
    /// </summary>
    public async Task NotifyNoShow(string clientName, DateTime dateTime, string service)
    {
        var notification = new NotificationMessage
        {
            Id = Guid.NewGuid().ToString(),
            Type = NotificationType.NoShow,
            Title = "Cliente faltoso",
            Message = $"{clientName} faltou no compromisso às {dateTime:HH:mm}",
            Data = new Dictionary<string, string>
            {
                { "clientName", clientName },
                { "service", service },
                { "dateTime", dateTime.ToString("o") }
            },
            Timestamp = DateTime.UtcNow,
            Priority = NotificationPriority.High
        };

        await SendRealtimeNotification(notification);
    }

    /// <summary>
    /// Envia notificação via SignalR em tempo real
    /// </summary>
    private async Task SendRealtimeNotification(NotificationMessage notification)
    {
        try
        {
            // 4.8: carimba o storeId da loja afetada para que cada dashboard mostre apenas
            // os próprios alertas. TenantId=0 (superadmin/sistema) deixa em branco = visível a todos.
            var tenantId = _tenantService.GetTenantId();
            if (tenantId > 0 && !notification.Data.ContainsKey("storeId"))
                notification.Data["storeId"] = tenantId.ToString();

            var eventKey = notification.Data.TryGetValue("eventKey", out var key) ? key : notification.Id;
            await _hubContext.Clients.All.SendAsync("ReceiveNotification", notification);
            _logger.LogInformation(
                "Notificacao enviada via SignalR: {Title}. Type: {Type}, Store: {StoreId}, EventKey: {EventKey}, Priority: {Priority}",
                notification.Title,
                notification.Type,
                tenantId,
                eventKey,
                notification.Priority);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao enviar notificacao via SignalR. Type: {Type}, Title: {Title}", notification.Type, notification.Title);
        }
    }


    /// <summary>
    /// Envia notificação via email
    /// </summary>
    private async Task SendEmailNotification(string clientName, DateTime dateTime, string service, string phoneNumber)
    {
        if (string.IsNullOrEmpty(_sendGridApiKey))
            return;

        try
        {
            var client = new SendGridClient(_sendGridApiKey);
            var from = new EmailAddress("noreply@barbearia.com", "Barbearia SaaS Bot");
            var to = new EmailAddress(_adminEmail, "Profissional");
            var subject = $"Novo agendamento - {clientName}";
            
            var htmlContent = $@"
                <html>
                    <head>
                        <style>
                            body {{ font-family: Arial, sans-serif; background: #f6f7fb; }}
                            .container {{ max-width: 500px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; box-shadow: 0 10px 30px rgba(0,0,0,0.1); }}
                            .header {{ color: #172033; font-size: 24px; font-weight: bold; margin-bottom: 20px; }}
                            .info {{ background: #f9fafc; padding: 15px; border-radius: 6px; margin: 10px 0; }}
                            .label {{ color: #667085; font-size: 12px; font-weight: bold; text-transform: uppercase; }}
                            .value {{ color: #172033; font-size: 18px; font-weight: bold; margin-top: 5px; }}
                            .action {{ text-align: center; margin-top: 20px; }}
                            .button {{ background: #172033; color: white; padding: 12px 24px; border-radius: 6px; text-decoration: none; font-weight: bold; display: inline-block; }}
                            .footer {{ color: #667085; font-size: 12px; margin-top: 20px; text-align: center; border-top: 1px solid #e6e8ef; padding-top: 10px; }}
                        </style>
                    </head>
                    <body>
                        <div class='container'>
                            <div class='header'>Novo agendamento</div>
                            
                            <div class='info'>
                                <div class='label'>Cliente</div>
                                <div class='value'>{clientName}</div>
                            </div>
                            
                            <div class='info'>
                                <div class='label'>Serviço</div>
                                <div class='value'>{service}</div>
                            </div>
                            
                            <div class='info'>
                                <div class='label'>Data e Hora</div>
                                <div class='value'>{dateTime:dd/MM/yyyy HH:mm}</div>
                            </div>

                            {(string.IsNullOrEmpty(phoneNumber) ? "" : $@"
                            <div class='info'>
                                <div class='label'>Telefone</div>
                                <div class='value'>{phoneNumber}</div>
                            </div>
                            ")}
                            
                            <div class='action'>
                                <a href='http://localhost:4000/dashboard-improved.html' class='button'>Abrir Dashboard</a>
                            </div>
                            
                            <div class='footer'>
                                Mensagem automatica da Barbearia SaaS · {DateTime.Now:dd/MM/yyyy HH:mm}
                            </div>
                        </div>
                    </body>
                </html>";

            var plainTextContent = 
                $"Novo Agendamento\n\n" +
                $"Cliente: {clientName}\n" +
                $"Serviço: {service}\n" +
                $"Data e Hora: {dateTime:dd/MM/yyyy HH:mm}\n" +
                (string.IsNullOrEmpty(phoneNumber) ? "" : $"Telefone: {phoneNumber}\n");

            var msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);
            var response = await client.SendEmailAsync(msg);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                _logger.LogInformation("Email de notificação enviado para {Email}", _adminEmail);
            }
            else
            {
                _logger.LogError("Falha ao enviar email: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao enviar email de notificação");
        }
    }
}
