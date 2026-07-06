using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class ServicosEndpoints
{
    public static IEndpointRouteBuilder MapServicosEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        // GET /api/servicos — lista serviços do tenant atual
        app.MapGet("/api/servicos", (HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;

            var catalog = new ServiceCatalogService(db);
            var items = catalog.GetAll(includeInactive: true);
            return Results.Ok(items);
        });

        // POST /api/servicos — cria novo serviço para o tenant
        app.MapPost("/api/servicos", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, ServicoItemRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;

            if (string.IsNullOrWhiteSpace(req.Nome))
                return Results.BadRequest("Nome é obrigatório.");
            if (req.DuracaoMinutos is < 5 or > 480)
                return Results.BadRequest("Duração deve ser entre 5 e 480 minutos.");
            if (req.Preco < 0)
                return Results.BadRequest("Preço não pode ser negativo.");

            var maxOrdem = db.Set<ServicoItem>().AsNoTracking()
                .Where(s => s.StoreId == storeId)
                .Select(s => (int?)s.Ordem).Max() ?? 0;

            var item = new ServicoItem
            {
                StoreId = storeId,
                Nome = req.Nome.Trim(),
                DuracaoMinutos = req.DuracaoMinutos,
                Preco = req.Preco,
                Ativo = req.Ativo ?? true,
                Ordem = req.Ordem ?? maxOrdem + 1,
                OcupaHorario = req.OcupaHorario ?? true,
            };

            db.Set<ServicoItem>().Add(item);
            await db.SaveChangesAsync();
            return Results.Created($"/api/servicos/{item.Id}", item);
        });

        // PATCH /api/servicos/{id} — atualiza serviço existente
        app.MapPatch("/api/servicos/{id:int}", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id, ServicoItemRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;

            var item = await db.Set<ServicoItem>().FirstOrDefaultAsync(s => s.Id == id && s.StoreId == storeId);
            if (item == null) return Results.NotFound();

            if (!string.IsNullOrWhiteSpace(req.Nome)) item.Nome = req.Nome.Trim();
            if (req.DuracaoMinutos is >= 5 and <= 480) item.DuracaoMinutos = req.DuracaoMinutos;
            if (req.Preco >= 0) item.Preco = req.Preco;
            if (req.Ativo.HasValue) item.Ativo = req.Ativo.Value;
            if (req.Ordem.HasValue) item.Ordem = req.Ordem.Value;
            if (req.OcupaHorario.HasValue) item.OcupaHorario = req.OcupaHorario.Value;

            await db.SaveChangesAsync();
            return Results.Ok(item);
        });

        // DELETE /api/servicos/{id} — remove serviço (não permite se tiver agendamentos futuros)
        app.MapDelete("/api/servicos/{id:int}", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;

            var item = await db.Set<ServicoItem>().FirstOrDefaultAsync(s => s.Id == id && s.StoreId == storeId);
            if (item == null) return Results.NotFound();

            var hasFutureAppointments = db.Appointments
                .Any(a => a.StoreId == storeId && a.ServiceId == id && a.DateTime >= DateTime.Now);

            if (hasFutureAppointments)
                return Results.Conflict("Existem agendamentos futuros com este serviço. Desative-o em vez de excluir.");

            db.Set<ServicoItem>().Remove(item);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

record ServicoItemRequest(string Nome, int DuracaoMinutos, decimal Preco, bool? Ativo, int? Ordem, bool? OcupaHorario);
