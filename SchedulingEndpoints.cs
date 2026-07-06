using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/api/agendamentos", (HttpContext ctx, AppDbContext db, ITenantService tenantService,
            string? data, string? servico, string? busca, int? barberId, int page = 1, int pageSize = 50) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;

            var query = db.Appointments.Where(a => a.Status == "ativo").AsQueryable();
            if (!tenantService.IsSuperAdmin()) query = query.Where(a => a.StoreId == tenantService.GetTenantId());

            var barberColors = db.Set<Barbeiro>().AsNoTracking().ToDictionary(b => b.Id, b => b.Cor);

            if (userRole == "barbeiro" && int.TryParse(userBarberIdClaim, out var bId))
                query = query.Where(a => a.BarberId == bId);
            else if (barberId.HasValue)
                query = query.Where(a => a.BarberId == barberId);

            if (data != null && DateTime.TryParse(data, out var dataFiltro))
                query = query.Where(a => a.DateTime.Date == dataFiltro.Date);
            else
                query = query.Where(a => a.DateTime >= DateTime.Now);

            if (servico != null && int.TryParse(servico, out var servicoFiltro))
                query = query.Where(a => a.ServiceId == servicoFiltro);

            if (!string.IsNullOrWhiteSpace(busca))
                query = query.Where(a => a.ContactName.ToLower().Contains(busca.ToLower()) || a.PhoneNumber.Contains(busca));

            var servicoNomes = db.Set<ServicoItem>().AsNoTracking()
                .ToDictionary(s => s.Id, s => s.Nome);

            // COUNT removido: o dashboard usa apenas agendRes.data e nunca agendRes.total.
            // Manter o .Count() separado adicionava 1 query ao banco a cada refresh de 15s sem benefício.
            var result = query
                .OrderBy(a => a.DateTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking()
                .AsEnumerable()
                .Select(a => new
                {
                    a.Id,
                    PhoneNumber = MaskPhone(a.PhoneNumber),
                    a.ContactName,
                    a.Notes,
                    a.DateTime,
                    ServicoId = a.ServiceId,
                    Servico = servicoNomes.TryGetValue(a.ServiceId, out var sNome) ? sNome : $"Serviço #{a.ServiceId}",
                    a.DuracaoMinutos,
                    a.Preco,
                    a.PresencaConfirmada,
                    a.BarberId,
                    a.BarberName,
                    BarberColor = a.BarberId.HasValue && barberColors.ContainsKey(a.BarberId.Value) ? barberColors[a.BarberId.Value] : "#ccc"
                })
                .ToList();

            return Results.Ok(new { data = result, page, pageSize });
        });

        app.MapGet("/api/hoje", (HttpContext ctx, AppDbContext db, ITenantService tenantService, int? barberId) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;
            var tenantId = tenantService.GetTenantId();
            var barberColors = db.Set<Barbeiro>().AsNoTracking()
                .Where(b => b.StoreId == tenantId)
                .ToDictionary(b => b.Id, b => b.Cor);

            var hoje = AgendaService.GetBrazilNow().Date;
            var query = db.Appointments.Where(a => a.StoreId == tenantId && a.DateTime.Date == hoje && a.Status == "ativo");

            if (userRole == "barbeiro" && int.TryParse(userBarberIdClaim, out var bId))
                query = query.Where(a => a.BarberId == bId);
            else if (barberId.HasValue)
                query = query.Where(a => a.BarberId == barberId);

            var servicoNomesHoje = db.Set<ServicoItem>().AsNoTracking()
                .ToDictionary(s => s.Id, s => s.Nome);

            var result = query
                .OrderBy(a => a.DateTime)
                .AsNoTracking()
                .AsEnumerable()
                .Select(a => new
                {
                    a.Id,
                    PhoneNumber = MaskPhone(a.PhoneNumber),
                    a.ContactName,
                    a.Notes,
                    a.DateTime,
                    ServicoId = a.ServiceId,
                    Servico = servicoNomesHoje.TryGetValue(a.ServiceId, out var sNh) ? sNh : $"Serviço #{a.ServiceId}",
                    a.DuracaoMinutos,
                    a.Preco,
                    a.PresencaConfirmada,
                    a.BarberId,
                    a.BarberName,
                    BarberColor = a.BarberId.HasValue && barberColors.ContainsKey(a.BarberId.Value) ? barberColors[a.BarberId.Value] : "#ccc"
                })
                .ToList();

            return Results.Ok(result);
        });

        app.MapGet("/api/semana", (HttpContext ctx, AppDbContext db, ITenantService tenantService, int? barberId) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;
            var tenantId = tenantService.GetTenantId();
            var inicio = AgendaService.GetBrazilNow().Date;
            var fim = inicio.AddDays(7);

            var query = db.Appointments.Where(a => a.StoreId == tenantId && a.DateTime.Date >= inicio && a.DateTime.Date <= fim && a.Status == "ativo");

            if (userRole == "barbeiro" && int.TryParse(userBarberIdClaim, out var bId))
                query = query.Where(a => a.BarberId == bId);
            else if (barberId.HasValue)
                query = query.Where(a => a.BarberId == barberId);

            var servicoNomesSemana = db.Set<ServicoItem>().AsNoTracking()
                .ToDictionary(s => s.Id, s => s.Nome);

            var result = query
                .OrderBy(a => a.DateTime)
                .AsEnumerable()
                .GroupBy(a => a.DateTime.Date)
                .Select(g => new
                {
                    Data = g.Key.ToString("dd/MM/yyyy"),
                    DiaSemana = g.Key.DayOfWeek.ToString(),
                    Total = g.Count(),
                    Faturamento = g.Sum(a => a.Preco),
                    Agendamentos = g.Select(a => new
                    {
                        a.Id,
                        a.ContactName,
                        a.Notes,
                        a.DateTime,
                        ServicoId = a.ServiceId,
                        Servico = servicoNomesSemana.TryGetValue(a.ServiceId, out var sNs) ? sNs : $"Serviço #{a.ServiceId}",
                        a.DuracaoMinutos,
                        a.Preco,
                        a.PresencaConfirmada,
                        a.BarberId,
                        a.BarberName
                    }).ToList()
                })
                .ToList();

            return Results.Ok(result);
        });

        app.MapGet("/api/horarios-livres", (HttpContext ctx, AppDbContext db, ITenantService tenantService,
            [FromServices] AgendaService agenda, [FromServices] ServiceCatalogService catalog, string data, string servico, int? barberId) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            if (!TryParseLocalDate(data, out var dataFiltro))
                return Results.BadRequest(new { error = "Data invalida. Use yyyy-MM-dd" });

            if (!int.TryParse(servico, out var servicoId))
                return Results.BadRequest(new { error = "Servico invalido" });

            var info = catalog.Get(servicoId);
            if (info == null) return Results.BadRequest(new { error = "Servico inativo ou invalido" });
            var duracao = info.DurationMinutes;
            var unavailable = agenda.GetUnavailableDay(dataFiltro, barberId);
            if (unavailable != null)
            {
                return Results.Ok(new
                {
                    Data = dataFiltro.ToString("dd/MM/yyyy"),
                    Servico = servico,
                    DuracaoMinutos = duracao,
                    HorariosLivres = Array.Empty<string>(),
                    Indisponivel = true,
                    Motivo = unavailable.Type,
                    Observacao = unavailable.Reason
                });
            }

            var horarios = agenda.GetHorariosDisponiveis(dataFiltro, servicoId, barberId);
            var window = agenda.GetScheduleWindow(dataFiltro, barberId);
            var ultimoInicio = window.WorkEnd - TimeSpan.FromMinutes(duracao);
            return Results.Ok(new
            {
                Data = dataFiltro.ToString("dd/MM/yyyy"),
                Servico = servico,
                DuracaoMinutos = duracao,
                HorariosLivres = horarios.Select(h => h.ToString(@"hh\:mm")).ToList(),
                ExpedienteInicio = window.WorkStart.ToString(@"hh\:mm"),
                ExpedienteFim = window.WorkEnd.ToString(@"hh\:mm"),
                UltimoInicioPossivel = ultimoInicio >= window.WorkStart ? ultimoInicio.ToString(@"hh\:mm") : null,
                AlmocoInicio = window.LunchStart?.ToString(@"hh\:mm"),
                AlmocoFim = window.LunchEnd?.ToString(@"hh\:mm"),
                Profissional = window.BarberName,
                Indisponivel = false
            });
        });

        app.MapPost("/api/agendamentos", async (HttpContext ctx, AppDbContext db,
            [FromServices] SchedulerService scheduler, [FromServices] AgendaService agenda, [FromServices] ServiceCatalogService catalog, [FromServices] ITenantService tenantService, CriarAgendamentoRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            if (!TryParseLocalDateTime(req.DateTime, out var dataHora))
                return Results.BadRequest(new { error = "Data/hora invalida" });

            if (dataHora <= AgendaService.GetBrazilNow().AddMinutes(1))
                return Results.BadRequest(new { error = "Agendamento precisa ser em data e horario futuros" });

            if (!int.TryParse(req.Servico, out var servicoIdReq))
                return Results.BadRequest(new { error = "Servico invalido" });

            // Cliente OPCIONAL: agendamento presencial pela dashboard não exige nome/telefone.
            // Sem nome -> "Cliente presencial". Sem telefone -> identificador interno (sem WhatsApp).
            var contactName = string.IsNullOrWhiteSpace(req.ContactName) ? "Cliente presencial" : req.ContactName.Trim();
            string clientPhone;
            if (string.IsNullOrWhiteSpace(req.PhoneNumber))
            {
                clientPhone = $"manual-{DateTime.Now:yyyyMMddHHmmssfff}"; // marcador interno, único, sem reminders
            }
            else
            {
                var normalized = NormalizePhone(req.PhoneNumber);
                if (normalized == null)
                    return Results.BadRequest(new { error = "Telefone invalido. Informe DDD e numero do WhatsApp ou deixe em branco." });
                clientPhone = normalized;
            }

            var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ctx.RequestAborted);
            if (guard.Error != null) return guard.Error;
            int storeId = guard.StoreId;
            var store = guard.Store!;
            var usesProfessionalSchedule = store.BusinessType == BusinessType.Barbershop;

            var sInfo = catalog.Get(servicoIdReq);
            if (sInfo == null) return Results.BadRequest(new { error = "Servico inativo ou invalido" });
            var duracao = sInfo.DurationMinutes;

            var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;

            // Lava-jato não tem profissional/box: barberId nulo, disponibilidade por capacidade da loja.
            // Barbearia: usa o profissional informado, OU auto-seleciona o primeiro ativo disponível no horário.
            int? barberId = null;
            string? barberName = null;

            if (usesProfessionalSchedule)
            {
                if (userRole == "barbeiro")
                {
                    if (!int.TryParse(userBarberIdClaim, out var claimBarber)) return Results.Forbid();
                    barberId = claimBarber;
                }
                else if (req.BarberId.HasValue && req.BarberId.Value > 0)
                {
                    barberId = req.BarberId.Value;
                }
                else
                {
                    // Auto: escolhe o primeiro profissional ativo da loja com o horário livre.
                    var ativos = await db.Set<Barbeiro>().AsNoTracking()
                        .Where(b => b.StoreId == storeId && b.Ativo).OrderBy(b => b.Id).ToListAsync();
                    if (ativos.Count == 0)
                        return Results.BadRequest(new { error = "Nenhum profissional ativo nesta loja." });
                    barberId = ativos.FirstOrDefault(b => scheduler.IsSlotAvailable(dataHora, servicoIdReq, b.Id))?.Id;
                    if (barberId == null)
                        return Results.BadRequest(new { error = "Nenhum profissional disponivel neste horario. Escolha outro horario." });
                }

                var barber = await db.Set<Barbeiro>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == barberId && b.StoreId == storeId && b.Ativo);
                if (barber == null) return Results.BadRequest(new { error = "Profissional invalido ou inativo" });
                barberName = string.IsNullOrWhiteSpace(req.BarberName) ? barber.Nome : req.BarberName;
            }

            var unavailable = agenda.GetUnavailableDay(dataHora, barberId);
            if (unavailable != null)
                return Results.BadRequest(new { error = $"Dia indisponivel: {unavailable.Type}" });

            if (!scheduler.IsSlotAvailable(dataHora, servicoIdReq, barberId))
                return Results.BadRequest(new { error = "Horario indisponivel ou cheio" });

            var appt = await scheduler.SaveAsync(storeId,
                clientPhone, contactName, dataHora,
                servicoIdReq, duracao, sInfo.Price, barberId, barberName, req.Notes, "Dashboard");

            if (appt == null)
                return Results.BadRequest(new { error = "Horario ocupado por outro agendamento" });

            await LogAction(db, "Dashboard", "CRIAR", $"Cliente: {contactName}, {dataHora:dd/MM/yyyy HH:mm}, {sInfo.Name}");
            return Results.Ok(new
            {
                id = appt.Id,
                message = "Agendamento criado com sucesso",
                serviceName = sInfo.Name,
                price = sInfo.Price,
                barberName,
                dateTime = appt.DateTime.ToString("yyyy-MM-ddTHH:mm:ss")
            });
        });

        app.MapGet("/api/dias-indisponiveis", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int? barberId, string? inicio, string? fim) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var today = AgendaService.GetBrazilNow().Date;
            var start = TryParseLocalDate(inicio, out var parsedStart) ? parsedStart.Date : today.AddDays(-7);
            var end = TryParseLocalDate(fim, out var parsedEnd) ? parsedEnd.Date : today.AddDays(45);
            var tenantId = tenantService.GetTenantId();

            var query = db.UnavailableDays.AsNoTracking()
                .Where(d => d.StoreId == tenantId && d.Date.Date >= start && d.Date.Date <= end);

            if (barberId.HasValue)
                query = query.Where(d => d.BarberId == null || d.BarberId == barberId.Value);

            var result = await query.OrderBy(d => d.Date).ThenBy(d => d.BarberId)
                .Select(d => new
                {
                    d.Id,
                    Data = d.Date.ToString("yyyy-MM-dd"),
                    d.Type,
                    d.Reason,
                    d.BarberId
                })
                .ToListAsync();

            return Results.Ok(result);
        });

        app.MapPost("/api/dias-indisponiveis", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, UnavailableDayRequest req) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            var claimBarberId = ctx.User.FindFirst("BarberId")?.Value;

            if (!TryParseLocalDate(req.Data, out var date))
                return Results.BadRequest(new { error = "Data invalida" });

            var barberId = req.BarberId;
            if (role == "barbeiro")
            {
                if (!int.TryParse(claimBarberId, out var currentBarberId)) return Results.Forbid();
                barberId = currentBarberId;
            }

            var type = NormalizeUnavailableType(req.Type);
            if (type == null) return Results.BadRequest(new { error = "Tipo invalido" });

            var tenantId = tenantService.GetTenantId();
            var targetDate = date.Date;
            var hasAppointments = await db.Appointments.AsNoTracking()
                .AnyAsync(a => a.StoreId == tenantId && a.DateTime.Date == targetDate && a.Status == "ativo" && (!barberId.HasValue || a.BarberId == barberId.Value));
            if (hasAppointments)
                return Results.Conflict(new { error = "Este dia ja possui agendamentos. Cancele/reagende antes de bloquear." });

            var existing = await db.UnavailableDays
                .FirstOrDefaultAsync(d => d.StoreId == tenantId && d.Date.Date == targetDate && d.BarberId == barberId);

            if (existing == null)
            {
                existing = new UnavailableDay { StoreId = tenantId, Date = targetDate, BarberId = barberId };
                db.UnavailableDays.Add(existing);
            }

            existing.Type = type;
            existing.Reason = req.Reason?.Trim() ?? "";
            await db.SaveChangesAsync();

            await LogAction(db, "Dashboard", "DIA_INDISPONIVEL", $"{targetDate:dd/MM/yyyy} - {type}");
            return Results.Ok(new { existing.Id, Data = targetDate.ToString("yyyy-MM-dd"), existing.Type, existing.Reason, existing.BarberId });
        });

        app.MapDelete("/api/dias-indisponiveis/{id}", async (HttpContext ctx, AppDbContext db, ITenantService tenantService, int id) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var day = await db.UnavailableDays.FirstOrDefaultAsync(d => d.Id == id && d.StoreId == tenantService.GetTenantId());
            if (day == null) return Results.NotFound();

            var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
            if (role == "barbeiro")
            {
                var claimBarberId = ctx.User.FindFirst("BarberId")?.Value;
                if (!int.TryParse(claimBarberId, out var barberId) || day.BarberId != barberId)
                    return Results.Forbid();
            }

            db.UnavailableDays.Remove(day);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = id });
        });

        app.MapDelete("/api/agendamentos/{id}", async (HttpContext ctx, int id, AppDbContext db, ITenantService tenantService, SchedulerService scheduler, ServiceCatalogService catalog) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id && a.StoreId == tenantService.GetTenantId() && a.Status == "ativo");
            if (appt is null) return Results.NotFound();
            if (!CanAccessAppointment(ctx, appt)) return Results.Forbid();

            await scheduler.CancelAsync(id, "dashboard");
            await LogAction(db, "Dashboard", "CANCELAR", $"ID: {id}, Cliente: {appt.ContactName}");
            return Results.Ok(new { deleted = id });
        });

        app.MapPatch("/api/agendamentos/{id}/confirmar", async (HttpContext ctx, int id, AppDbContext db, ITenantService tenantService, NotificationService notifications, ServiceCatalogService catalog) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var tenantId = tenantService.GetTenantId();
            var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id && a.Status == "ativo" && (tenantId == 0 || a.StoreId == tenantId));
            if (appt is null) return Results.NotFound();
            if (!CanAccessAppointment(ctx, appt)) return Results.Forbid();

            appt.PresencaConfirmada = true;
            await db.SaveChangesAsync();
            await LogAction(db, "Dashboard", "CONFIRMAR_PRESENCA", $"ID: {id}, Cliente: {appt.ContactName}");
            await notifications.NotifyAppointmentConfirmed(appt.ContactName, appt.DateTime, catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}", appt.Id);
            return Results.Ok(new
            {
                confirmed = id,
                appointment = new
                {
                    appt.Id,
                    appt.ContactName,
                    appt.DateTime,
                    Servico = appt.ServiceId,
                    appt.BarberId,
                    appt.BarberName,
                    appt.Preco,
                    appt.DuracaoMinutos,
                    appt.PresencaConfirmada
                }
            });
        });

        app.MapPatch("/api/agendamentos/{id}", async (HttpContext ctx, int id, AppDbContext db,
            [FromServices] SchedulerService scheduler, [FromServices] AgendaService agenda, ITenantService tenantService, EditarAgendamentoRequest req, ILogger<Program> logger, NotificationService notifications, ServiceCatalogService catalog) =>
        {
            if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return Results.Unauthorized();

            var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id && a.StoreId == tenantService.GetTenantId() && a.Status == "ativo");
            if (appt is null) return Results.NotFound();
            if (!CanAccessAppointment(ctx, appt)) return Results.Forbid();

            if (!TryParseLocalDateTime(req.NovaData, out var rawDate))
                return Results.BadRequest(new { error = "Data invalida" });

            var novaData = new DateTime(rawDate.Year, rawDate.Month, rawDate.Day, rawDate.Hour, rawDate.Minute, 0);
            if (novaData <= AgendaService.GetBrazilNow().AddMinutes(1))
                return Results.BadRequest(new { error = "Reagendamento precisa ser em data e horario futuros" });

            var unavailable = agenda.GetUnavailableDay(novaData, appt.BarberId);
            if (unavailable != null)
                return Results.BadRequest(new { error = $"Dia indisponivel: {unavailable.Type}" });

            var dataAntiga = appt.DateTime;

            // Reagendamento ATÔMICO sob o semáforo global de SchedulerService.
            // A checagem de conflito + gravação acontecem na mesma seção crítica de SaveAsync,
            // impedindo double-booking quando um reagendamento concorre com um agendamento do bot.
            var (ok, error) = await scheduler.RescheduleAsync(id, novaData, ctx.RequestAborted);
            if (!ok)
                return Results.BadRequest(new { error = error ?? "Horario indisponivel" });
            await LogAction(db, "Dashboard", "EDITAR", $"ID: {id}, Cliente: {appt.ContactName}, De: {dataAntiga:HH:mm}, Para: {novaData:HH:mm}");
            await notifications.NotifyAppointmentRescheduled(appt.ContactName, dataAntiga, novaData, catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}", appt.Id);
            logger.LogInformation("Agendamento {Id} editado: {De} -> {Para}", id, dataAntiga, novaData);

            return Results.Ok(new { updated = id, novaData = novaData.ToString("dd/MM/yyyy HH:mm") });
        });

        return app;
    }

    private static bool CanAccessAppointment(HttpContext ctx, Appointment appt)
    {
        var userRole = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
        if (userRole != "barbeiro") return true;

        var userBarberIdClaim = ctx.User.FindFirst("BarberId")?.Value;
        return int.TryParse(userBarberIdClaim, out var barberId) && appt.BarberId == barberId;
    }

    private static string MaskPhone(string phone) =>
        phone.Length > 4 ? phone[..4] + "****" : "****";

    private static string? NormalizePhone(string phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length is >= 10 and <= 13 ? digits : null;
    }

    private static string? NormalizeUnavailableType(string? type)
    {
        var value = (type ?? "fechado").Trim().ToLowerInvariant();
        return value is "fechado" or "feriado" or "folga" or "manutencao" or "manutenção" ? value : null;
    }

    private static bool TryParseLocalDate(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            date = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
            return true;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date)) return false;
        date = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return true;
    }

    private static bool TryParseLocalDateTime(string? value, out DateTime dateTime)
    {
        dateTime = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var formats = new[] { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
        {
            dateTime = DateTime.SpecifyKind(new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, dateTime.Hour, dateTime.Minute, 0), DateTimeKind.Unspecified);
            return true;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime)) return false;
        dateTime = DateTime.SpecifyKind(new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, dateTime.Hour, dateTime.Minute, 0), DateTimeKind.Unspecified);
        return true;
    }

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
}

public record EditarAgendamentoRequest(string NovaData);

public record CriarAgendamentoRequest(string ContactName, string PhoneNumber, string DateTime, string Servico, int? BarberId, string? BarberName, string? Notes);

public record UnavailableDayRequest(string Data, string Type, string? Reason, int? BarberId);
