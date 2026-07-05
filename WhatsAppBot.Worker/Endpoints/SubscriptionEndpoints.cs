using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        // ── Planos ────────────────────────────────────────────────────────────

        // GET /api/assinaturas/planos
        app.MapGet("/api/assinaturas/planos", (HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            return Results.Ok(svc.GetPlans(includeInactive: true));
        });

        // POST /api/assinaturas/planos
        app.MapPost("/api/assinaturas/planos", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, PlanRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            if (string.IsNullOrWhiteSpace(req.Nome)) return Results.BadRequest("Nome é obrigatório.");
            if (req.Preco < 0) return Results.BadRequest("Preço inválido.");
            if (req.Creditos < 1) return Results.BadRequest("Créditos deve ser ≥ 1.");
            if (req.DuracaoDias < 1) return Results.BadRequest("Duração deve ser ≥ 1 dia.");
            var svc = new SubscriptionService(db);
            var plan = await svc.CreatePlanAsync(storeId, req.Nome, req.Descricao ?? "", req.Preco, req.Creditos, req.DuracaoDias, req.ServicosPermitidos ?? "*");
            return Results.Created($"/api/assinaturas/planos/{plan.Id}", plan);
        });

        // PATCH /api/assinaturas/planos/{id}
        app.MapPatch("/api/assinaturas/planos/{id:int}", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id, PlanRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            var plan = await svc.UpdatePlanAsync(id, req.Nome, req.Descricao, req.Preco > 0 ? req.Preco : null,
                req.Creditos > 0 ? req.Creditos : null, req.DuracaoDias > 0 ? req.DuracaoDias : null, req.Ativo,
                req.ServicosPermitidos);
            return plan == null ? Results.NotFound() : Results.Ok(plan);
        });

        // DELETE /api/assinaturas/planos/{id}
        app.MapDelete("/api/assinaturas/planos/{id:int}", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var plan = db.Set<SubscriptionPlan>().FirstOrDefault(p => p.Id == id && p.StoreId == storeId);
            if (plan == null) return Results.NotFound();
            var hasSubs = db.Set<ClientSubscription>().Any(s => s.PlanId == id && s.Status == SubscriptionStatus.Active);
            if (hasSubs) return Results.Conflict("Existem assinantes ativos neste plano. Desative-o em vez de excluir.");
            db.Set<SubscriptionPlan>().Remove(plan);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ── Assinaturas ───────────────────────────────────────────────────────

        // GET /api/assinaturas
        // Antes de retornar a lista, varre assinaturas Active com EndDate expirada e
        // marca como Expired — expiração lazy sem necessidade de job background.
        app.MapGet("/api/assinaturas", async (HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            await svc.ExpireOverdueAsync(ctx.RequestAborted);
            var list = svc.GetAll();
            return Results.Ok(list.Select(s => ToDto(s)));
        });

        // GET /api/assinaturas/stats
        app.MapGet("/api/assinaturas/stats", (HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            return Results.Ok(new
            {
                ativos = svc.CountActive(),
                pendentes = svc.CountPending(),
                receitaMes = svc.RevenueThisMonth()
            });
        });

        // DELETE /api/assinaturas/canceladas
        app.MapDelete("/api/assinaturas/canceladas", async (HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            var deleted = await svc.DeleteAllCancelledAsync(ctx.RequestAborted);
            return Results.Ok(new { deleted });
        });

        // DELETE /api/assinaturas/{id}
        app.MapDelete("/api/assinaturas/{id:int}", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);

            try
            {
                var sub = await svc.DeleteCancelledAsync(id, ctx.RequestAborted);
                // Idempotente: se a assinatura já não existe (duplo clique, lista desatualizada
                // ou já removida pelo "apagar canceladas"), o estado final desejado já foi atingido.
                // Retornar 404 aqui só gera um erro confuso na dashboard — tratamos como sucesso.
                return Results.Ok(new { deleted = sub == null ? 0 : 1 });
            }
            catch (InvalidOperationException ex)
            {
                // Só bloqueia quando a assinatura existe mas NÃO está cancelada (regra de negócio real).
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // POST /api/assinaturas/{id}/ativar
        app.MapPost("/api/assinaturas/{id:int}/ativar", async (
            HttpContext ctx, AppDbContext db, ITenantService tenantService,
            WhatsAppClient whatsapp, ILoggerFactory loggerFactory, int id) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            var sub = await svc.ActivateAsync(id);
            if (sub == null) return Results.NotFound();

            // Notifica o cliente via WhatsApp — best-effort: falha não cancela a ativação
            try
            {
                var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId);
                var bridgeUrl = string.IsNullOrWhiteSpace(store?.BridgeUrl)
                    ? $"http://127.0.0.1:{3000 + (storeId <= 0 ? 1 : storeId)}"
                    : store.BridgeUrl;

                var storeName = store?.Name ?? "nossa empresa";
                var msg = $"🎉 Seu clube de fidelidade em *{storeName}* foi ativado!\n\n" +
                          $"👑 *{sub.PlanNome}*\n" +
                          $"✂️ {sub.CreditosTotal} uso{(sub.CreditosTotal == 1 ? "" : "s")} disponível{(sub.CreditosTotal == 1 ? "" : "eis")}\n" +
                          $"📅 Válida até {sub.EndDate:dd/MM/yyyy}\n\n" +
                          $"Seus créditos são usados automaticamente ao agendar. Bem-vindo! 💈";

                await whatsapp.SendAsync(bridgeUrl, sub.ClientPhone, msg);
            }
            catch (Exception ex)
            {
                // Log mas não falha o retorno — ativação já foi persistida
                loggerFactory.CreateLogger("SubscriptionEndpoints")
                    .LogWarning(ex, "[FIDELIDADE] Falha ao notificar {Phone} via WhatsApp após ativação #{Id}", sub.ClientPhone, id);
            }

            return Results.Ok(ToDto(sub));
        });

        // POST /api/assinaturas/{id}/cancelar
        app.MapPost("/api/assinaturas/{id:int}/cancelar", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id, CancelRequest? req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            var sub = await svc.CancelAsync(id, req?.Notes);
            return sub == null ? Results.NotFound() : Results.Ok(ToDto(sub));
        });

        // PATCH /api/assinaturas/{id}/notas
        app.MapPatch("/api/assinaturas/{id:int}/notas", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id, NotesRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var sub = db.Set<ClientSubscription>().FirstOrDefault(s => s.Id == id && s.StoreId == storeId);
            if (sub == null) return Results.NotFound();
            sub.Notes = req.Notes;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(sub));
        });

        // GET /api/assinaturas/lookup?phone=... — usado pelo bot/dashboard para checar assinatura de um número
        app.MapGet("/api/assinaturas/lookup", (HttpContext ctx, AppDbContext db, ITenantService tenantService, string phone) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;
            var svc = new SubscriptionService(db);
            var sub = svc.GetActiveByPhone(phone);
            return sub == null ? Results.NotFound() : Results.Ok(ToDto(sub));
        });

        return app;
    }

    private static object ToDto(ClientSubscription s) => new
    {
        id = s.Id,
        storeId = s.StoreId,
        clientPhone = s.ClientPhone,
        clientName = s.ClientName,
        planId = s.PlanId,
        planNome = s.PlanNome,
        planPreco = s.PlanPreco,
        servicosPermitidos = s.ServicosPermitidos,
        servicosLabel = SubscriptionService.DescricaoServicosPermitidos(s.ServicosPermitidos),
        barbeiroId = s.BarbeiroId,
        barbeiroNome = s.BarbeiroNome,
        barbeiroLabel = SubscriptionService.DescricaoBarbeiro(s),
        creditosTotal = s.CreditosTotal,
        creditosUsados = s.CreditosUsados,
        creditosRestantes = s.CreditosRestantes,
        status = s.Status.ToString(),
        statusCode = (int)s.Status,
        startDate = s.StartDate,
        endDate = s.EndDate,
        createdAt = s.CreatedAt,
        notes = s.Notes,
        isEffectivelyActive = s.IsEffectivelyActive,
        isExpired = s.IsExpired
    };
}

record PlanRequest(string Nome, string? Descricao, decimal Preco, int Creditos, int DuracaoDias, bool? Ativo, string? ServicosPermitidos);
record CancelRequest(string? Notes);
record NotesRequest(string? Notes);
