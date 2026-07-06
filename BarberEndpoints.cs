using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class BarberEndpoints
{
    public static IEndpointRouteBuilder MapBarberEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/api/barbeiros", (HttpContext ctx, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var query = db.Set<Barbeiro>().AsQueryable();
            if (!tenantService.IsSuperAdmin())
            {
                var currentStoreId = tenantService.GetTenantId();
                query = query.Where(b => b.StoreId == currentStoreId);
            }

            return Results.Ok(query.OrderBy(b => b.Nome).ToList());
        });

        app.MapPost("/api/barbeiros", async (HttpContext ctx, CriarBarbeiroRequest req, AppDbContext db, [FromServices] AuthService authService, [FromServices] ITenantService tenantService, [FromServices] BusinessHours hours, [FromHeader(Name = "X-User-Role")] string? userRole) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Nome))
                return Results.BadRequest(new { error = "O nome do barbeiro e obrigatorio" });

            var workStart = ParseTimeOrDefault(req.WorkStart, hours.OpeningTime);
            var workEnd = ParseTimeOrDefault(req.WorkEnd, hours.ClosingTime);
            var lunchStart = ParseNullableTime(req.LunchStart);
            var lunchEnd = ParseNullableTime(req.LunchEnd);
            var scheduleError = ValidateSchedule(
                workStart,
                workEnd,
                lunchStart,
                lunchEnd);
            if (scheduleError != null) return Results.BadRequest(new { error = scheduleError });

            var novoBarbeiro = new Barbeiro
            {
                StoreId = tenantService.GetTenantId(),
                Nome = req.Nome,
                Cor = req.Cor ?? "#3498db",
                Ativo = true,
                Especialidade = req.Especialidade ?? "Geral",
                Adicional = req.Adicional ?? "",
                WorkStart = workStart,
                WorkEnd = workEnd,
                LunchStart = lunchStart,
                LunchEnd = lunchEnd,
                WorkingDays = NormalizeWorkingDays(req.WorkingDays),
                BlockedSlotsJson = SerializeJsonObject(req.BlockedSlots),
                CustomHoursJson = SerializeJsonObject(req.CustomHours)
            };

            db.Set<Barbeiro>().Add(novoBarbeiro);
            await db.SaveChangesAsync();

            if (!string.IsNullOrEmpty(req.Password))
            {
                if (userRole != "admin")
                {
                    return Results.Json(new { error = "Apenas administradores podem definir senhas para novos profissionais." }, statusCode: 403);
                }

                db.Users.Add(new User
                {
                    Username = req.Nome,
                    PasswordHash = authService.HashPassword(req.Password),
                    Role = "barbeiro",
                    StoreId = tenantService.GetTenantId(),
                    BarberId = novoBarbeiro.Id
                });
                await db.SaveChangesAsync();
            }

            await LogAction(db, "Dashboard", "CRIAR_BARBEIRO", $"Profissional: {req.Nome}");
            return Results.Created($"/api/barbeiros/{novoBarbeiro.Id}", novoBarbeiro);
        });

        app.MapPatch("/api/barbeiros/{id}", async (HttpContext ctx, int id, BarbeiroUpdateRequest req, AppDbContext db, [FromServices] AuthService authService, ITenantService tenantService, [FromHeader(Name = "X-User-Role")] string? userRole) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var barber = await db.Set<Barbeiro>().FirstOrDefaultAsync(b => b.Id == id && b.StoreId == tenantService.GetTenantId());
            if (barber is null) return Results.NotFound();

            if (!string.IsNullOrEmpty(req.Nome))
            {
                barber.Nome = req.Nome;
                var associatedUser = await db.Users.FirstOrDefaultAsync(u => u.BarberId == id && u.StoreId == tenantService.GetTenantId());
                if (associatedUser != null && associatedUser.Role == "barbeiro")
                {
                    associatedUser.Username = req.Nome;
                }
            }

            if (!string.IsNullOrEmpty(req.Especialidade)) barber.Especialidade = req.Especialidade;
            if (req.Adicional != null) barber.Adicional = req.Adicional;

            if (req.Ativo.HasValue)
            {
                barber.Ativo = req.Ativo.Value;
                await LogAction(db, "Dashboard", req.Ativo.Value ? "ATIVAR_BARBEIRO" : "DESATIVAR_BARBEIRO", $"Profissional: {barber.Nome}");
            }

            if (!string.IsNullOrEmpty(req.Cor))
            {
                barber.Cor = req.Cor;
                await LogAction(db, "Dashboard", "ALTERAR_COR_BARBEIRO", $"Profissional: {barber.Nome}, Cor: {req.Cor}");
            }

            var nextWorkStart = !string.IsNullOrWhiteSpace(req.WorkStart) ? ParseTimeOrDefault(req.WorkStart, barber.WorkStart) : barber.WorkStart;
            var nextWorkEnd = !string.IsNullOrWhiteSpace(req.WorkEnd) ? ParseTimeOrDefault(req.WorkEnd, barber.WorkEnd) : barber.WorkEnd;
            var nextLunchStart = req.LunchStart != null ? ParseNullableTime(req.LunchStart) : barber.LunchStart;
            var nextLunchEnd = req.LunchEnd != null ? ParseNullableTime(req.LunchEnd) : barber.LunchEnd;
            var scheduleError = ValidateSchedule(nextWorkStart, nextWorkEnd, nextLunchStart, nextLunchEnd);
            if (scheduleError != null) return Results.BadRequest(new { error = scheduleError });

            barber.WorkStart = nextWorkStart;
            barber.WorkEnd = nextWorkEnd;
            barber.LunchStart = nextLunchStart;
            barber.LunchEnd = nextLunchEnd;
            if (req.WorkingDays != null) barber.WorkingDays = NormalizeWorkingDays(req.WorkingDays);
            if (req.BlockedSlots != null) barber.BlockedSlotsJson = SerializeJsonObject(req.BlockedSlots);
            if (req.CustomHours != null) barber.CustomHoursJson = SerializeJsonObject(req.CustomHours);

            if (!string.IsNullOrEmpty(req.Password))
            {
                if (userRole != "admin")
                {
                    return Results.Json(new { error = "Apenas administradores podem alterar senhas de outros profissionais." }, statusCode: 403);
                }

                var associatedUser = await db.Users.FirstOrDefaultAsync(u => u.BarberId == id && u.StoreId == tenantService.GetTenantId());
                if (associatedUser != null)
                {
                    associatedUser.PasswordHash = authService.HashPassword(req.Password!);
                    await LogAction(db, "Dashboard", "ALTERAR_SENHA_BARBEIRO", $"Profissional: {barber.Nome}");
                }
            }

            await db.SaveChangesAsync();
            return Results.Ok(barber);
        });

        // ── Horários semanais ─────────────────────────────────────────────────
        // GET /api/barbeiros/{id}/horarios → retorna todos os dias (0-6) configurados
        app.MapGet("/api/barbeiros/{id}/horarios", (HttpContext ctx, int id, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;

            var barber = db.Set<Barbeiro>().FirstOrDefault(b => b.Id == id && b.StoreId == storeId);
            if (barber == null) return Results.NotFound();

            var horarios = db.Set<BarbeiroHorario>()
                .Where(h => h.BarbeiroId == id)
                .OrderBy(h => h.DiaSemana)
                .ToList();

            return Results.Ok(horarios.Select(ToHorarioDto));
        });

        // PUT /api/barbeiros/{id}/horarios → substitui toda a grade semanal
        // Body: array de até 7 objetos { diaSemana, folga, entrada, saida, inicioAlmoco?, fimAlmoco? }
        app.MapPut("/api/barbeiros/{id}/horarios", async (
            HttpContext ctx, int id, AppDbContext db, ITenantService tenantService,
            List<HorarioRequest> body) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();
            var storeId = tenantService.GetTenantId();
            db.TenantId = storeId;

            var barber = await db.Set<Barbeiro>().FirstOrDefaultAsync(b => b.Id == id && b.StoreId == storeId);
            if (barber == null) return Results.NotFound();

            if (body.Any(r => r.DiaSemana < 0 || r.DiaSemana > 6))
                return Results.BadRequest(new { error = "DiaSemana deve ser 0 (Dom) a 6 (Sáb)." });

            if (body.GroupBy(r => r.DiaSemana).Any(g => g.Count() > 1))
                return Results.BadRequest(new { error = "Há dias duplicados na lista." });

            foreach (var req in body.Where(r => !r.Folga))
            {
                var err = ValidateSchedule(
                    ParseTimeOrDefault(req.Entrada, TimeSpan.Zero),
                    ParseTimeOrDefault(req.Saida, TimeSpan.Zero),
                    ParseNullableTime(req.InicioAlmoco),
                    ParseNullableTime(req.FimAlmoco));
                if (err != null)
                    return Results.BadRequest(new { error = $"Dia {req.DiaSemana}: {err}" });
            }

            // Remove registros existentes e recria — estratégia simples e sem conflito de PK
            var existing = db.Set<BarbeiroHorario>().Where(h => h.BarbeiroId == id).ToList();
            db.Set<BarbeiroHorario>().RemoveRange(existing);

            foreach (var req in body)
            {
                db.Set<BarbeiroHorario>().Add(new BarbeiroHorario
                {
                    BarbeiroId  = id,
                    StoreId     = storeId,
                    DiaSemana   = req.DiaSemana,
                    Folga       = req.Folga,
                    Entrada     = req.Folga ? TimeSpan.Zero : ParseTimeOrDefault(req.Entrada, TimeSpan.Zero),
                    Saida       = req.Folga ? TimeSpan.Zero : ParseTimeOrDefault(req.Saida, TimeSpan.Zero),
                    InicioAlmoco = req.Folga ? null : ParseNullableTime(req.InicioAlmoco),
                    FimAlmoco   = req.Folga ? null : ParseNullableTime(req.FimAlmoco)
                });
            }

            await db.SaveChangesAsync();
            await LogAction(db, "Dashboard", "ATUALIZAR_HORARIO_BARBEIRO", $"Profissional #{id}: {body.Count} dias configurados");

            return Results.Ok(body.Select(r => new { r.DiaSemana, r.Folga, r.Entrada, r.Saida }));
        });

        app.MapDelete("/api/barbeiros/{id}", async (HttpContext ctx, int id, AppDbContext db, ITenantService tenantService) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var barber = await db.Set<Barbeiro>().FirstOrDefaultAsync(b => b.Id == id && b.StoreId == tenantService.GetTenantId());
            if (barber is null) return Results.NotFound();

            var storeId = tenantService.GetTenantId();
            var associatedUser = await db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.BarberId == id && u.StoreId == storeId);
            if (associatedUser != null)
            {
                if (associatedUser.Role == "admin")
                {
                    return Results.BadRequest(new { error = "Nao e permitido excluir um administrador atraves da gestao de barbeiros." });
                }

                db.Users.Remove(associatedUser);
            }

            var appointments = await db.Appointments.Where(a => a.BarberId == id).ToListAsync();
            foreach (var appt in appointments)
            {
                appt.BarberId = null;
                appt.BarberName = "(Removido)";
            }

            db.Set<Barbeiro>().Remove(barber);
            await db.SaveChangesAsync();

            await LogAction(db, "Dashboard", "REMOVER_BARBEIRO", $"Profissional: {barber.Nome}");
            return Results.Ok(new { deleted = id });
        });

        return app;
    }

    private static object ToHorarioDto(BarbeiroHorario h) => new
    {
        id           = h.Id,
        barbeiroId   = h.BarbeiroId,
        diaSemana    = h.DiaSemana,
        folga        = h.Folga,
        entrada      = h.Folga ? null : h.Entrada.ToString(@"hh\:mm"),
        saida        = h.Folga ? null : h.Saida.ToString(@"hh\:mm"),
        inicioAlmoco = h.InicioAlmoco?.ToString(@"hh\:mm"),
        fimAlmoco    = h.FimAlmoco?.ToString(@"hh\:mm")
    };

    private static async Task LogAction(AppDbContext db, string user, string action, string details)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO AuditLogs (Timestamp, User, Action, Details, StoreId) VALUES (DateTime('now'), {0}, {1}, {2}, {3})",
                user, action, details, db.TenantId);
        }
        catch { }
    }

    private static TimeSpan ParseTimeOrDefault(string? value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, out var parsed) ? parsed : fallback;

    private static TimeSpan? ParseNullableTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeSpan.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string NormalizeWorkingDays(IEnumerable<int>? days)
    {
        var validDays = (days ?? Enumerable.Range(1, 6))
            .Where(d => d is >= 0 and <= 6)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        return validDays.Count == 0 ? "1,2,3,4,5,6" : string.Join(",", validDays);
    }

    private static string SerializeJsonObject(JsonElement? element, string fallback = "{}")
    {
        if (element is null) return fallback;
        return element.Value.ValueKind == JsonValueKind.Object ? element.Value.GetRawText() : fallback;
    }

    private static string? ValidateSchedule(TimeSpan workStart, TimeSpan workEnd, TimeSpan? lunchStart, TimeSpan? lunchEnd)
    {
        if (workEnd <= workStart)
            return "Carga horaria invalida: o fim precisa ser maior que o inicio.";

        if (lunchStart.HasValue != lunchEnd.HasValue)
            return "Informe inicio e fim da pausa/almoco, ou deixe ambos vazios.";

        if (lunchStart.HasValue && lunchEnd.HasValue)
        {
            if (lunchEnd <= lunchStart)
                return "Pausa/almoco invalido: o fim precisa ser maior que o inicio.";

            if (lunchStart < workStart || lunchEnd > workEnd)
                return "Pausa/almoco precisa ficar dentro da carga horaria do profissional.";
        }

        return null;
    }
}

public record CriarBarbeiroRequest(string Nome, string? Cor, string? Especialidade, string? Adicional, string? Password, string? WorkStart, string? WorkEnd, string? LunchStart, string? LunchEnd, List<int>? WorkingDays, JsonElement? BlockedSlots, JsonElement? CustomHours);

public record BarbeiroUpdateRequest(string? Nome, bool? Ativo, string? Cor, string? Especialidade, string? Adicional, string? Password, string? WorkStart, string? WorkEnd, string? LunchStart, string? LunchEnd, List<int>? WorkingDays, JsonElement? BlockedSlots, JsonElement? CustomHours);

/// <summary>Payload para PUT /api/barbeiros/{id}/horarios (um item por dia da semana).</summary>
public record HorarioRequest(int DiaSemana, bool Folga, string? Entrada, string? Saida, string? InicioAlmoco, string? FimAlmoco);
