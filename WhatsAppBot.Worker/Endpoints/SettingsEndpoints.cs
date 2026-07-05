using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

/// <summary>
/// Política centralizada que decide se uma chave de SystemConfigs é específica da loja.
/// Toda chave específica da loja é salva com prefixo <c>Store_{tenantId}_{key}</c>
/// e retornada ao client SEM o prefixo (transparente para o frontend/dashboard).
///
/// Regra: se a chave começar com qualquer um dos prefixos abaixo, ela é por-loja.
/// Adicionar novo prefixo aqui propaga automaticamente para GET e POST/PUT.
/// </summary>
public static class SettingsKeyPolicy
{
    private static readonly string[] StoreSpecificPrefixes =
    [
        // Horários de funcionamento
        "HorarioAbertura",
        "HorarioFechamento",
        // Automações — ativação e mensagens
        "Active_",
        "Msg_",
        // Retenção
        "Retention_",
        // Serviços — limites, durações, preços, nomes, status
        "LimitService_",
        "DurationService_",
        "Service_",
        // Chaves PIX da loja (individuais do barbeiro são Barbeiro_{id}_PixKey, não tocadas aqui)
        "PixKey",
    ];

    /// <summary>
    /// Retorna true se a chave deve ser armazenada com prefixo de loja.
    /// </summary>
    public static bool IsStoreSpecific(string key)
        => StoreSpecificPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Retorna a chave de armazenamento: com prefixo se for específica da loja, ou a chave original.
    /// </summary>
    public static string StorageKey(string key, int tenantId)
        => IsStoreSpecific(key) ? $"Store_{tenantId}_{key}" : key;

