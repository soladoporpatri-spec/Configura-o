using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class OperationalEndpoints
{
    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/health", (AppDbContext db) =>
        {
            try
            {
                return Results.Ok(new
                {
                    ok = db.Database.CanConnect(),
                    service = "backend",
                    timestamp = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });

        app.MapGet("/status", (AppDbContext db) =>
        {
            return Results.Ok(new
            {
                ok = db.Database.CanConnect(),
                service = "backend",
                timestamp = DateTimeOffset.UtcNow
            });
        });

        // GET /api/bot/status — chamado a cada ~12s pelo dashboard.
        // Usa GetStatusFastAsync (sem retry, timeout 4s) para não bloquear o endpoint
        // com o backoff exponencial do retry policy (que soma até +14s por chamada).
        app.MapGet("/api/bot/status", async (WhatsAppClient whatsapp, HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var bridgeUrl = await GetBridgeUrlAsync(db, tenantService);
            if (string.IsNullOrEmpty(bridgeUrl))
                return Results.Problem("Bridge URL nao configurada para esta loja");

            var status = await whatsapp.GetStatusFastAsync(bridgeUrl);
            return status != null
                ? Results.Ok(status)
                : Results.Ok(new
                {
                    whatsappConnected = false,
                    status = "offline",
                    botEnabled = false,
                    phone = (string?)null,
                    pushname = (string?)null
                });
        });

        app.MapGet("/api/bot/qr", async (WhatsAppClient whatsapp, HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var bridgeUrl = await GetBridgeUrlAsync(db, tenantService);
            if (string.IsNullOrEmpty(bridgeUrl))
                return Results.Problem("Bridge URL nao configurada para esta loja");

            var qrData = await whatsapp.GetQrAsync(bridgeUrl);
            return qrData != null
                ? Results.Ok(qrData)
                : Results.Ok(new
                {
                    whatsappConnected = false,
                    status = "offline",
                    botEnabled = false,
                    phone = (string?)null,
                    pushname = (string?)null,
                    qr = (string?)null,
                    qrImage = (string?)null
                });
        });

        app.MapPost("/api/bot/toggle", async (WhatsAppClient whatsapp, HttpContext ctx, AppDbContext db, ITenantService tenantService, [FromBody] JsonElement req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var bridgeUrl = await GetBridgeUrlAsync(db, tenantService);
            if (string.IsNullOrEmpty(bridgeUrl))
                return Results.Problem("Bridge URL nao configurada para esta loja");

            try
            {
                var enabled = req.GetProperty("enabled").GetBoolean();
                var status = await whatsapp.ToggleBotAsync(bridgeUrl, enabled);
                return status != null
                    ? Results.Ok(status)
                    : Results.Problem("Falha ao alterar status do bot");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Erro: {ex.Message}");
            }
        });

        return app;
    }

    private static async Task<string?> GetBridgeUrlAsync(AppDbContext db, ITenantService tenantService)
    {
        var storeId = tenantService.GetTenantId();
        if (storeId == 0) return null;

        var store = await db.Stores.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == storeId);
        return store?.BridgeUrl;
    }
}
