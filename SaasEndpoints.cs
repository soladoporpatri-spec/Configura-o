using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;
using WhatsAppBot.Worker.Services.Modules;

namespace WhatsAppBot.Worker.Endpoints;

public static class SaasEndpoints
{
    public static IEndpointRouteBuilder MapSaasEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapPost("/api/maintenance/safe-cleanup", (HttpContext ctx, [FromServices] IMemoryCache cache) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            if (role == "barbeiro") return Results.Forbid();

            var actions = new List<string>();
            if (cache is MemoryCache memoryCache)
            {
                memoryCache.Compact(1.0);
                actions.Add("Cache em memoria do backend limpo");
            }

            return Results.Ok(new
            {
                ok = true,
                actions,
                preserved = new[]
                {
                    "Banco SQLite",
                    "usuarios e senhas",
                    "lojas/empresas",
                    "sessao do WhatsApp",
                    "agendamentos",
                    "configuracoes",
                    "planilhas"
                },
                timestamp = DateTime.Now
            });
        });

        app.MapGet("/api/modules", async (HttpContext ctx, [FromServices] FeatureAccessService features) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var modules = await features.GetModulesAsync(ctx.RequestAborted);
            return Results.Ok(new
            {
                modules,
                enabled = modules.Where(m => m.Enabled).Select(m => m.Key).ToList()
            });
        });

        app.MapGet("/api/diagnostics", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var storeId = tenantService.GetTenantId();
            if (storeId <= 0) return Results.BadRequest(new { error = "Diagnostico tenant-scoped exige uma loja operacional." });

            db.TenantId = storeId;

            var now = DateTime.Now;
            var store = await db.Stores
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == storeId, ctx.RequestAborted);

            if (store == null) return Results.NotFound(new { error = "Loja nao encontrada." });

            var access = StoreAccessPolicy.Evaluate(store, now);
            var modules = await features.GetModulesAsync(ctx.RequestAborted);

            var users = await db.Users.AsNoTracking().Where(u => u.StoreId == storeId)
                .Select(u => new { u.Role, u.Username, u.Is2FAEnabled })
                .ToListAsync(ctx.RequestAborted);
            var services = await db.Servicos.AsNoTracking().Where(s => s.StoreId == storeId)
                .Select(s => new { s.Ativo, s.Nome })
                .ToListAsync(ctx.RequestAborted);
            var professionals = await db.Barbeiros.AsNoTracking().Where(b => b.StoreId == storeId)
                .Select(b => new { b.Ativo, b.Nome })
                .ToListAsync(ctx.RequestAborted);
            var appointments = await db.Appointments.AsNoTracking().Where(a => a.StoreId == storeId)
                .Select(a => new { a.DateTime, a.Status, a.PhoneNumber, a.Preco })
                .ToListAsync(ctx.RequestAborted);
            var sessions = await db.ConversationSessions.AsNoTracking().Where(s => s.StoreId == storeId)
                .Select(s => new { s.LastInteraction, s.State, s.Phone })
                .ToListAsync(ctx.RequestAborted);
            var activeClientSubscriptions = await db.ClientSubscriptions.AsNoTracking()
                .CountAsync(s => s.StoreId == storeId && s.Status == SubscriptionStatus.Active, ctx.RequestAborted);
            var pendingClientSubscriptions = await db.ClientSubscriptions.AsNoTracking()
                .CountAsync(s => s.StoreId == storeId && s.Status == SubscriptionStatus.Pending, ctx.RequestAborted);
            var techTicketsOpen = await db.OptimizationTickets.AsNoTracking()
                .CountAsync(t => t.StoreId == storeId && t.Status != OptimizationTicketStatus.Concluido && t.Status != OptimizationTicketStatus.Cancelado, ctx.RequestAborted);
            var vehicles = await db.ClientVehicles.AsNoTracking()
                .CountAsync(v => v.StoreId == storeId, ctx.RequestAborted);
            var lastPayment = await db.StorePaymentRecords.AsNoTracking()
                .Where(p => p.StoreId == storeId && p.Status == StorePaymentStatus.Paid)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new { p.Amount, p.Plan, p.PaidUntil, p.ConfirmedAt, p.PaymentMode })
                .FirstOrDefaultAsync(ctx.RequestAborted);
            var logs = await db.AuditLogs.AsNoTracking()
                .Where(l => l.StoreId == storeId)
                .OrderByDescending(l => l.Timestamp)
                .Take(12)
                .Select(l => new { l.Timestamp, l.User, l.Action, l.Details })
                .ToListAsync(ctx.RequestAborted);

            var activeServices = services.Count(s => s.Ativo);
            var admins = users.Count(u => string.Equals(u.Role, "admin", StringComparison.OrdinalIgnoreCase));
            var activeProfessionals = professionals.Count(p => p.Ativo);
            var lastAppointment = appointments.Count > 0 ? appointments.Max(a => (DateTime?)a.DateTime) : null;
            var lastBotInteraction = sessions.Count > 0 ? sessions.Max(s => (DateTime?)s.LastInteraction) : null;
            var expiry = store.ExpiresAt ?? store.SubscriptionExpiry;
            var daysUntilExpiry = expiry == default ? null : (int?)Math.Ceiling((expiry - now).TotalDays);
            var whatsappStatus = string.IsNullOrWhiteSpace(store.BotStatus) ? "unknown" : store.BotStatus.Trim().ToLowerInvariant();

            var checklist = new List<object>
            {
                new { key = "store_access", label = "Loja ativa e liberada", ok = access.CanOperate, severity = access.CanOperate ? "ok" : "critical", message = access.CanOperate ? "Operacao liberada." : access.Message },
                new { key = "admin_user", label = "Administrador cadastrado", ok = admins > 0, severity = admins > 0 ? "ok" : "warning", message = admins > 0 ? $"{admins} admin(s)." : "Crie pelo menos um administrador." },
                new { key = "services", label = "Servicos ativos", ok = activeServices > 0, severity = activeServices > 0 ? "ok" : "warning", message = activeServices > 0 ? $"{activeServices} servico(s) ativo(s)." : "Cadastre servicos antes de liberar a loja." },
                new { key = "whatsapp", label = "WhatsApp/Bot", ok = whatsappStatus is "connected" or "online", severity = whatsappStatus is "connected" or "online" ? "ok" : "info", message = whatsappStatus is "connected" or "online" ? "WhatsApp conectado." : "Conecte ou valide a bridge do WhatsApp." },
                new { key = "subscription", label = "Assinatura da loja", ok = daysUntilExpiry == null || daysUntilExpiry >= 0, severity = daysUntilExpiry == null || daysUntilExpiry >= 0 ? "ok" : "critical", message = daysUntilExpiry == null ? "Sem vencimento formal." : daysUntilExpiry >= 0 ? $"{daysUntilExpiry} dia(s) restantes." : "Assinatura vencida." }
            };

            if (store.BusinessType == BusinessType.Barbershop)
            {
                checklist.Add(new { key = "barbershop_team", label = "Profissionais da barbearia", ok = activeProfessionals > 0, severity = activeProfessionals > 0 ? "ok" : "warning", message = activeProfessionals > 0 ? $"{activeProfessionals} profissional(is) ativo(s)." : "Cadastre barbeiros/profissionais." });
                checklist.Add(new { key = "loyalty", label = "Fidelidade/clube", ok = activeClientSubscriptions > 0 || pendingClientSubscriptions > 0, severity = activeClientSubscriptions > 0 || pendingClientSubscriptions > 0 ? "ok" : "info", message = $"{activeClientSubscriptions} ativa(s), {pendingClientSubscriptions} pendente(s)." });
            }
            else if (store.BusinessType == BusinessType.ComputerOptimization)
            {
                checklist.Add(new { key = "tech_tickets", label = "Fila tecnica", ok = techTicketsOpen > 0, severity = techTicketsOpen > 0 ? "ok" : "info", message = techTicketsOpen > 0 ? $"{techTicketsOpen} ticket(s) aberto(s)." : "Crie um atendimento tecnico de teste." });
            }
            else if (store.BusinessType == BusinessType.CarWash)
            {
                checklist.Add(new { key = "vehicle_history", label = "Historico de veiculos", ok = vehicles > 0, severity = vehicles > 0 ? "ok" : "info", message = vehicles > 0 ? $"{vehicles} veiculo(s) no historico." : "Registre o primeiro veiculo atendido." });
            }

            var critical = checklist.Count(item => string.Equals(GetAnonymousString(item, "severity"), "critical", StringComparison.OrdinalIgnoreCase));
            var warning = checklist.Count(item => string.Equals(GetAnonymousString(item, "severity"), "warning", StringComparison.OrdinalIgnoreCase));
            var healthStatus = critical > 0 ? "critical" : warning > 0 ? "attention" : "ready";

            return Results.Ok(new
            {
                store = new
                {
                    store.Id,
                    store.Name,
                    store.Slug,
                    businessType = store.BusinessType.ToString(),
                    segmentLabel = BusinessTypeLabel(store.BusinessType),
                    plan = PlanCatalog.Normalize(store.Plan),
                    store.IsActive,
                    store.IsSuspended,
                    access = access.Reason,
                    accessMessage = access.Message,
                    daysUntilExpiry,
                    store.BackendUrl,
                    store.BridgeUrl
                },
                health = new
                {
                    status = healthStatus,
                    critical,
                    warning,
                    score = Math.Max(0, 100 - critical * 35 - warning * 15),
                    generatedAt = DateTime.UtcNow
                },
                counts = new
                {
                    users = users.Count,
                    admins,
                    professionals = activeProfessionals,
                    services = activeServices,
                    appointments = appointments.Count,
                    botSessions = sessions.Count,
                    activeClientSubscriptions,
                    pendingClientSubscriptions,
                    openTechTickets = techTicketsOpen,
                    vehicles
                },
                activity = new
                {
                    lastAppointment,
                    lastBotInteraction,
                    recentAppointments = appointments.Count(a => a.DateTime >= now.AddDays(-30)),
                    revenue30d = appointments.Where(a => a.DateTime >= now.AddDays(-30)).Sum(a => (double)a.Preco)
                },
                whatsapp = new
                {
                    status = whatsappStatus,
                    lastInteraction = lastBotInteraction,
                    needsReconnect = whatsappStatus is "unknown" or "disconnected" or "offline" or "qr"
                },
                billing = new
                {
                    plan = PlanCatalog.Normalize(store.Plan),
                    expiry,
                    daysUntilExpiry,
                    lastPayment
                },
                modules,
                checklist,
                logs
            });
        });

        return app;
    }

    private static string BusinessTypeLabel(BusinessType type) =>
        type switch
        {
            BusinessType.CarWash => "Lavajato",
            BusinessType.Pizzeria => "Pizzaria",
            BusinessType.ComputerOptimization => "Tecnologia",
            _ => "Barbearia"
        };

    private static string? GetAnonymousString(object value, string property)
        => value.GetType().GetProperty(property)?.GetValue(value)?.ToString();
}
