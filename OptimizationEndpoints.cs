using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;
using WhatsAppBot.Worker.Services.Modules;

namespace WhatsAppBot.Worker.Endpoints;

public static class OptimizationEndpoints
{
    public const string ModuleKey = "computer_optimization";

    public static IEndpointRouteBuilder MapOptimizationEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/api/optimization/tickets", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            string? status,
            string? busca,
            int page = 1,
            int pageSize = 80) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            var query = db.OptimizationTickets.AsNoTracking()
                .Where(t => t.StoreId == guard.StoreId);

            if (!string.IsNullOrWhiteSpace(status) && TryParseStatus(status, out var parsedStatus))
                query = query.Where(t => t.Status == parsedStatus);

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var q = busca.Trim().ToLowerInvariant();
                query = query.Where(t =>
                    t.TicketNumber.ToLower().Contains(q) ||
                    t.CustomerName.ToLower().Contains(q) ||
                    t.PhoneNumber.Contains(q) ||
                    t.ReportedProblem.ToLower().Contains(q));
            }

            var total = await query.CountAsync(ctx.RequestAborted);
            var tickets = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ctx.RequestAborted);

            var deviceIds = tickets.Where(t => t.OptimizationDeviceId.HasValue).Select(t => t.OptimizationDeviceId!.Value).Distinct().ToList();
            var serviceIds = tickets.Where(t => t.ServiceId.HasValue).Select(t => t.ServiceId!.Value).Distinct().ToList();

            var devices = await db.OptimizationDevices.AsNoTracking()
                .Where(d => d.StoreId == guard.StoreId && deviceIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, ctx.RequestAborted);

            var services = await db.Servicos.AsNoTracking()
                .Where(s => s.StoreId == guard.StoreId && serviceIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ctx.RequestAborted);

            return Results.Ok(new
            {
                data = tickets.Select(t => ToTicketDto(t, devices.GetValueOrDefault(t.OptimizationDeviceId ?? 0), services.GetValueOrDefault(t.ServiceId ?? 0))),
                total,
                page,
                pageSize
            });
        });

        app.MapGet("/api/optimization/tickets/{id:int}", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var ticket = await db.OptimizationTickets.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == id && t.StoreId == guard.StoreId, ctx.RequestAborted);
            if (ticket == null) return Results.NotFound(new { error = "Atendimento nao encontrado." });

            var device = ticket.OptimizationDeviceId.HasValue
                ? await db.OptimizationDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == ticket.OptimizationDeviceId && d.StoreId == guard.StoreId, ctx.RequestAborted)
                : null;
            var service = ticket.ServiceId.HasValue
                ? await db.Servicos.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ticket.ServiceId && s.StoreId == guard.StoreId, ctx.RequestAborted)
                : null;
            var events = await db.OptimizationTicketEvents.AsNoTracking()
                .Where(e => e.StoreId == guard.StoreId && e.OptimizationTicketId == ticket.Id)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => new
                {
                    e.Id,
                    e.Type,
                    e.FromStatus,
                    e.ToStatus,
                    e.Message,
                    e.CreatedBy,
                    e.CreatedAt,
                    e.VisibleToCustomer
                })
                .ToListAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                ticket = ToTicketDto(ticket, device, service),
                events
            });
        });

        app.MapPost("/api/optimization/tickets", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationTicketRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var validation = ValidateTicketText(req);
            if (validation != null) return validation;

            var phone = NormalizePhoneOrManual(req.PhoneNumber);
            var customerName = Clean(req.CustomerName, 90);
            if (string.IsNullOrWhiteSpace(customerName))
                return Results.BadRequest(new { error = "Nome do cliente e obrigatorio." });

            var service = await ValidateServiceAsync(db, guard.StoreId, req.ServiceId, ctx.RequestAborted);
            if (req.ServiceId.HasValue && service == null)
                return Results.BadRequest(new { error = "Servico/pacote invalido para esta loja." });

            if (req.AppointmentId.HasValue)
            {
                var appointmentExists = await db.Appointments.AsNoTracking()
                    .AnyAsync(a => a.Id == req.AppointmentId && a.StoreId == guard.StoreId, ctx.RequestAborted);
                if (!appointmentExists) return Results.BadRequest(new { error = "Agendamento invalido para esta loja." });
            }

            int? deviceId = req.OptimizationDeviceId;
            if (deviceId.HasValue)
            {
                var deviceExists = await db.OptimizationDevices.AsNoTracking()
                    .AnyAsync(d => d.Id == deviceId && d.StoreId == guard.StoreId, ctx.RequestAborted);
                if (!deviceExists) return Results.BadRequest(new { error = "Computador invalido para esta loja." });
            }
            else if (req.Device != null)
            {
                var device = BuildDevice(guard.StoreId, phone, customerName, req.Device);
                db.OptimizationDevices.Add(device);
                await db.SaveChangesAsync(ctx.RequestAborted);
                deviceId = device.Id;
            }

            var ticket = new OptimizationTicket
            {
                StoreId = guard.StoreId,
                TicketNumber = await GenerateTicketNumberAsync(db, guard.StoreId, ctx.RequestAborted),
                PhoneNumber = phone,
                CustomerName = customerName,
                OptimizationDeviceId = deviceId,
                AppointmentId = req.AppointmentId,
                ServiceId = req.ServiceId,
                ServiceMode = Clean(req.ServiceMode, 40, "Presencial"),
                Goal = Clean(req.Goal, 120, service?.Nome ?? "Melhorar desempenho geral"),
                ReportedProblem = Clean(req.ReportedProblem, 500, ""),
                Urgency = Clean(req.Urgency, 40, "Essa semana"),
                Status = req.AppointmentId.HasValue ? OptimizationTicketStatus.Agendado : OptimizationTicketStatus.Novo,
                BeforeNotes = CleanNullable(req.BeforeNotes, 1200),
                OptimizationChecklistJson = DefaultChecklistJson(),
                EstimatedAmount = req.EstimatedAmount ?? service?.Preco,
                FinalAmount = req.FinalAmount,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            if (ticket.Status == OptimizationTicketStatus.Agendado)
                ticket.StartedAt = null;

            db.OptimizationTickets.Add(ticket);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await AddEventAsync(db, guard.StoreId, ticket.Id, "created", null, ticket.Status.ToString(), "Atendimento de otimizacao criado.", CurrentUser(ctx), false, ctx.RequestAborted);

            return Results.Created($"/api/optimization/tickets/{ticket.Id}", ToTicketDto(ticket, null, service));
        });

        app.MapPatch("/api/optimization/tickets/{id:int}", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationTicketUpdateRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var ticket = await db.OptimizationTickets.FirstOrDefaultAsync(t => t.Id == id && t.StoreId == guard.StoreId, ctx.RequestAborted);
            if (ticket == null) return Results.NotFound(new { error = "Atendimento nao encontrado." });

            if (ContainsCredentialLikeText(req.BeforeNotes, req.AfterNotes, req.ResultSummary, req.ReportedProblem))
                return Results.BadRequest(new { error = "Nao registre senhas ou credenciais do computador. Use apenas observacoes operacionais." });

            if (req.ServiceId.HasValue)
            {
                var service = await ValidateServiceAsync(db, guard.StoreId, req.ServiceId, ctx.RequestAborted);
                if (service == null) return Results.BadRequest(new { error = "Servico/pacote invalido para esta loja." });
                ticket.ServiceId = req.ServiceId;
                ticket.EstimatedAmount ??= service.Preco;
            }

            if (req.AppointmentId.HasValue)
            {
                var appointmentExists = await db.Appointments.AsNoTracking()
                    .AnyAsync(a => a.Id == req.AppointmentId && a.StoreId == guard.StoreId, ctx.RequestAborted);
                if (!appointmentExists) return Results.BadRequest(new { error = "Agendamento invalido para esta loja." });
                ticket.AppointmentId = req.AppointmentId;
            }

            if (req.OptimizationDeviceId.HasValue)
            {
                var deviceExists = await db.OptimizationDevices.AsNoTracking()
                    .AnyAsync(d => d.Id == req.OptimizationDeviceId && d.StoreId == guard.StoreId, ctx.RequestAborted);
                if (!deviceExists) return Results.BadRequest(new { error = "Computador invalido para esta loja." });
                ticket.OptimizationDeviceId = req.OptimizationDeviceId;
            }

            if (!string.IsNullOrWhiteSpace(req.CustomerName)) ticket.CustomerName = Clean(req.CustomerName, 90);
            if (!string.IsNullOrWhiteSpace(req.PhoneNumber)) ticket.PhoneNumber = NormalizePhoneOrManual(req.PhoneNumber);
            if (!string.IsNullOrWhiteSpace(req.ServiceMode)) ticket.ServiceMode = Clean(req.ServiceMode, 40, ticket.ServiceMode);
            if (!string.IsNullOrWhiteSpace(req.Goal)) ticket.Goal = Clean(req.Goal, 120, ticket.Goal);
            if (!string.IsNullOrWhiteSpace(req.ReportedProblem)) ticket.ReportedProblem = Clean(req.ReportedProblem, 500, ticket.ReportedProblem);
            if (!string.IsNullOrWhiteSpace(req.Urgency)) ticket.Urgency = Clean(req.Urgency, 40, ticket.Urgency);
            if (req.BeforeNotes != null) ticket.BeforeNotes = CleanNullable(req.BeforeNotes, 1200);
            if (req.AfterNotes != null) ticket.AfterNotes = CleanNullable(req.AfterNotes, 1200);
            if (req.ResultSummary != null) ticket.ResultSummary = CleanNullable(req.ResultSummary, 1200);
            if (req.EstimatedAmount.HasValue) ticket.EstimatedAmount = req.EstimatedAmount;
            if (req.FinalAmount.HasValue) ticket.FinalAmount = req.FinalAmount;
            ticket.UpdatedAt = DateTime.Now;

            await db.SaveChangesAsync(ctx.RequestAborted);
            await AddEventAsync(db, guard.StoreId, ticket.Id, "updated", null, null, "Atendimento atualizado.", CurrentUser(ctx), false, ctx.RequestAborted);
            return Results.Ok(ToTicketDto(ticket, null, null));
        });

        app.MapPatch("/api/optimization/tickets/{id:int}/status", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationStatusRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            if (!TryParseStatus(req.Status, out var next))
                return Results.BadRequest(new { error = "Status invalido." });

            var ticket = await db.OptimizationTickets.FirstOrDefaultAsync(t => t.Id == id && t.StoreId == guard.StoreId, ctx.RequestAborted);
            if (ticket == null) return Results.NotFound(new { error = "Atendimento nao encontrado." });

            var current = ticket.Status;
            if (!CanTransition(current, next))
                return Results.BadRequest(new { error = $"Transicao invalida: {current} -> {next}." });

            ticket.Status = next;
            ticket.UpdatedAt = DateTime.Now;
            if (next == OptimizationTicketStatus.EmOtimizacao && ticket.StartedAt == null) ticket.StartedAt = DateTime.Now;
            if (next == OptimizationTicketStatus.Concluido && ticket.CompletedAt == null) ticket.CompletedAt = DateTime.Now;
            if (next is OptimizationTicketStatus.Concluido or OptimizationTicketStatus.Cancelado) ticket.ClosedAt ??= DateTime.Now;

            await AddEventAsync(db, guard.StoreId, ticket.Id, "status", current.ToString(), next.ToString(),
                Clean(req.Message, 500, $"Status alterado para {next}."), CurrentUser(ctx), req.VisibleToCustomer ?? false, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);

            return Results.Ok(ToTicketDto(ticket, null, null));
        });

        app.MapPatch("/api/optimization/tickets/{id:int}/checklist", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationChecklistRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var ticket = await db.OptimizationTickets.FirstOrDefaultAsync(t => t.Id == id && t.StoreId == guard.StoreId, ctx.RequestAborted);
            if (ticket == null) return Results.NotFound(new { error = "Atendimento nao encontrado." });

            ticket.OptimizationChecklistJson = req.Json ?? JsonSerializer.Serialize(req.Items ?? DefaultChecklist());
            ticket.UpdatedAt = DateTime.Now;
            await AddEventAsync(db, guard.StoreId, ticket.Id, "checklist", null, null, "Checklist de software atualizado.", CurrentUser(ctx), false, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToTicketDto(ticket, null, null));
        });

        app.MapPost("/api/optimization/tickets/{id:int}/quote", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationQuoteRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            if (!IsValidQuoteAmount(req.Amount))
                return Results.BadRequest(new { error = "Valor do orcamento invalido." });

            if (ContainsCredentialLikeText(req.Message))
                return Results.BadRequest(new { error = "Nao registre senhas ou credenciais do computador." });

            var ticket = await db.OptimizationTickets.FirstOrDefaultAsync(t => t.Id == id && t.StoreId == guard.StoreId, ctx.RequestAborted);
            if (ticket == null) return Results.NotFound(new { error = "Atendimento nao encontrado." });
            if (ticket.Status is OptimizationTicketStatus.Concluido or OptimizationTicketStatus.Cancelado)
                return Results.BadRequest(new { error = "Nao e possivel alterar orcamento de atendimento encerrado." });

            var oldStatus = ticket.Status;
            ticket.EstimatedAmount = req.Amount;
            ticket.UpdatedAt = DateTime.Now;

            var approved = req.Approved == true;
            var rejected = req.Approved == false;
            var eventType = approved ? "quote_approved" : rejected ? "quote_rejected" : "quote_sent";
            var eventMessage = Clean(req.Message, 600,
                approved
                    ? $"Orcamento aprovado: R$ {req.Amount:0.00}."
                    : rejected
                        ? $"Orcamento recusado: R$ {req.Amount:0.00}."
                        : $"Orcamento enviado: R$ {req.Amount:0.00}.");

            if (approved)
            {
                ticket.FinalAmount = req.Amount;
                if (ticket.Status == OptimizationTicketStatus.AguardandoCliente && CanTransition(ticket.Status, OptimizationTicketStatus.Triagem))
                    ticket.Status = OptimizationTicketStatus.Triagem;
            }
            else if (rejected)
            {
                if (CanTransition(ticket.Status, OptimizationTicketStatus.AguardandoCliente))
                    ticket.Status = OptimizationTicketStatus.AguardandoCliente;
            }
            else if (CanTransition(ticket.Status, OptimizationTicketStatus.AguardandoCliente))
            {
                ticket.Status = OptimizationTicketStatus.AguardandoCliente;
            }

            await AddEventAsync(db, guard.StoreId, ticket.Id, eventType,
                oldStatus == ticket.Status ? null : oldStatus.ToString(),
                oldStatus == ticket.Status ? null : ticket.Status.ToString(),
                eventMessage, CurrentUser(ctx), req.VisibleToCustomer ?? true, ctx.RequestAborted);
            await db.SaveChangesAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                ticket = ToTicketDto(ticket, null, null),
                quote = new
                {
                    amount = req.Amount,
                    approved = req.Approved,
                    visibleToCustomer = req.VisibleToCustomer ?? true,
                    eventType
                }
            });
        });

        app.MapPost("/api/optimization/tickets/{id:int}/events", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationEventRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var exists = await db.OptimizationTickets.AsNoTracking().AnyAsync(t => t.Id == id && t.StoreId == guard.StoreId, ctx.RequestAborted);
            if (!exists) return Results.NotFound(new { error = "Atendimento nao encontrado." });

            if (ContainsCredentialLikeText(req.Message))
                return Results.BadRequest(new { error = "Nao registre senhas ou credenciais do computador." });

            var ev = new OptimizationTicketEvent
            {
                StoreId = guard.StoreId,
                OptimizationTicketId = id,
                Type = Clean(req.Type, 40, "note"),
                Message = Clean(req.Message, 1000, ""),
                CreatedBy = CurrentUser(ctx),
                CreatedAt = DateTime.Now,
                VisibleToCustomer = req.VisibleToCustomer ?? false
            };
            db.OptimizationTicketEvents.Add(ev);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/optimization/tickets/{id}/events/{ev.Id}", ev);
        });

        app.MapGet("/api/optimization/devices", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            string? busca) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var query = db.OptimizationDevices.AsNoTracking()
                .Where(d => d.StoreId == guard.StoreId && d.IsActive);

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var q = busca.Trim().ToLowerInvariant();
                query = query.Where(d =>
                    d.CustomerName.ToLower().Contains(q) ||
                    d.PhoneNumber.Contains(q) ||
                    d.DeviceType.ToLower().Contains(q) ||
                    (d.Processor != null && d.Processor.ToLower().Contains(q)) ||
                    (d.Gpu != null && d.Gpu.ToLower().Contains(q)));
            }

            var devices = await query.OrderByDescending(d => d.UpdatedAt).Take(200).ToListAsync(ctx.RequestAborted);
            return Results.Ok(devices);
        });

        app.MapPost("/api/optimization/devices", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationDeviceRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            if (ContainsCredentialLikeText(req.Notes))
                return Results.BadRequest(new { error = "Nao registre senhas ou credenciais do computador." });

            var phone = NormalizePhoneOrManual(req.PhoneNumber);
            var customer = Clean(req.CustomerName, 90);
            if (string.IsNullOrWhiteSpace(customer))
                return Results.BadRequest(new { error = "Nome do cliente e obrigatorio." });

            var device = BuildDevice(guard.StoreId, phone, customer, req);
            db.OptimizationDevices.Add(device);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Created($"/api/optimization/devices/{device.Id}", device);
        });

        app.MapPatch("/api/optimization/devices/{id:int}", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            OptimizationDeviceRequest req) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            if (ContainsCredentialLikeText(req.Notes))
                return Results.BadRequest(new { error = "Nao registre senhas ou credenciais do computador." });

            var device = await db.OptimizationDevices.FirstOrDefaultAsync(d => d.Id == id && d.StoreId == guard.StoreId, ctx.RequestAborted);
            if (device == null) return Results.NotFound(new { error = "Computador nao encontrado." });

            ApplyDeviceUpdate(device, req);
            device.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(device);
        });

        app.MapGet("/api/optimization/reports/summary", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            FeatureAccessService features,
            DateTime? inicio,
            DateTime? fim) =>
        {
            var guard = await RequireOptimizationStoreAsync(ctx, db, tenantService, features, apiKey);
            if (guard.Error != null) return guard.Error;

            var start = (inicio ?? DateTime.Now.Date.AddDays(-30)).Date;
            var end = (fim ?? DateTime.Now.Date).Date.AddDays(1).AddTicks(-1);

            var tickets = await db.OptimizationTickets.AsNoTracking()
                .Where(t => t.StoreId == guard.StoreId && t.CreatedAt >= start && t.CreatedAt <= end)
                .ToListAsync(ctx.RequestAborted);

            var serviceIds = tickets.Where(t => t.ServiceId.HasValue).Select(t => t.ServiceId!.Value).Distinct().ToList();
            var services = await db.Servicos.AsNoTracking()
                .Where(s => s.StoreId == guard.StoreId && serviceIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ctx.RequestAborted);

            var completed = tickets.Where(t => t.Status == OptimizationTicketStatus.Concluido).ToList();
            var cancelled = tickets.Where(t => t.Status == OptimizationTicketStatus.Cancelado).ToList();
            var averageHours = completed
                .Where(t => t.CompletedAt.HasValue)
                .Select(t => (t.CompletedAt!.Value - t.CreatedAt).TotalHours)
                .DefaultIfEmpty(0)
                .Average();

            var topPackage = tickets
                .Where(t => t.ServiceId.HasValue)
                .GroupBy(t => t.ServiceId!.Value)
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    serviceId = g.Key,
                    name = services.GetValueOrDefault(g.Key)?.Nome ?? $"Servico #{g.Key}",
                    count = g.Count()
                })
                .FirstOrDefault();

            return Results.Ok(new
            {
                period = new { inicio = start, fim = end },
                created = tickets.Count,
                completed = completed.Count,
                cancelled = cancelled.Count,
                revenue = tickets.Sum(t => t.FinalAmount ?? t.EstimatedAmount ?? 0),
                averageCompletionHours = Math.Round(averageHours, 1),
                byStatus = tickets.GroupBy(t => t.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                byMode = tickets.GroupBy(t => string.IsNullOrWhiteSpace(t.ServiceMode) ? "Nao informado" : t.ServiceMode).ToDictionary(g => g.Key, g => g.Count()),
                topPackage
            });
        });

        app.MapMethods("/api/tech/{**path}", new[] { "GET", "POST", "PATCH", "PUT", "DELETE" }, (HttpContext ctx, string? path) =>
        {
            var suffix = string.IsNullOrWhiteSpace(path) ? "" : "/" + path.TrimStart('/');
            return Results.Redirect($"/api/optimization{suffix}{ctx.Request.QueryString}", permanent: false, preserveMethod: true);
        });

        return app;
    }

    public static bool CanTransition(OptimizationTicketStatus from, OptimizationTicketStatus to)
    {
        if (from == to) return true;
        if (from is OptimizationTicketStatus.Concluido or OptimizationTicketStatus.Cancelado) return false;

        return from switch
        {
            OptimizationTicketStatus.Novo => to is OptimizationTicketStatus.Triagem or OptimizationTicketStatus.Agendado or OptimizationTicketStatus.Cancelado,
            OptimizationTicketStatus.Triagem => to is OptimizationTicketStatus.Agendado or OptimizationTicketStatus.AguardandoCliente or OptimizationTicketStatus.EmOtimizacao or OptimizationTicketStatus.Cancelado,
            OptimizationTicketStatus.Agendado => to is OptimizationTicketStatus.AguardandoCliente or OptimizationTicketStatus.EmOtimizacao or OptimizationTicketStatus.Cancelado,
            OptimizationTicketStatus.AguardandoCliente => to is OptimizationTicketStatus.Triagem or OptimizationTicketStatus.Agendado or OptimizationTicketStatus.EmOtimizacao or OptimizationTicketStatus.Cancelado,
            OptimizationTicketStatus.EmOtimizacao => to is OptimizationTicketStatus.EmRevisao or OptimizationTicketStatus.AguardandoCliente or OptimizationTicketStatus.Pronto or OptimizationTicketStatus.Cancelado,
            OptimizationTicketStatus.EmRevisao => to is OptimizationTicketStatus.EmOtimizacao or OptimizationTicketStatus.AguardandoCliente or OptimizationTicketStatus.Pronto or OptimizationTicketStatus.Cancelado,
            OptimizationTicketStatus.Pronto => to is OptimizationTicketStatus.Concluido or OptimizationTicketStatus.EmRevisao or OptimizationTicketStatus.Cancelado,
            _ => false
        };
    }

    public static bool TryParseStatus(string? value, out OptimizationTicketStatus status)
    {
        status = OptimizationTicketStatus.Novo;
        var key = NormalizeKey(value);
        var map = new Dictionary<string, OptimizationTicketStatus>
        {
            ["novo"] = OptimizationTicketStatus.Novo,
            ["triagem"] = OptimizationTicketStatus.Triagem,
            ["agendado"] = OptimizationTicketStatus.Agendado,
            ["aguardandocliente"] = OptimizationTicketStatus.AguardandoCliente,
            ["emotimizacao"] = OptimizationTicketStatus.EmOtimizacao,
            ["emrevisao"] = OptimizationTicketStatus.EmRevisao,
            ["pronto"] = OptimizationTicketStatus.Pronto,
            ["concluido"] = OptimizationTicketStatus.Concluido,
            ["cancelado"] = OptimizationTicketStatus.Cancelado
        };
        return map.TryGetValue(key, out status);
    }

    internal static async Task<int> CreateTicketFromBotAsync(
        AppDbContext db,
        int storeId,
        Appointment appointment,
        ServiceCatalogItem service,
        string? detail,
        CancellationToken ct = default)
    {
        db.TenantId = storeId;

        var existing = await db.OptimizationTickets
            .FirstOrDefaultAsync(t => t.StoreId == storeId && t.AppointmentId == appointment.Id, ct);
        if (existing != null) return existing.Id;

        OptimizationDevice? device = null;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            device = new OptimizationDevice
            {
                StoreId = storeId,
                CustomerName = appointment.ContactName,
                PhoneNumber = appointment.PhoneNumber,
                DeviceType = GuessDeviceType(detail),
                OperatingSystem = GuessOperatingSystem(detail),
                MainUse = GuessMainUse(detail),
                Notes = detail,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsActive = true
            };
            db.OptimizationDevices.Add(device);
            await db.SaveChangesAsync(ct);
        }

        var ticket = new OptimizationTicket
        {
            StoreId = storeId,
            TicketNumber = await GenerateTicketNumberAsync(db, storeId, ct),
            PhoneNumber = appointment.PhoneNumber,
            CustomerName = appointment.ContactName,
            OptimizationDeviceId = device?.Id,
            AppointmentId = appointment.Id,
            ServiceId = appointment.ServiceId,
            ServiceMode = GuessServiceMode(detail),
            Goal = service.Name,
            ReportedProblem = detail ?? appointment.Notes ?? "",
            Urgency = "A confirmar",
            Status = OptimizationTicketStatus.Agendado,
            BeforeNotes = appointment.Notes,
            OptimizationChecklistJson = DefaultChecklistJson(),
            EstimatedAmount = appointment.Preco,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        db.OptimizationTickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        await AddEventAsync(db, storeId, ticket.Id, "bot_triage", null, ticket.Status.ToString(),
            "Ticket criado automaticamente pelo bot de otimizacao.", "WhatsApp/Bot", false, ct);
        return ticket.Id;
    }

    private static async Task<(IResult? Error, int StoreId)> RequireOptimizationStoreAsync(
        HttpContext ctx,
        AppDbContext db,
        ITenantService tenantService,
        FeatureAccessService features,
        string apiKey)
    {
        if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return (Results.Unauthorized(), 0);

        var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ctx.RequestAborted);
        if (guard.Error != null) return (guard.Error, guard.StoreId);

        db.TenantId = guard.StoreId;
        if (guard.Store?.BusinessType != BusinessType.ComputerOptimization)
            return (Results.Json(new { error = "Modulo disponivel apenas para lojas de otimizacao de computadores." }, statusCode: 403), guard.StoreId);

        if (!await features.IsEnabledAsync(ModuleKey, ctx.RequestAborted))
            return (Results.Json(new { error = "Modulo de otimizacao de computadores desativado pela feature flag." }, statusCode: 403), guard.StoreId);

        return (null, guard.StoreId);
    }

    private static async Task<ServicoItem?> ValidateServiceAsync(AppDbContext db, int storeId, int? serviceId, CancellationToken ct)
    {
        if (!serviceId.HasValue) return null;
        return await db.Servicos.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceId.Value && s.StoreId == storeId && s.Ativo, ct);
    }

    private static IResult? ValidateTicketText(OptimizationTicketRequest req)
    {
        if (ContainsCredentialLikeText(req.BeforeNotes, req.ReportedProblem, req.Device?.Notes))
            return Results.BadRequest(new { error = "Nao registre senhas ou credenciais do computador. Use apenas observacoes operacionais." });
        return null;
    }

    private static OptimizationDevice BuildDevice(int storeId, string phone, string customerName, OptimizationDeviceRequest req)
    {
        var now = DateTime.Now;
        return new OptimizationDevice
        {
            StoreId = storeId,
            CustomerName = customerName,
            PhoneNumber = phone,
            DeviceType = Clean(req.DeviceType, 40, "Desktop"),
            OperatingSystem = Clean(req.OperatingSystem, 40, "Windows 11"),
            Processor = CleanNullable(req.Processor, 120),
            Gpu = CleanNullable(req.Gpu, 120),
            RamGb = req.RamGb is > 0 and <= 1024 ? req.RamGb : null,
            StorageType = Clean(req.StorageType, 40, "Nao informado"),
            MainUse = Clean(req.MainUse, 60, "Uso geral"),
            Notes = CleanNullable(req.Notes, 1200),
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = req.IsActive ?? true
        };
    }

    private static void ApplyDeviceUpdate(OptimizationDevice device, OptimizationDeviceRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.CustomerName)) device.CustomerName = Clean(req.CustomerName, 90);
        if (!string.IsNullOrWhiteSpace(req.PhoneNumber)) device.PhoneNumber = NormalizePhoneOrManual(req.PhoneNumber);
        if (!string.IsNullOrWhiteSpace(req.DeviceType)) device.DeviceType = Clean(req.DeviceType, 40, device.DeviceType);
        if (!string.IsNullOrWhiteSpace(req.OperatingSystem)) device.OperatingSystem = Clean(req.OperatingSystem, 40, device.OperatingSystem);
        if (req.Processor != null) device.Processor = CleanNullable(req.Processor, 120);
        if (req.Gpu != null) device.Gpu = CleanNullable(req.Gpu, 120);
        if (req.RamGb.HasValue) device.RamGb = req.RamGb is > 0 and <= 1024 ? req.RamGb : null;
        if (!string.IsNullOrWhiteSpace(req.StorageType)) device.StorageType = Clean(req.StorageType, 40, device.StorageType);
        if (!string.IsNullOrWhiteSpace(req.MainUse)) device.MainUse = Clean(req.MainUse, 60, device.MainUse);
        if (req.Notes != null) device.Notes = CleanNullable(req.Notes, 1200);
        if (req.IsActive.HasValue) device.IsActive = req.IsActive.Value;
    }

    private static async Task<string> GenerateTicketNumberAsync(AppDbContext db, int storeId, CancellationToken ct)
    {
        var today = DateTime.Now.Date;
        var tomorrow = today.AddDays(1);
        var count = await db.OptimizationTickets.IgnoreQueryFilters()
            .CountAsync(t => t.StoreId == storeId && t.CreatedAt >= today && t.CreatedAt < tomorrow, ct);
        return $"OPT-{today:yyyyMMdd}-{count + 1:000}";
    }

    private static async Task AddEventAsync(
        AppDbContext db,
        int storeId,
        int ticketId,
        string type,
        string? fromStatus,
        string? toStatus,
        string message,
        string createdBy,
        bool visibleToCustomer,
        CancellationToken ct)
    {
        db.OptimizationTicketEvents.Add(new OptimizationTicketEvent
        {
            StoreId = storeId,
            OptimizationTicketId = ticketId,
            Type = type,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Message = message,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now,
            VisibleToCustomer = visibleToCustomer
        });
        await db.SaveChangesAsync(ct);
    }

    private static object ToTicketDto(OptimizationTicket t, OptimizationDevice? device, ServicoItem? service) => new
    {
        t.Id,
        t.StoreId,
        t.TicketNumber,
        t.PhoneNumber,
        t.CustomerName,
        t.OptimizationDeviceId,
        t.AppointmentId,
        t.ServiceId,
        ServiceName = service?.Nome,
        t.ServiceMode,
        t.Goal,
        t.ReportedProblem,
        t.Urgency,
        Status = t.Status.ToString(),
        t.BeforeNotes,
        t.OptimizationChecklistJson,
        Checklist = ParseChecklist(t.OptimizationChecklistJson),
        t.AfterNotes,
        t.ResultSummary,
        t.EstimatedAmount,
        t.FinalAmount,
        t.CreatedAt,
        t.UpdatedAt,
        t.StartedAt,
        t.CompletedAt,
        t.ClosedAt,
        Device = device
    };

    private static IReadOnlyList<OptimizationChecklistItem> ParseChecklist(string json)
    {
        try { return JsonSerializer.Deserialize<List<OptimizationChecklistItem>>(json) ?? DefaultChecklist(); }
        catch { return DefaultChecklist(); }
    }

    private static string DefaultChecklistJson() => JsonSerializer.Serialize(DefaultChecklist());

    private static List<OptimizationChecklistItem> DefaultChecklist() =>
    [
        new("restore_point", "Ponto de restauracao criado"),
        new("startup_apps", "Programas de inicializacao revisados"),
        new("atlas_os", "ATLAS OS avaliado/aplicado quando combinado"),
        new("islc", "ISLC configurado quando aplicavel"),
        new("process_lasso", "Process Lasso revisado quando aplicavel"),
        new("msi_utility", "MSI Utility verificado"),
        new("exm", "EXM avaliado quando aplicavel"),
        new("storage_check", "Armazenamento verificado"),
        new("power_plan", "Plano de energia ajustado"),
        new("drivers", "Drivers verificados"),
        new("games", "Configuracoes de jogos revisadas"),
        new("services", "Servicos desnecessarios revisados"),
        new("windows_update", "Windows Update verificado"),
        new("final_test", "Teste final realizado"),
        new("customer_guidance", "Orientacoes passadas ao cliente")
    ];

    private static string CurrentUser(HttpContext ctx)
        => ctx.User.FindFirst(ClaimTypes.Name)?.Value
           ?? ctx.User.FindFirst("username")?.Value
           ?? ctx.User.FindFirst(ClaimTypes.Role)?.Value
           ?? "Dashboard";

    private static string NormalizePhoneOrManual(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length is >= 10 and <= 13) return digits;
        return string.IsNullOrWhiteSpace(value) ? $"manual-opt-{DateTime.Now:yyyyMMddHHmmssfff}" : Clean(value, 40, $"manual-opt-{DateTime.Now:yyyyMMddHHmmssfff}");
    }

    private static string Clean(string? value, int maxLength, string fallback = "")
    {
        var cleaned = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = fallback;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string? CleanNullable(string? value, int maxLength)
    {
        if (value == null) return null;
        var cleaned = value.Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static bool ContainsCredentialLikeText(params string?[] values)
    {
        var joined = string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(joined)) return false;
        return joined.Contains("senha") || joined.Contains("password") || joined.Contains("credencial") || joined.Contains("token de acesso");
    }

    public static bool IsValidQuoteAmount(decimal? amount)
        => amount is > 0 and <= 100000;

    private static string NormalizeKey(string? value)
    {
        var text = (value ?? "").Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string GuessDeviceType(string text)
    {
        var key = text.ToLowerInvariant();
        if (key.Contains("note")) return "Notebook";
        if (key.Contains("all in one") || key.Contains("all-in-one")) return "AllInOne";
        return "Desktop";
    }

    private static string GuessOperatingSystem(string text)
    {
        var key = text.ToLowerInvariant();
        if (key.Contains("windows 10") || key.Contains("win10")) return "Windows 10";
        if (key.Contains("windows 11") || key.Contains("win11")) return "Windows 11";
        return "Outro";
    }

    private static string GuessMainUse(string text)
    {
        var key = text.ToLowerInvariant();
        if (key.Contains("fps") || key.Contains("jogo") || key.Contains("gamer")) return "Jogos";
        if (key.Contains("edicao") || key.Contains("render") || key.Contains("video")) return "Edicao";
        if (key.Contains("trabalho")) return "Trabalho";
        if (key.Contains("estudo")) return "Estudo";
        return "Uso geral";
    }

    private static string GuessServiceMode(string? text)
    {
        var key = (text ?? "").ToLowerInvariant();
        if (key.Contains("remoto") || key.Contains("online") || key.Contains("anydesk") || key.Contains("teamviewer")) return "Remoto";
        if (key.Contains("loja") || key.Contains("levar") || key.Contains("entrega")) return "EntregaNaLoja";
        if (key.Contains("presencial")) return "Presencial";
        return "A confirmar";
    }
}

public record OptimizationDeviceRequest(
    string? CustomerName,
    string? PhoneNumber,
    string? DeviceType,
    string? OperatingSystem,
    string? Processor,
    string? Gpu,
    int? RamGb,
    string? StorageType,
    string? MainUse,
    string? Notes,
    bool? IsActive);

public record OptimizationTicketRequest(
    string? CustomerName,
    string? PhoneNumber,
    int? OptimizationDeviceId,
    int? AppointmentId,
    int? ServiceId,
    string? ServiceMode,
    string? Goal,
    string? ReportedProblem,
    string? Urgency,
    string? BeforeNotes,
    decimal? EstimatedAmount,
    decimal? FinalAmount,
    OptimizationDeviceRequest? Device);

public record OptimizationTicketUpdateRequest(
    string? CustomerName,
    string? PhoneNumber,
    int? OptimizationDeviceId,
    int? AppointmentId,
    int? ServiceId,
    string? ServiceMode,
    string? Goal,
    string? ReportedProblem,
    string? Urgency,
    string? BeforeNotes,
    string? AfterNotes,
    string? ResultSummary,
    decimal? EstimatedAmount,
    decimal? FinalAmount);

public record OptimizationStatusRequest(string Status, string? Message, bool? VisibleToCustomer);

public record OptimizationQuoteRequest(decimal? Amount, bool? Approved, string? Message, bool? VisibleToCustomer);

public record OptimizationChecklistRequest(List<OptimizationChecklistItem>? Items, string? Json);

public record OptimizationChecklistItem(string Key, string Label, bool Done = false, string? Notes = null);

public record OptimizationEventRequest(string? Type, string? Message, bool? VisibleToCustomer);
