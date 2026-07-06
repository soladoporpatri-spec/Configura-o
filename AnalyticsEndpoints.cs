using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/api/analytics", async (HttpContext ctx, [FromServices] AnalyticsService analytics) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var data = await analytics.GetAnalyticsAsync();
            return Results.Ok(data);
        });

        app.MapGet("/api/stats", (HttpContext ctx, AppDbContext db, ITenantService tenantService, int? barberId, int days = 30) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;
            var hoje = AgendaService.GetBrazilNow().Date;

            var dataInicioAtual = hoje.AddDays(-days);
            var dataInicioAnterior = hoje.AddDays(-(days * 2));

            var tenantId = tenantService.GetTenantId();
            var query = db.Appointments.AsNoTracking();

            if (tenantId != 0)
                query = query.Where(a => a.StoreId == tenantId);

            if (userRole == "barbeiro" && int.TryParse(userBarberIdClaim, out var bId))
                query = query.Where(a => a.BarberId == bId);
            else if (barberId.HasValue)
                query = query.Where(a => a.BarberId == barberId);

            var appointments = query.ToList();
            var atual = appointments.Where(a => a.DateTime.Date >= dataInicioAtual && a.DateTime.Date <= hoje).ToList();
            var anterior = appointments.Where(a => a.DateTime.Date >= dataInicioAnterior && a.DateTime.Date < dataInicioAtual).ToList();

            decimal calcFat(List<Appointment> list) => list.Sum(a => a.Preco);
            double calcTrend(decimal cur, decimal prev) => prev == 0 ? (cur > 0 ? 100 : 0) : Math.Round((double)((cur - prev) / prev) * 100, 1);
            double calcTaxa(List<Appointment> list) => list.Any() ? Math.Round((double)list.Count(a => a.PresencaConfirmada) / list.Count * 100, 1) : 0;

            var fatAtual = calcFat(atual);
            var fatAnterior = calcFat(anterior);
            var phonesAnteriores = appointments.Where(a => a.DateTime.Date < dataInicioAtual).Select(a => a.PhoneNumber).ToHashSet();
            var novosClientesCount = atual.Where(a => !phonesAnteriores.Contains(a.PhoneNumber)).Select(a => a.PhoneNumber).Distinct().Count();

            var stats = new
            {
                Receita = new { Valor = fatAtual, Trend = calcTrend(fatAtual, fatAnterior), Label = "vs. mes anterior" },
                Agendamentos = new { Total = atual.Count, Trend = calcTrend(atual.Count, anterior.Count), Status = atual.Count >= anterior.Count ? "Estavel" : "Em queda" },
                Presenca = new { Taxa = calcTaxa(atual), Trend = calcTrend((decimal)calcTaxa(atual), (decimal)calcTaxa(anterior)) },
                Clientes = new { Novos = novosClientesCount, Status = novosClientesCount > 0 ? "Novo!" : "--" },
                Grafico = Enumerable.Range(0, days + 1)
                    .Select(offset => dataInicioAtual.AddDays(offset).Date)
                    .Where(date => date <= hoje)
                    .Select(date => new { Data = date.ToString("dd/MM"), Quantidade = atual.Count(a => a.DateTime.Date == date) })
                    .ToList(),
                ServicosMaisPopulares = atual
                    .GroupBy(a => a.ServiceId)
                    .OrderByDescending(g => g.Count())
                    .Take(3)
                    .Select(g => new { Servico = g.Key.ToString(), Total = g.Count(), Faturamento = g.Sum(a => a.Preco) })
                    .ToList(),
                TaxaPresenca = calcTaxa(appointments)
            };

            return Results.Ok(stats);
        });

        app.MapGet("/api/reports/appointments", (HttpContext ctx, AppDbContext db, ITenantService tenantService,
            DateTime? from, DateTime? to, int? barberId, string? servico, string? status, int page = 1, int pageSize = 250) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;
            var today = AgendaService.GetBrazilNow().Date;
            var start = (from ?? today.AddDays(-6)).Date;
            var end = (to ?? today).Date.AddDays(1).AddTicks(-1);
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 25, 500);

            var tenantId = tenantService.GetTenantId();
            var query = db.Appointments.AsNoTracking()
                .Where(a => a.DateTime >= start && a.DateTime <= end);

            if (tenantId != 0)
                query = query.Where(a => a.StoreId == tenantId);

            if (userRole == "barbeiro" && int.TryParse(userBarberIdClaim, out var bId))
                query = query.Where(a => a.BarberId == bId);
            else if (barberId.HasValue)
                query = query.Where(a => a.BarberId == barberId);

            if (!string.IsNullOrWhiteSpace(servico) && int.TryParse(servico, out var servicoFiltro))
                query = query.Where(a => a.ServiceId == servicoFiltro);

            if (string.Equals(status, "confirmed", StringComparison.OrdinalIgnoreCase))
                query = query.Where(a => a.PresencaConfirmada);
            else if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                query = query.Where(a => !a.PresencaConfirmada);

            var servicoNomesAnalytics = db.Set<ServicoItem>().AsNoTracking()
                .ToDictionary(s => s.Id, s => s.Nome);

            var total = query.Count();
            var receitaEstimada = (decimal)query.Select(a => (double)a.Preco).Sum();
            var receitaRealizada = (decimal)query.Where(a => a.PresencaConfirmada).Select(a => (double)a.Preco).Sum();
            var cancelledCount = db.AuditLogs.AsNoTracking()
                .Count(l => l.Timestamp >= start && l.Timestamp <= end && l.Action.Contains("CANCEL"));
            var items = query
                .OrderBy(a => a.DateTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsEnumerable()
                .Select(a => new
                {
                    a.Id,
                    a.ContactName,
                    a.PhoneNumber,
                    a.DateTime,
                    ServicoId = a.ServiceId,
                    Servico = servicoNomesAnalytics.TryGetValue(a.ServiceId, out var sNa) ? sNa : $"Serviço #{a.ServiceId}",
                    ServicoCodigo = a.ServiceId.ToString(),
                    a.BarberId,
                    BarberName = a.BarberName ?? "Profissional nao definido",
                    a.Preco,
                    a.DuracaoMinutos,
                    a.PresencaConfirmada,
                    Status = a.PresencaConfirmada ? "Concluido" : "Pendente",
                    FormaAgendamento = string.IsNullOrWhiteSpace(a.Notes) ? "WhatsApp/Bot" : "Dashboard",
                    a.CreatedAt
                })
                .ToList();

            return Results.Ok(new
            {
                data = items,
                summary = new
                {
                    TotalAgendamentos = total,
                    Cancelamentos = cancelledCount,
                    ReceitaEstimada = receitaEstimada,
                    ReceitaRealizada = receitaRealizada
                },
                total,
                page,
                pageSize,
                from = start.ToString("yyyy-MM-dd"),
                to = end.ToString("yyyy-MM-dd")
            });
        });

        return app;
    }
}
