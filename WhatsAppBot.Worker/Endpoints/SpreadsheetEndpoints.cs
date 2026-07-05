using Microsoft.AspNetCore.Mvc;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class SpreadsheetEndpoints
{
    public static IEndpointRouteBuilder MapSpreadsheetEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapPost("/api/google-sheets/sync", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, [FromServices] GoogleSheetsService sheets) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ctx.RequestAborted);
            if (guard.Error != null) return guard.Error;

            var result = await sheets.SyncDetailedAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                synced = result.Synced,
                message = result.Message,
                appointments = result.Appointments,
                clients = result.Clients,
                professionals = result.Professionals,
                logs = result.Logs,
                configured = !string.IsNullOrWhiteSpace(result.WebhookUrl)
            });
        });

        app.MapPost("/api/spreadsheets/update", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, [FromServices] SpreadsheetMaintenanceService spreadsheets, bool syncGoogleSheets = true) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ctx.RequestAborted);
            if (guard.Error != null) return guard.Error;

            var result = await spreadsheets.UpdateAsync(guard.StoreId, syncGoogleSheets, ctx.RequestAborted);
            return Results.Ok(new
            {
                ok = result.Ok,
                message = result.Message,
                xlsxPath = result.XlsxPath,
                xlsxBytes = result.XlsxBytes,
                appointments = result.Appointments,
                googleSheets = result.GoogleSheets is null ? null : new
                {
                    synced = result.GoogleSheets.Synced,
                    message = result.GoogleSheets.Message,
                    appointments = result.GoogleSheets.Appointments,
                    clients = result.GoogleSheets.Clients,
                    professionals = result.GoogleSheets.Professionals,
                    logs = result.GoogleSheets.Logs,
                    configured = !string.IsNullOrWhiteSpace(result.GoogleSheets.WebhookUrl)
                }
            });
        });

        app.MapGet("/api/spreadsheets/status", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, [FromServices] SpreadsheetMaintenanceService spreadsheets) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ctx.RequestAborted);
            if (guard.Error != null) return guard.Error;

            var path = spreadsheets.GetExportPath(guard.StoreId);
            var info = new FileInfo(path);
            return Results.Ok(new
            {
                exists = info.Exists,
                xlsxPath = path,
                xlsxBytes = info.Exists ? info.Length : 0,
                updatedAt = info.Exists ? info.LastWriteTime : (DateTime?)null
            });
        });

        app.MapGet("/api/export", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, [FromServices] SpreadsheetMaintenanceService spreadsheets, bool refresh = false) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ctx.RequestAborted);
            if (guard.Error != null) return guard.Error;

            var path = spreadsheets.GetExportPath(guard.StoreId);
            if (refresh || !File.Exists(path))
            {
                var result = await spreadsheets.UpdateAsync(guard.StoreId, syncGoogleSheets: false, cancellationToken: ctx.RequestAborted);
                if (!result.Ok) return Results.Json(new { error = result.Message }, statusCode: 403);
            }

            if (!File.Exists(path))
                return Results.NotFound(new { error = "Planilha ainda nao foi gerada." });

            return Results.File(path,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"agendamentos_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        });

        return app;
    }
}
