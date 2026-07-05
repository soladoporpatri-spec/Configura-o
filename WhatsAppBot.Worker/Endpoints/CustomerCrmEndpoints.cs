using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class CustomerCrmEndpoints
{
    private static readonly (string Name, string Color)[] DefaultTags =
    [
        ("VIP", "#a855f7"),
        ("Cliente fiel", "#16a34a"),
        ("Sumido", "#f59e0b"),
        ("Prefere WhatsApp", "#22c55e"),
        ("Barba", "#0ea5e9"),
        ("Corte", "#2563eb"),
        ("Sobrancelha", "#ec4899"),
        ("Atencao especial", "#ef4444"),
        ("Nao compareceu", "#f97316"),
        ("Promocao", "#14b8a6"),
        ("Aniversariante", "#eab308")
    ];

    public static IEndpointRouteBuilder MapCustomerCrmEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/api/customers", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            string? q,
            string? status,
            int? tagId,
            string? service,
            string? professional,
            bool? missingNext,
            int inactiveDays = 60,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;

            await EnsureDefaultTagsAsync(db, guard.StoreId, ct);
            var customers = await BuildCustomersAsync(db, guard.StoreId, inactiveDays, includeHistory: false, ct);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = NormalizeSearch(q);
                customers = customers.Where(c => NormalizeSearch($"{c.Name} {c.PhoneNumber} {c.TopService} {c.PreferredProfessional}")
                    .Contains(needle)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(status))
                customers = customers.Where(c => string.Equals(c.Status, NormalizeStatus(status), StringComparison.OrdinalIgnoreCase)).ToList();

            if (tagId.HasValue)
                customers = customers.Where(c => c.Tags.Any(t => t.Id == tagId.Value)).ToList();

            if (!string.IsNullOrWhiteSpace(service))
                customers = customers.Where(c => string.Equals(c.TopService, service, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrWhiteSpace(professional))
                customers = customers.Where(c => string.Equals(c.PreferredProfessional, professional, StringComparison.OrdinalIgnoreCase)).ToList();

            if (missingNext == true)
                customers = customers.Where(c => c.NextAppointmentAt == null).ToList();

            var allTags = await db.CustomerTags.AsNoTracking()
                .Where(t => t.StoreId == guard.StoreId)
                .OrderBy(t => t.Name)
                .Select(t => ToTagDto(t))
                .ToListAsync(ct);

            return Results.Ok(new
            {
                data = customers,
                summary = BuildSummary(customers),
                segments = BuildSegments(customers),
                tags = allTags
            });
        });

        app.MapGet("/api/customers/reports/summary", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            DateTime? from,
            DateTime? to,
            int inactiveDays = 60,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;

            var customers = await BuildCustomersAsync(db, guard.StoreId, inactiveDays, includeHistory: false, ct);
            var start = (from ?? AgendaService.GetBrazilNow().Date.AddDays(-30)).Date;
            var end = (to ?? AgendaService.GetBrazilNow().Date).Date.AddDays(1).AddTicks(-1);
            var appointments = await db.Appointments.AsNoTracking()
                .Where(a => a.StoreId == guard.StoreId && a.DateTime >= start && a.DateTime <= end)
                .ToListAsync(ct);

            var totalCustomers = customers.Count;
            var recurring = customers.Count(c => c.CompletedAppointments >= 2);
            var inactive = customers.Count(c => c.Status is "sumido" or "inativo" or "em_risco");
            var vip = customers.Count(c => c.Status == "vip" || c.Tags.Any(t => string.Equals(t.Name, "VIP", StringComparison.OrdinalIgnoreCase)));
            var noNext = customers.Count(c => c.NextAppointmentAt == null);
            var noShows = customers.Sum(c => c.NoShows);
            var returnRate = totalCustomers == 0 ? 0 : Math.Round((double)recurring / totalCustomers * 100, 1);
            var avgInterval = customers.Where(c => c.AverageReturnDays.HasValue).Select(c => c.AverageReturnDays!.Value).DefaultIfEmpty(0).Average();
            var recurringRevenue = customers.Where(c => c.CompletedAppointments >= 2).Sum(c => c.TotalSpent);

            return Results.Ok(new
            {
                period = new { from = start.ToString("yyyy-MM-dd"), to = end.ToString("yyyy-MM-dd") },
                totalCustomers,
                newCustomers = appointments
                    .GroupBy(a => CustomerKeyFor(a.PhoneNumber, a.ContactName))
                    .Count(g => g.Min(a => a.DateTime) >= start),
                recurringCustomers = recurring,
                inactiveCustomers = inactive,
                vipCustomers = vip,
                topSpenders = customers.OrderByDescending(c => c.TotalSpent).Take(10),
                topVisits = customers.OrderByDescending(c => c.CompletedAppointments).Take(10),
                returnRate,
                averageReturnDays = Math.Round(avgInterval, 1),
                customersWithoutNextAppointment = noNext,
                noShows,
                recurringRevenue,
                preferredServices = customers
                    .Where(c => !string.IsNullOrWhiteSpace(c.TopService) && c.TopService != "-")
                    .GroupBy(c => c.TopService)
                    .Select(g => new { service = g.Key, customers = g.Count(), revenue = g.Sum(c => c.TotalSpent) })
                    .OrderByDescending(x => x.customers)
                    .Take(10)
            });
        });

        app.MapGet("/api/customers/inactive", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            int inactiveDays = 60,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var customers = await BuildCustomersAsync(db, guard.StoreId, inactiveDays, includeHistory: false, ct);
            return Results.Ok(customers
                .Where(c => c.Status is "sumido" or "inativo" or "em_risco")
                .OrderByDescending(c => c.DaysSinceLast ?? 0));
        });

        app.MapGet("/api/customers/top", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            string by = "spent",
            int take = 10,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var customers = await BuildCustomersAsync(db, guard.StoreId, inactiveDays: 60, includeHistory: false, ct);
            take = Math.Clamp(take, 1, 50);
            var ordered = string.Equals(by, "visits", StringComparison.OrdinalIgnoreCase)
                ? customers.OrderByDescending(c => c.CompletedAppointments)
                : customers.OrderByDescending(c => c.TotalSpent);
            return Results.Ok(ordered.Take(take));
        });

        app.MapGet("/api/customers/{customerKey}/history", async (
            HttpContext ctx,
            string customerKey,
            AppDbContext db,
            ITenantService tenantService,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            var detail = (await BuildCustomersAsync(db, guard.StoreId, inactiveDays: 60, includeHistory: true, ct))
                .FirstOrDefault(c => c.CustomerKey == key);
            return detail == null ? Results.NotFound(new { error = "Cliente nao encontrado." }) : Results.Ok(detail.History);
        });

        app.MapGet("/api/customers/{customerKey}/events", async (
            HttpContext ctx,
            string customerKey,
            AppDbContext db,
            ITenantService tenantService,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            var detail = (await BuildCustomersAsync(db, guard.StoreId, inactiveDays: 60, includeHistory: true, ct))
                .FirstOrDefault(c => c.CustomerKey == key);
            return detail == null ? Results.NotFound(new { error = "Cliente nao encontrado." }) : Results.Ok(detail.Events);
        });

        app.MapGet("/api/customers/{customerKey}", async (
            HttpContext ctx,
            string customerKey,
            AppDbContext db,
            ITenantService tenantService,
            int inactiveDays = 60,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            var detail = (await BuildCustomersAsync(db, guard.StoreId, inactiveDays, includeHistory: true, ct))
                .FirstOrDefault(c => c.CustomerKey == key);
            return detail == null ? Results.NotFound(new { error = "Cliente nao encontrado." }) : Results.Ok(detail);
        });

        app.MapPatch("/api/customers/{customerKey}", async (
            HttpContext ctx,
            string customerKey,
            AppDbContext db,
            ITenantService tenantService,
            CustomerCrmUpdateRequest req,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "Cliente sem telefone nao pode salvar dados persistentes." });

            var profile = await db.CustomerProfiles.FirstOrDefaultAsync(c => c.StoreId == guard.StoreId && c.CustomerKey == key, ct);
            if (profile == null)
            {
                profile = new CustomerProfile
                {
                    StoreId = guard.StoreId,
                    CustomerKey = key,
                    PhoneNumber = key,
                    CreatedAt = DateTime.UtcNow
                };
                db.CustomerProfiles.Add(profile);
            }

            var beforeNotes = profile.InternalNotes;
            if (!string.IsNullOrWhiteSpace(req.DisplayName)) profile.DisplayName = Clean(req.DisplayName, 90);
            profile.ManualStatus = CleanNullable(req.ManualStatus, 40);
            profile.IsBlocked = req.IsBlocked ?? profile.IsBlocked;
            profile.InternalNotes = CleanNullable(req.InternalNotes, 1600);
            profile.Preferences = CleanNullable(req.Preferences, 1200);
            profile.PreferredService = CleanNullable(req.PreferredService, 90);
            profile.PreferredProfessional = CleanNullable(req.PreferredProfessional, 90);
            profile.BestTime = CleanNullable(req.BestTime, 40);
            profile.ReturnFrequencyDays = req.ReturnFrequencyDays is > 0 and <= 365 ? req.ReturnFrequencyDays : profile.ReturnFrequencyDays;
            profile.ContactPreference = CleanNullable(req.ContactPreference, 40);
            profile.Birthday = req.Birthday;
            profile.Source = CleanNullable(req.Source, 90);
            profile.UpdatedAt = DateTime.UtcNow;

            if (!string.Equals(beforeNotes, profile.InternalNotes, StringComparison.Ordinal))
                db.CustomerEvents.Add(NewEvent(guard.StoreId, key, "note", "Observacao atualizada", "Observacao interna alterada.", CurrentUser(ctx), false));

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true, customerKey = key });
        });

        app.MapPost("/api/customers/{customerKey}/events", async (
            HttpContext ctx,
            string customerKey,
            AppDbContext db,
            ITenantService tenantService,
            CustomerEventRequest req,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "Cliente invalido." });
            var ev = NewEvent(
                guard.StoreId,
                key,
                Clean(req.Type, 40, "note"),
                Clean(req.Title, 120, "Anotacao"),
                CleanNullable(req.Description, 1000),
                CurrentUser(ctx),
                req.VisibleToCustomer ?? false);
            ev.RelatedAppointmentId = req.RelatedAppointmentId;
            db.CustomerEvents.Add(ev);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/customers/{key}/events/{ev.Id}", ToEventDto(ev));
        });

        app.MapGet("/api/customer-tags", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            await EnsureDefaultTagsAsync(db, guard.StoreId, ct);
            return Results.Ok(await db.CustomerTags.AsNoTracking()
                .Where(t => t.StoreId == guard.StoreId)
                .OrderBy(t => t.Name)
                .Select(t => ToTagDto(t))
                .ToListAsync(ct));
        });

        app.MapPost("/api/customer-tags", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            CustomerTagRequest req,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var name = Clean(req.Name, 40);
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Informe o nome da tag." });
            var existing = await db.CustomerTags.FirstOrDefaultAsync(t => t.StoreId == guard.StoreId && t.Name.ToLower() == name.ToLower(), ct);
            if (existing != null) return Results.Ok(ToTagDto(existing));
            var tag = new CustomerTag { StoreId = guard.StoreId, Name = name, Color = Clean(req.Color, 20, "#64748b"), IsSystem = false };
            db.CustomerTags.Add(tag);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/customer-tags/{tag.Id}", ToTagDto(tag));
        });

        app.MapPost("/api/customers/{customerKey}/tags", async (
            HttpContext ctx,
            string customerKey,
            AppDbContext db,
            ITenantService tenantService,
            CustomerTagAssignRequest req,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "Cliente invalido." });
            await EnsureDefaultTagsAsync(db, guard.StoreId, ct);

            CustomerTag? tag = null;
            if (req.TagId.HasValue)
                tag = await db.CustomerTags.FirstOrDefaultAsync(t => t.StoreId == guard.StoreId && t.Id == req.TagId.Value, ct);
            if (tag == null && !string.IsNullOrWhiteSpace(req.Name))
            {
                var name = Clean(req.Name, 40);
                tag = await db.CustomerTags.FirstOrDefaultAsync(t => t.StoreId == guard.StoreId && t.Name.ToLower() == name.ToLower(), ct);
                if (tag == null)
                {
                    tag = new CustomerTag { StoreId = guard.StoreId, Name = name, Color = Clean(req.Color, 20, "#64748b") };
                    db.CustomerTags.Add(tag);
                    await db.SaveChangesAsync(ct);
                }
            }
            if (tag == null) return Results.BadRequest(new { error = "Tag invalida." });

            var exists = await db.CustomerTagAssignments.AnyAsync(a => a.StoreId == guard.StoreId && a.CustomerKey == key && a.CustomerTagId == tag.Id, ct);
            if (!exists)
            {
                db.CustomerTagAssignments.Add(new CustomerTagAssignment { StoreId = guard.StoreId, CustomerKey = key, CustomerTagId = tag.Id });
                db.CustomerEvents.Add(NewEvent(guard.StoreId, key, "tag", "Tag adicionada", tag.Name, CurrentUser(ctx), false));
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(ToTagDto(tag));
        });

        app.MapDelete("/api/customers/{customerKey}/tags/{tagId:int}", async (
            HttpContext ctx,
            string customerKey,
            int tagId,
            AppDbContext db,
            ITenantService tenantService,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(customerKey);
            var assignment = await db.CustomerTagAssignments.FirstOrDefaultAsync(a => a.StoreId == guard.StoreId && a.CustomerKey == key && a.CustomerTagId == tagId, ct);
            if (assignment == null) return Results.NotFound(new { error = "Tag nao encontrada para este cliente." });
            db.CustomerTagAssignments.Remove(assignment);
            db.CustomerEvents.Add(NewEvent(guard.StoreId, key, "tag", "Tag removida", $"Tag #{tagId}", CurrentUser(ctx), false));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/customer-reminders", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            string? customerKey,
            string? status,
            string? due,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var query = db.CustomerReminders.AsNoTracking().Where(r => r.StoreId == guard.StoreId);
            if (!string.IsNullOrWhiteSpace(customerKey))
            {
                var key = NormalizeCustomerKey(customerKey);
                query = query.Where(r => r.CustomerKey == key);
            }
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == NormalizeStatus(status));
            var today = AgendaService.GetBrazilNow().Date;
            if (string.Equals(due, "overdue", StringComparison.OrdinalIgnoreCase)) query = query.Where(r => r.DueDate.Date < today && r.Status == "pendente");
            if (string.Equals(due, "today", StringComparison.OrdinalIgnoreCase)) query = query.Where(r => r.DueDate.Date == today && r.Status == "pendente");
            return Results.Ok(await query.OrderBy(r => r.DueDate).Select(r => ToReminderDto(r)).ToListAsync(ct));
        });

        app.MapPost("/api/customer-reminders", async (
            HttpContext ctx,
            AppDbContext db,
            ITenantService tenantService,
            CustomerReminderRequest req,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var key = NormalizeCustomerKey(req.CustomerKey);
            if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { error = "Cliente invalido." });
            var title = Clean(req.Title, 120);
            if (string.IsNullOrWhiteSpace(title)) return Results.BadRequest(new { error = "Informe o titulo do lembrete." });
            var reminder = new CustomerReminder
            {
                StoreId = guard.StoreId,
                CustomerKey = key,
                Title = title,
                Description = CleanNullable(req.Description, 600),
                DueDate = req.DueDate.Date,
                Status = "pendente"
            };
            db.CustomerReminders.Add(reminder);
            db.CustomerEvents.Add(NewEvent(guard.StoreId, key, "reminder", "Lembrete criado", title, CurrentUser(ctx), false));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/customer-reminders/{reminder.Id}", ToReminderDto(reminder));
        });

        app.MapPatch("/api/customer-reminders/{id:int}", async (
            HttpContext ctx,
            int id,
            AppDbContext db,
            ITenantService tenantService,
            CustomerReminderUpdateRequest req,
            CancellationToken ct = default) =>
        {
            var guard = await RequireCrmStoreAsync(ctx, db, tenantService, apiKey, ct);
            if (guard.Error != null) return guard.Error;
            var reminder = await db.CustomerReminders.FirstOrDefaultAsync(r => r.StoreId == guard.StoreId && r.Id == id, ct);
            if (reminder == null) return Results.NotFound(new { error = "Lembrete nao encontrado." });
            if (!string.IsNullOrWhiteSpace(req.Title)) reminder.Title = Clean(req.Title, 120);
            if (req.Description != null) reminder.Description = CleanNullable(req.Description, 600);
            if (req.DueDate.HasValue) reminder.DueDate = req.DueDate.Value.Date;
            if (!string.IsNullOrWhiteSpace(req.Status))
            {
                reminder.Status = NormalizeReminderStatus(req.Status);
                reminder.CompletedAt = reminder.Status == "concluido" ? DateTime.UtcNow : null;
                db.CustomerEvents.Add(NewEvent(guard.StoreId, reminder.CustomerKey, "reminder", "Lembrete atualizado", reminder.Status, CurrentUser(ctx), false));
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToReminderDto(reminder));
        });

        return app;
    }

    public static string NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return new string(value.Where(char.IsDigit).ToArray());
    }

    public static string CustomerKeyFor(string? phone, string? name)
    {
        var phoneDigits = NormalizePhone(phone);
        if (!string.IsNullOrWhiteSpace(phoneDigits)) return phoneDigits;
        var cleanedName = NormalizeSearch(name ?? "cliente");
        return $"walkin-{Math.Abs(cleanedName.GetHashCode()):x}";
    }

    public static string ComputeStatus(int completedAppointments, decimal totalSpent, int? daysSinceLast, double? averageReturnDays, bool isBlocked, string? manualStatus, IEnumerable<string>? tagNames, int inactiveDays = 60)
    {
        if (isBlocked) return "bloqueado";
        var manual = NormalizeStatus(manualStatus);
        if (!string.IsNullOrWhiteSpace(manual) && manual != "auto") return manual;
        var tags = (tagNames ?? Array.Empty<string>()).Select(NormalizeSearch).ToList();
        if (tags.Contains("vip") || totalSpent >= 500m) return "vip";
        if (daysSinceLast.HasValue && daysSinceLast.Value >= Math.Max(90, inactiveDays * 2)) return "inativo";
        if (daysSinceLast.HasValue && daysSinceLast.Value >= inactiveDays) return "sumido";
        if (completedAppointments >= 2 && averageReturnDays.HasValue && daysSinceLast.HasValue && daysSinceLast.Value > Math.Max(30, averageReturnDays.Value * 1.5)) return "em_risco";
        if (completedAppointments >= 5) return "fiel";
        if (completedAppointments >= 2) return "recorrente";
        return "novo";
    }

    private static async Task<(IResult? Error, int StoreId)> RequireCrmStoreAsync(HttpContext ctx, AppDbContext db, ITenantService tenantService, string apiKey, CancellationToken ct)
    {
        if (!EndpointAuth.IsAuthenticated(ctx, apiKey)) return (Results.Unauthorized(), 0);
        var guard = await EndpointTenantGuard.RequireOperationalStoreAsync(db, tenantService, ct);
        if (guard.Error != null) return (guard.Error, guard.StoreId);
        db.TenantId = guard.StoreId;
        return (null, guard.StoreId);
    }

    private static async Task EnsureDefaultTagsAsync(AppDbContext db, int storeId, CancellationToken ct)
    {
        var existing = await db.CustomerTags.Where(t => t.StoreId == storeId).Select(t => t.Name.ToLower()).ToListAsync(ct);
        foreach (var (name, color) in DefaultTags)
        {
            if (existing.Contains(name.ToLower())) continue;
            db.CustomerTags.Add(new CustomerTag { StoreId = storeId, Name = name, Color = color, IsSystem = true });
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task<List<CustomerDto>> BuildCustomersAsync(AppDbContext db, int storeId, int inactiveDays, bool includeHistory, CancellationToken ct)
    {
        var now = AgendaService.GetBrazilNow();
        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.StoreId == storeId)
            .OrderByDescending(a => a.DateTime)
            .ToListAsync(ct);
        var profiles = await db.CustomerProfiles.AsNoTracking().Where(p => p.StoreId == storeId).ToDictionaryAsync(p => p.CustomerKey, ct);
        var tags = await db.CustomerTags.AsNoTracking().Where(t => t.StoreId == storeId).ToDictionaryAsync(t => t.Id, ct);
        var assignments = await db.CustomerTagAssignments.AsNoTracking().Where(a => a.StoreId == storeId).ToListAsync(ct);
        var events = await db.CustomerEvents.AsNoTracking().Where(e => e.StoreId == storeId).OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        var reminders = await db.CustomerReminders.AsNoTracking().Where(r => r.StoreId == storeId).OrderBy(r => r.DueDate).ToListAsync(ct);
        var serviceNames = await db.Servicos.AsNoTracking().Where(s => s.StoreId == storeId).ToDictionaryAsync(s => s.Id, s => s.Nome, ct);
        var businessType = await db.Stores.AsNoTracking()
            .Where(s => s.Id == storeId)
            .Select(s => s.BusinessType)
            .FirstOrDefaultAsync(ct);

        return appointments
            .GroupBy(a => CustomerKeyFor(a.PhoneNumber, a.ContactName))
            .Select(group =>
            {
                var key = group.Key;
                profiles.TryGetValue(key, out var profile);
                var ordered = group.OrderBy(a => a.DateTime).ToList();
                var activePast = ordered.Where(a => a.Status == "ativo" && a.DateTime <= now).ToList();
                var cancelled = ordered.Where(a => a.Status != "ativo" || a.CancelledAt.HasValue).ToList();
                var noShows = activePast.Count(a => !a.PresencaConfirmada && a.DateTime.Date < now.Date);
                var next = ordered.Where(a => a.Status == "ativo" && a.DateTime >= now).OrderBy(a => a.DateTime).FirstOrDefault();
                var last = activePast.OrderByDescending(a => a.DateTime).FirstOrDefault();
                var first = ordered.OrderBy(a => a.DateTime).FirstOrDefault();
                var completed = activePast.Count;
                var totalSpent = activePast.Sum(a => a.Preco);
                var serviceStats = activePast.GroupBy(a => a.ServiceId).OrderByDescending(g => g.Count()).FirstOrDefault();
                var professional = activePast.Where(a => !string.IsNullOrWhiteSpace(a.BarberName)).GroupBy(a => a.BarberName!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                var commonHour = activePast.GroupBy(a => a.DateTime.Hour).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                var daysSinceLast = last == null ? (int?)null : Math.Max(0, (int)Math.Floor((now.Date - last.DateTime.Date).TotalDays));
                var intervals = ordered.Where(a => a.Status == "ativo" && a.DateTime <= now).Select(a => a.DateTime.Date).Distinct().OrderBy(d => d).ToList();
                double? avgInterval = null;
                if (intervals.Count > 1)
                    avgInterval = intervals.Zip(intervals.Skip(1), (a, b) => (b - a).TotalDays).Average();
                var customerTags = assignments
                    .Where(a => a.CustomerKey == key && tags.ContainsKey(a.CustomerTagId))
                    .Select(a => tags[a.CustomerTagId])
                    .Select(ToTagDto)
                    .OrderBy(t => t.Name)
                    .ToList();
                var status = ComputeStatus(completed, totalSpent, daysSinceLast, avgInterval, profile?.IsBlocked ?? false, profile?.ManualStatus, customerTags.Select(t => t.Name), inactiveDays);
                var name = !string.IsNullOrWhiteSpace(profile?.DisplayName) ? profile!.DisplayName : ordered.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.ContactName))?.ContactName ?? "Cliente";
                var phone = NormalizePhone(ordered.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.PhoneNumber))?.PhoneNumber ?? profile?.PhoneNumber);
                var history = includeHistory ? ordered.OrderByDescending(a => a.DateTime).Select(a => ToHistoryDto(a, serviceNames)).ToList() : [];
                var generatedEvents = includeHistory ? BuildGeneratedEvents(ordered, serviceNames).ToList() : [];
                var manualEvents = includeHistory ? events.Where(e => e.CustomerKey == key).Select(ToEventDto).ToList() : [];
                var customerReminderEntities = reminders.Where(r => r.CustomerKey == key).ToList();
                var customerReminders = customerReminderEntities.Select(ToReminderDto).ToList();

                return new CustomerDto(
                    key,
                    phone,
                    name,
                    first?.CreatedAt ?? first?.DateTime,
                    last?.DateTime,
                    next?.DateTime,
                    ordered.Count,
                    completed,
                    noShows,
                    cancelled.Count,
                    totalSpent,
                    completed == 0 ? 0 : Math.Round(totalSpent / completed, 2),
                    serviceStats == null ? "-" : serviceNames.GetValueOrDefault(serviceStats.Key, $"Servico #{serviceStats.Key}"),
                    professional ?? profile?.PreferredProfessional ?? "-",
                    commonHour.HasValue ? $"{commonHour:00}:00" : profile?.BestTime ?? "-",
                    daysSinceLast,
                    avgInterval.HasValue ? Math.Round(avgInterval.Value, 1) : null,
                    status,
                    StatusLabel(status),
                    customerTags,
                    profile?.InternalNotes,
                    profile?.Preferences,
                    profile?.PreferredService,
                    profile?.PreferredProfessional,
                    profile?.BestTime,
                    profile?.ReturnFrequencyDays,
                    profile?.ContactPreference,
                    profile?.Birthday,
                    profile?.Source,
                    customerReminderEntities.Count(r => r.Status == "pendente" && r.DueDate.Date <= now.Date),
                    SuggestedMessages(name, phone, daysSinceLast, serviceStats == null ? "" : serviceNames.GetValueOrDefault(serviceStats.Key, ""), businessType),
                    history,
                    generatedEvents.Concat(manualEvents).OrderByDescending(e => e.CreatedAt).Take(80).ToList(),
                    customerReminders);
            })
            .OrderByDescending(c => c.LastAppointmentAt ?? c.FirstAppointmentAt ?? DateTime.MinValue)
            .ToList();
    }

    private static object BuildSummary(List<CustomerDto> customers) => new
    {
        total = customers.Count,
        recurring = customers.Count(c => c.CompletedAppointments >= 2),
        faithful = customers.Count(c => c.Status is "fiel" or "vip"),
        inactive = customers.Count(c => c.Status is "sumido" or "inativo" or "em_risco"),
        vip = customers.Count(c => c.Status == "vip" || c.Tags.Any(t => string.Equals(t.Name, "VIP", StringComparison.OrdinalIgnoreCase))),
        noNext = customers.Count(c => c.NextAppointmentAt == null),
        revenue = customers.Sum(c => c.TotalSpent),
        pendingReminders = customers.Sum(c => c.PendingReminders)
    };

    private static object BuildSegments(List<CustomerDto> customers) => new
    {
        inactive = customers.Where(c => c.Status is "sumido" or "inativo" or "em_risco").Take(25),
        vip = customers.Where(c => c.Status == "vip" || c.Tags.Any(t => t.Name == "VIP")).Take(25),
        noNext = customers.Where(c => c.NextAppointmentAt == null).Take(25),
        top = customers.OrderByDescending(c => c.TotalSpent).Take(10)
    };

    private static IEnumerable<CustomerEventDto> BuildGeneratedEvents(List<Appointment> appointments, Dictionary<int, string> serviceNames)
    {
        foreach (var a in appointments)
        {
            var service = serviceNames.GetValueOrDefault(a.ServiceId, $"Servico #{a.ServiceId}");
            if (a.Status != "ativo" || a.CancelledAt.HasValue)
            {
                yield return new CustomerEventDto(0, "cancelamento", "Agendamento cancelado", service, a.Id, "Sistema", a.CancelledAt ?? a.DateTime, false);
                continue;
            }
            yield return new CustomerEventDto(0, "appointment", a.PresencaConfirmada ? "Atendimento concluido" : "Agendamento criado", $"{service} com {a.BarberName ?? "profissional"}", a.Id, "Agenda", a.DateTime, false);
        }
    }

    private static object ToHistoryDto(Appointment a, Dictionary<int, string> serviceNames) => new
    {
        a.Id,
        dateTime = a.DateTime,
        service = serviceNames.GetValueOrDefault(a.ServiceId, $"Servico #{a.ServiceId}"),
        professional = a.BarberName ?? "Profissional",
        price = a.Preco,
        durationMinutes = a.DuracaoMinutos,
        status = a.Status == "ativo" ? (a.PresencaConfirmada ? "concluido" : "agendado") : "cancelado",
        noShow = a.Status == "ativo" && !a.PresencaConfirmada && a.DateTime.Date < AgendaService.GetBrazilNow().Date,
        notes = a.Notes
    };

    private static object SuggestedMessages(string name, string phone, int? daysSinceLast, string topService, BusinessType businessType)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "tudo bem" : name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        var reactivation = $"Oi, {safeName}! Tudo bem? Ja faz um tempo desde seu ultimo atendimento. Quer que eu veja um horario para voce esta semana?";
        var post = $"Oi, {safeName}! Passando para agradecer pela visita. Quando quiser manter o visual em dia, me chama por aqui.";
        var vip = $"Oi, {safeName}! Separei prioridade para voce na agenda. Quer ver os melhores horarios desta semana?";
        var loyalty = $"Oi, {safeName}! Seus atendimentos ja estao contando para beneficios de fidelidade da barbearia.";

        if (businessType == BusinessType.ComputerOptimization)
        {
            reactivation = $"Oi, {safeName}! Faz um tempo desde sua ultima otimizacao. Quer que eu confira um horario para revisar seu equipamento esta semana?";
            post = $"Oi, {safeName}! Obrigado pela visita. Se o PC ou console precisar de ajuste, me chama por aqui.";
            vip = $"Oi, {safeName}! Separei prioridade para seu atendimento tecnico. Quer ver os melhores horarios desta semana?";
            loyalty = $"Oi, {safeName}! Seus atendimentos ja contam para beneficios de fidelidade da loja.";
        }
        else if (businessType != BusinessType.Barbershop)
        {
            post = $"Oi, {safeName}! Passando para agradecer pela visita. Quando precisar de um novo atendimento, me chama por aqui.";
            loyalty = $"Oi, {safeName}! Seus atendimentos ja contam para beneficios de fidelidade da loja.";
        }

        return new
        {
            whatsappUrl = string.IsNullOrWhiteSpace(phone) ? null : $"https://wa.me/{phone}",
            reactivation,
            postService = post,
            birthday = $"Parabens, {safeName}! A equipe preparou uma lembranca para sua proxima visita.",
            loyalty,
            vip,
            missing = daysSinceLast.HasValue ? $"Cliente esta ha {daysSinceLast} dias sem retorno. Servico mais usado: {topService}." : ""
        };
    }

    private static CustomerEvent NewEvent(int storeId, string key, string type, string title, string? description, string createdBy, bool visible)
        => new()
        {
            StoreId = storeId,
            CustomerKey = key,
            Type = type,
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            VisibleToCustomer = visible,
            CreatedAt = DateTime.UtcNow
        };

    private static CustomerTagDto ToTagDto(CustomerTag t) => new(t.Id, t.Name, t.Color, t.IsSystem);
    private static CustomerEventDto ToEventDto(CustomerEvent e) => new(e.Id, e.Type, e.Title, e.Description, e.RelatedAppointmentId, e.CreatedBy, e.CreatedAt, e.VisibleToCustomer);
    private static object ToReminderDto(CustomerReminder r) => new { r.Id, r.CustomerKey, r.Title, r.Description, dueDate = r.DueDate, r.Status, r.CreatedAt, r.CompletedAt };

    private static string CurrentUser(HttpContext ctx)
        => ctx.User.FindFirst(ClaimTypes.Name)?.Value
           ?? ctx.User.FindFirst("username")?.Value
           ?? ctx.User.Identity?.Name
           ?? "Dashboard";

    private static string NormalizeCustomerKey(string? value)
        => Uri.UnescapeDataString(NormalizePhone(value));

    private static string NormalizeStatus(string? value)
    {
        var key = NormalizeSearch(value);
        return key switch
        {
            "em risco" => "em_risco",
            "em_risco" => "em_risco",
            "cliente fiel" => "fiel",
            "vip" => "vip",
            "sumido" => "sumido",
            "inativo" => "inativo",
            "bloqueado" => "bloqueado",
            "recorrente" => "recorrente",
            "fiel" => "fiel",
            "novo" => "novo",
            "auto" => "auto",
            _ => ""
        };
    }

    private static string NormalizeReminderStatus(string value)
    {
        var key = NormalizeSearch(value);
        return key is "concluido" or "concluida" ? "concluido" : key is "cancelado" or "cancelada" ? "cancelado" : "pendente";
    }

    private static string StatusLabel(string status) => status switch
    {
        "vip" => "VIP",
        "fiel" => "Fiel",
        "recorrente" => "Recorrente",
        "sumido" => "Sumido",
        "em_risco" => "Em risco",
        "inativo" => "Inativo",
        "bloqueado" => "Bloqueado",
        _ => "Novo"
    };

    private static string NormalizeSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var chars = normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(chars);
    }

    private static string Clean(string? value, int max, string fallback = "")
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return text.Length <= max ? text : text[..max];
    }

    private static string? CleanNullable(string? value, int max)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= max ? text : text[..max];
    }
}

