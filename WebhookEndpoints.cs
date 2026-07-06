using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapPost("/api/webhook/whatsapp", async (
            IncomingMessage msg,
            [FromServices] ConversationStateManager manager,
            [FromServices] NotificationService notifications,
            [FromServices] ILoggerFactory loggerFactory,
            HttpContext ctx,
            ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            if (!msg.StoreId.HasValue || msg.StoreId < 1)
            {
                // Sem StoreId não há como saber a qual negócio a mensagem pertence.
                // REJEITA em vez de assumir a loja 1 — assumir misturaria dados entre lojas
                // (ex.: uma mensagem do lava-jato cairia na barbearia). A bridge-factory
                // sempre define STORE_ID; ausência indica configuração incorreta da bridge.
                var warnLogger = loggerFactory.CreateLogger("WhatsAppWebhook");
                warnLogger.LogError(
                    "Webhook recebido SEM StoreId de {Phone}. Mensagem REJEITADA para nao misturar lojas. " +
                    "Verifique se a Bridge esta sendo iniciada pela bridge-factory com STORE_ID configurado.",
                    msg.Phone);
                return Results.BadRequest(new { error = "StoreId ausente: nao e possivel identificar a loja da mensagem." });
            }

            var tenantId = msg.StoreId.Value;
            tenantService.SetTenantId(tenantId);

            var logger = loggerFactory.CreateLogger("WhatsAppWebhook");

            try
            {
                logger.LogInformation(
                    "Webhook recebido. Phone: {Phone}, StoreId: {StoreId}, PollId: {PollId}, MessageId: {MessageId}, Source: {Source}",
                    msg.Phone,
                    tenantId,
                    msg.PollId ?? "-",
                    msg.Id ?? "-",
                    msg.Source ?? "-");

                if (string.IsNullOrWhiteSpace(msg.PollId))
                {
                    await notifications.NotifyIncomingMessage(msg.Phone, msg.Text, "WhatsApp");
                }
                await manager.HandleAsync(msg.Phone, msg.Text, msg.PollId, msg.Id, ctx.RequestAborted);
                return Results.Ok();
            }
            catch (DbUpdateException)
            {
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Webhook processado com aviso para {Phone}: {Message}", msg.Phone, ex.Message);
                await notifications.NotifySystemAlert(
                    "Erro importante no bot",
                    $"Falha ao processar mensagem de {msg.Phone}: {ex.Message}",
                    NotificationPriority.High);
                return Results.Accepted();
            }
        });

        // Alerta proativo de status do bot (a Bridge avisa quando cai, precisa de QR ou reconecta).
        // Roteado para a loja correta — o dashboard daquela loja vê o alerta em tempo real (SignalR).
        app.MapPost("/api/bridge/status-alert", async (
            BridgeStatusAlertRequest req,
            [FromServices] NotificationService notifications,
            [FromServices] ITenantService tenantService,
            [FromServices] ILoggerFactory loggerFactory,
            HttpContext ctx) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            if (req.StoreId < 1) return Results.BadRequest(new { error = "StoreId obrigatorio" });

            tenantService.SetTenantId(req.StoreId);
            var priority = req.Priority switch
            {
                "Critical" => NotificationPriority.Critical,
                "Medium" => NotificationPriority.Medium,
                "Low" => NotificationPriority.Low,
                _ => NotificationPriority.High
            };

            loggerFactory.CreateLogger("BridgeStatus").LogInformation(
                "Alerta de status da Bridge (Store {StoreId}): {Title}", req.StoreId, req.Title);
            await notifications.NotifySystemAlert(req.Title ?? "Bot WhatsApp", req.Message ?? "", priority);
            return Results.Ok();
        });

        return app;
    }
}

public record BridgeStatusAlertRequest(int StoreId, string? Title, string? Message, string? Priority);