    /// <summary>
    /// Prefixo para filtrar overrides de uma loja específica durante o GET.
    /// </summary>
    public static string TenantPrefix(int tenantId) => $"Store_{tenantId}_";
}

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/api/settings", async (HttpContext ctx, AppDbContext db, IOptionsSnapshot<AgendaConfig> config, ServiceCatalogService catalog) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var tid = db.TenantId;

            // Carrega base com horários globais como ponto de partida
            var settings = new Dictionary<string, string>
            {
                ["HorarioAbertura"]  = config.Value.HorarioAbertura.ToString(@"hh\:mm"),
                ["HorarioFechamento"] = config.Value.HorarioFechamento.ToString(@"hh\:mm")
            };

            // Carrega todos os SystemConfigs (globais + todos os overrides de lojas)
            var allConfigs = await db.SystemConfigs.AsNoTracking().ToListAsync(ctx.RequestAborted);

            // Passo 1: Aplica configs globais (chaves sem prefixo Store_)
            var tenantPrefix = SettingsKeyPolicy.TenantPrefix(tid);
            foreach (var cfg in allConfigs.Where(c => !c.Key.StartsWith("Store_", StringComparison.OrdinalIgnoreCase)))
                settings[cfg.Key] = cfg.Value;

            // Passo 2: Aplica overrides da loja atual (Store_{tid}_* sobrescreve o global)
            // Remove o prefixo antes de entregar ao client — o frontend não sabe do prefixo.
            foreach (var cfg in allConfigs.Where(c => c.Key.StartsWith(tenantPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var clientKey = cfg.Key[tenantPrefix.Length..]; // remove "Store_{tid}_"
                settings[clientKey] = cfg.Value;
            }

            // Passo 3: Adiciona catálogo de serviços (já isolado por tenant via query filter)
            foreach (var service in catalog.GetAll(includeInactive: true))
            {
                settings[$"Service_{service.Id}_Name"]   = service.Name;
                settings[$"Service_{service.Id}_Price"]  = service.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                settings[$"Service_{service.Id}_Active"] = service.Active ? "true" : "false";
                settings[$"DurationService_{service.Id}"] = service.DurationMinutes.ToString();
            }

            return Results.Ok(settings);
        });

        app.MapGet("/api/services", (HttpContext ctx, ServiceCatalogService catalog) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            return Results.Ok(catalog.GetAll(includeInactive: true).Select(s => new
            {
                s.Id, Tipo = s.Name, s.Name,
                s.DurationMinutes, s.Price, s.Active
            }));
        });

        async Task<IResult> SaveSettingsAsync(HttpContext ctx, Dictionary<string, string> req, AppDbContext db, IMemoryCache cache, [FromHeader(Name = "X-User-Role")] string? userRole)
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            if (userRole != "admin") return Results.Forbid();

            // ── Validações ────────────────────────────────────────────────────
            if (req.TryGetValue("HorarioAbertura",  out var opening) &&
                req.TryGetValue("HorarioFechamento", out var closing) &&
                TimeSpan.TryParse(opening,  out var openingTime) &&
                TimeSpan.TryParse(closing, out var closingTime) &&
                closingTime <= openingTime)
            {
                return Results.BadRequest(new { error = "Horario de fechamento precisa ser maior que o horario de abertura." });
            }

            foreach (var duration in req.Where(i => i.Key.StartsWith("DurationService_", StringComparison.OrdinalIgnoreCase)))
            {
                if (!int.TryParse(duration.Value, out var minutes) || minutes < 5 || minutes > 480)
                    return Results.BadRequest(new { error = $"Duracao invalida em {duration.Key}. Use entre 5 e 480 minutos." });
            }

            foreach (var limit in req.Where(i => i.Key.StartsWith("LimitService_", StringComparison.OrdinalIgnoreCase)))
            {
                if (!int.TryParse(limit.Value, out var amount) || amount < 0 || amount > 500)
                    return Results.BadRequest(new { error = $"Limite invalido em {limit.Key}. Use zero ou um numero positivo." });
            }

            foreach (var price in req.Where(i => i.Key.StartsWith("Service_", StringComparison.OrdinalIgnoreCase) && i.Key.EndsWith("_Price", StringComparison.OrdinalIgnoreCase)))
            {
                if (!decimal.TryParse(price.Value.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0 || value > 99999)
                    return Results.BadRequest(new { error = $"Preco invalido em {price.Key}. Use um valor positivo." });
            }

            foreach (var active in req.Where(i => i.Key.StartsWith("Service_", StringComparison.OrdinalIgnoreCase) && i.Key.EndsWith("_Active", StringComparison.OrdinalIgnoreCase)))
            {
                if (!bool.TryParse(active.Value, out _))
                    return Results.BadRequest(new { error = $"Status invalido em {active.Key}. Use true ou false." });
            }

            // ── Persistência com isolamento por tenant ─────────────────────────
            var tenantId = db.TenantId;
            foreach (var item in req)
            {
                // SettingsKeyPolicy decide se a chave é por-loja ou global.
                // Todas as chaves de automação, mensagens, serviços e horários são por-loja,
                // garantindo que o admin de Loja 1 nunca sobrescreva configs de Loja 2.
                var storageKey = SettingsKeyPolicy.StorageKey(item.Key, tenantId);

                var existing = await db.SystemConfigs.FindAsync([storageKey], ctx.RequestAborted);
                if (existing == null)
                    db.SystemConfigs.Add(new SystemConfig { Key = storageKey, Value = item.Value });
                else
                    existing.Value = item.Value;
            }

            await db.SaveChangesAsync(ctx.RequestAborted);
            cache.Remove($"BusinessHours_{tenantId}");
            cache.Remove("BusinessHours"); // compat: limpa cache legado

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO AuditLogs (Timestamp, User, Action, Details, StoreId) VALUES (DateTime('now'), {0}, {1}, {2}, {3})",
                "Admin", "UPDATE_SETTINGS", "Alteracao de horarios/configuracoes", db.TenantId);

            return Results.Ok(new { success = true });
        }

        app.MapPost("/api/settings", SaveSettingsAsync);
        app.MapPut("/api/settings",  SaveSettingsAsync);

        return app;
    }
}