public record CustomerCrmUpdateRequest(
    string? DisplayName,
    string? ManualStatus,
    bool? IsBlocked,
    string? InternalNotes,
    string? Preferences,
    string? PreferredService,
    string? PreferredProfessional,
    string? BestTime,
    int? ReturnFrequencyDays,
    string? ContactPreference,
    DateTime? Birthday,
    string? Source);

public record CustomerEventRequest(string? Type, string? Title, string? Description, int? RelatedAppointmentId, bool? VisibleToCustomer);
public record CustomerTagRequest(string? Name, string? Color);
public record CustomerTagAssignRequest(int? TagId, string? Name, string? Color);
public record CustomerReminderRequest(string? CustomerKey, string? Title, string? Description, DateTime DueDate);
public record CustomerReminderUpdateRequest(string? Title, string? Description, DateTime? DueDate, string? Status);

public record CustomerTagDto(int Id, string Name, string Color, bool IsSystem);
public record CustomerEventDto(int Id, string Type, string Title, string? Description, int? RelatedAppointmentId, string CreatedBy, DateTime CreatedAt, bool VisibleToCustomer);

public record CustomerDto(
    string CustomerKey,
    string PhoneNumber,
    string Name,
    DateTime? FirstAppointmentAt,
    DateTime? LastAppointmentAt,
    DateTime? NextAppointmentAt,
    int TotalAppointments,
    int CompletedAppointments,
    int NoShows,
    int Cancellations,
    decimal TotalSpent,
    decimal AverageTicket,
    string TopService,
    string PreferredProfessional,
    string CommonTime,
    int? DaysSinceLast,
    double? AverageReturnDays,
    string Status,
    string StatusLabel,
    List<CustomerTagDto> Tags,
    string? InternalNotes,
    string? Preferences,
    string? PreferredService,
    string? ManualPreferredProfessional,
    string? BestTime,
    int? ReturnFrequencyDays,
    string? ContactPreference,
    DateTime? Birthday,
    string? Source,
    int PendingReminders,
    object SuggestedMessages,
    List<object> History,
    List<CustomerEventDto> Events,
    List<object> Reminders);
