using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;
using WhatsAppBot.Worker.Services.Modules;

namespace WhatsAppBot.Worker.Endpoints;

public static class SuperAdminEndpoints
{
    private const int BridgeBasePort = 3000;
    private const string DefaultBackendUrl = "http://127.0.0.1:5000";

    public static IEndpointRouteBuilder MapSuperAdminEndpoints(this IEndpointRouteBuilder app, string apiKey)
    {
        app.MapGet("/superadmin/global-stats", () => Results.Redirect("/api/superadmin/global-stats"));
        app.MapGet("/superadmin/overview", () => Results.Redirect("/api/superadmin/overview"));
        app.MapGet("/superadmin/subscriptions", () => Results.Redirect("/api/superadmin/subscriptions"));
        app.MapGet("/superadmin/stores", () => Results.Redirect("/api/superadmin/stores"));
        app.MapGet("/superadmin/stores/{id}/users", (int id) => Results.Redirect($"/api/superadmin/stores/{id}/users"));

        app.MapGet("/api/superadmin/global-stats", async (HttpContext ctx, AppDbContext db) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0; // Superadmin vê tudo

            var hoje = DateTime.Today;
            var amanha = hoje.AddDays(1);

            var totalAppointments = await db.Appointments.IgnoreQueryFilters().CountAsync();
            var totalRevenue = await db.Appointments.IgnoreQueryFilters().Select(a => (double?)a.Preco).SumAsync() ?? 0;
            var totalStoresCount = await db.Set<Store>().CountAsync();
            var activeStoresCount = await db.Set<Store>().CountAsync(s => s.IsActive);
            var mrrEstimado = db.Set<Store>().Where(s => s.IsActive).AsEnumerable()
                .Sum(s => PlanCatalog.MonthlyPrice(s.Plan));

            // Carrega agendamentos em memória para evitar problemas de tradução SQL com groupby+where
            var allAppointments = await db.Appointments.IgnoreQueryFilters()
                .AsNoTracking()
                .Select(a => new { a.StoreId, a.DateTime, a.Preco })
                .ToListAsync();

            var statsPorLoja = allAppointments
                .GroupBy(a => a.StoreId)
                .Select(g => new
                {
                    StoreId = g.Key,
                    TotalAgendamentos = g.Count(),
                    ReceitaHoje = g.Where(a => a.DateTime >= hoje && a.DateTime < amanha).Sum(a => (double)a.Preco),
                    ReceitaTotal = g.Sum(a => (double)a.Preco),
                    UltimoAgendamento = g.Max(a => (DateTime?)a.DateTime)
                })
                .ToList();

            return Results.Ok(new
            {
                Global = new
                {
                    ReceitaTotal = totalRevenue,
                    TotalAgendamentos = totalAppointments,
                    LojasAtivas = activeStoresCount,
                    TotalLojas = totalStoresCount,
                    MRREstimado = mrrEstimado,
                    TicketMedio = totalAppointments > 0 ? totalRevenue / totalAppointments : 0
                },
                DetalhesPorLoja = statsPorLoja,
                Timestamp = DateTime.UtcNow
            });
        });

        app.MapGet("/api/superadmin/overview", async (HttpContext ctx, AppDbContext db, ModuleCatalog moduleCatalog, FeatureAccessService featureAccess) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var now = DateTime.Now;
            var today = DateTime.Today;
            var recentFrom = now.AddDays(-30);
            var staleFrom = now.AddDays(-14);

            var stores = await db.Stores
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .ThenBy(s => s.Id)
                .ToListAsync();

            var storeIds = stores.Select(s => s.Id).ToList();

            var appointments = await db.Appointments.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(a => storeIds.Contains(a.StoreId))
                .Select(a => new
                {
                    a.StoreId,
                    a.PhoneNumber,
                    a.ContactName,
                    a.DateTime,
                    a.CreatedAt,
                    a.ServiceId,
                    a.Status,
                    Price = (double)a.Preco
                })
                .ToListAsync();

            var users = await db.Users.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => storeIds.Contains(u.StoreId))
                .Select(u => new { u.StoreId, u.Role, u.Username, u.PhoneNumber })
                .ToListAsync();

            var services = await db.Servicos.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => storeIds.Contains(s.StoreId))
                .Select(s => new { s.Id, s.StoreId, s.Nome, s.Ativo, s.Preco })
                .ToListAsync();

            var professionals = await db.Barbeiros.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => storeIds.Contains(b.StoreId))
                .Select(b => new { b.StoreId, b.Nome, b.Ativo })
                .ToListAsync();

            var customers = await db.CustomerProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => storeIds.Contains(c.StoreId))
                .Select(c => new { c.StoreId, c.CustomerKey, c.PhoneNumber, c.DisplayName, c.CreatedAt, c.UpdatedAt })
                .ToListAsync();

            var sessions = await db.ConversationSessions.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => storeIds.Contains(s.StoreId))
                .Select(s => new { s.StoreId, s.Phone, s.CustomerName, s.LastInteraction, State = s.State.ToString() })
                .ToListAsync();

            var subscriptionPlans = await db.SubscriptionPlans.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => storeIds.Contains(p.StoreId))
                .Select(p => new { p.StoreId, p.Nome, p.Preco, p.Ativo, p.CreatedAt })
                .ToListAsync();

            var clientSubscriptions = await db.ClientSubscriptions.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => storeIds.Contains(s.StoreId))
                .Select(s => new
                {
                    s.Id,
                    s.StoreId,
                    s.ClientPhone,
                    s.ClientName,
                    s.PlanId,
                    s.PlanNome,
                    s.PlanPreco,
                    s.CreditosTotal,
                    s.CreditosUsados,
                    Status = s.Status.ToString(),
                    s.CreatedAt,
                    s.StartDate,
                    s.EndDate,
                    s.Notes
                })
                .ToListAsync();

            var techTickets = await db.OptimizationTickets.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => storeIds.Contains(t.StoreId))
                .Select(t => new { t.StoreId, t.TicketNumber, Status = t.Status.ToString(), t.CreatedAt, t.UpdatedAt, t.FinalAmount, t.EstimatedAmount })
                .ToListAsync();

            var storePayments = await db.StorePaymentRecords.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => storeIds.Contains(p.StoreId))
                .Select(p => new
                {
                    p.Id,
                    p.StoreId,
                    p.Plan,
                    p.Amount,
                    p.PaymentMode,
                    Status = p.Status.ToString(),
                    p.PaidUntil,
                    p.CreatedAt,
                    p.ConfirmedAt
                })
                .ToListAsync();

            var logs = await db.AuditLogs.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(l => l.StoreId == 0 || storeIds.Contains(l.StoreId))
                .OrderByDescending(l => l.Timestamp)
                .Take(80)
                .Select(l => new { l.Id, l.StoreId, l.Timestamp, l.User, l.Action, l.Details })
                .ToListAsync();

            var serviceNameByKey = services.ToDictionary(s => (s.StoreId, s.Id), s => s.Nome);
            string ServiceNameFor(int storeId, int serviceId) =>
                serviceNameByKey.TryGetValue((storeId, serviceId), out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : $"Servico #{serviceId}";

            var appointmentsByStore = appointments
                .GroupBy(a => a.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var usersByStore = users
                .GroupBy(u => u.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var servicesByStore = services
                .GroupBy(s => s.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var professionalsByStore = professionals
                .GroupBy(p => p.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var customerProfilesByStore = customers
                .GroupBy(c => c.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var sessionsByStore = sessions
                .GroupBy(s => s.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var subscriptionPlansByStore = subscriptionPlans
                .GroupBy(p => p.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var clientSubscriptionsByStore = clientSubscriptions
                .GroupBy(s => s.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var techTicketsByStore = techTickets
                .GroupBy(t => t.StoreId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var paymentsByStore = storePayments
                .GroupBy(p => p.StoreId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CreatedAt).ToList());

            var logsByStore = logs
                .Where(l => l.StoreId > 0)
                .GroupBy(l => l.StoreId)
                .ToDictionary(g => g.Key, g => g.Take(5).ToList());

            var modulesByStore = new Dictionary<int, IReadOnlyList<TenantModuleStatus>>();
            foreach (var store in stores)
            {
                modulesByStore[store.Id] = await featureAccess.GetModulesForStoreAsync(store.Id, ctx.RequestAborted);
            }

            bool ModuleEnabledFor(Store store, ModuleDefinition module)
            {
                return modulesByStore.TryGetValue(store.Id, out var modules)
                    && modules.Any(m => string.Equals(m.Key, module.Key, StringComparison.OrdinalIgnoreCase) && m.Enabled);
            }

            bool ModuleEligibleFor(Store store, ModuleDefinition module)
                => module.Supports(store.BusinessType) && PlanCatalog.Allows(store.Plan, module.MinimumPlan);

            var storeSummaries = stores.Select(store =>
            {
                appointmentsByStore.TryGetValue(store.Id, out var storeAppointments);
                usersByStore.TryGetValue(store.Id, out var storeUsers);
                servicesByStore.TryGetValue(store.Id, out var storeServices);
                professionalsByStore.TryGetValue(store.Id, out var storeProfessionals);
                customerProfilesByStore.TryGetValue(store.Id, out var storeCustomers);
                sessionsByStore.TryGetValue(store.Id, out var storeSessions);
                subscriptionPlansByStore.TryGetValue(store.Id, out var storeSubscriptionPlans);
                clientSubscriptionsByStore.TryGetValue(store.Id, out var storeClientSubscriptions);
                techTicketsByStore.TryGetValue(store.Id, out var storeTechTickets);
                paymentsByStore.TryGetValue(store.Id, out var storePaymentsRows);
                logsByStore.TryGetValue(store.Id, out var storeLogs);

                storeAppointments ??= [];
                storeUsers ??= [];
                storeServices ??= [];
                storeProfessionals ??= [];
                storeCustomers ??= [];
                storeSessions ??= [];
                storeSubscriptionPlans ??= [];
                storeClientSubscriptions ??= [];
                storeTechTickets ??= [];
                storePaymentsRows ??= [];
                storeLogs ??= [];

                var access = StoreAccessPolicy.Evaluate(store, now);
                var plan = PlanCatalog.Normalize(store.Plan);
                var expiry = store.ExpiresAt ?? store.SubscriptionExpiry;
                var overdue = !access.CanOperate && (access.Reason == "expired" || access.Reason == "subscription_expired");
                var isTrial = !overdue && plan == PlanCatalog.Starter && store.CreatedAt >= now.AddDays(-21);
                var commercialStatus = !store.IsActive
                    ? "inactive"
                    : store.IsSuspended
                        ? "blocked"
                        : overdue
                            ? "overdue"
                            : isTrial
                                ? "trial"
                                : "active";

                var profileCustomers = storeCustomers
                    .Select(c => string.IsNullOrWhiteSpace(c.CustomerKey) ? c.PhoneNumber : c.CustomerKey)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var appointmentCustomers = storeAppointments
                    .Select(a => a.PhoneNumber)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var customerCount = profileCustomers > 0 ? profileCustomers : appointmentCustomers;
                var customerSource = profileCustomers > 0 ? "crm" : "appointments_phone";

                var lastAppointment = storeAppointments.Count > 0 ? storeAppointments.Max(a => (DateTime?)a.DateTime) : null;
                var lastSession = storeSessions.Count > 0 ? storeSessions.Max(s => (DateTime?)s.LastInteraction) : null;
                var lastActivity = LatestDate(store.LastAccess, lastAppointment, lastSession);
                int? daysSinceActivity = lastActivity.HasValue ? Math.Max(0, (int)(now - lastActivity.Value).TotalDays) : null;
                var recentAppointments = storeAppointments.Count(a => a.DateTime >= recentFrom);
                var activeServices = storeServices.Count(s => s.Ativo);
                var activeProfessionals = storeProfessionals.Count(p => p.Ativo);
                var admins = storeUsers.Count(u => string.Equals(u.Role, "admin", StringComparison.OrdinalIgnoreCase));
                var staffUsers = storeUsers.Count(u => !string.Equals(u.Role, "superadmin", StringComparison.OrdinalIgnoreCase));
                var recurringRevenue = PlanCatalog.MonthlyPrice(plan);
                var subscriptionRevenue = storeClientSubscriptions
                    .Where(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    .Sum(s => (double)s.PlanPreco);

                var usageHealthScore = 0;
                if (access.CanOperate) usageHealthScore += 25;
                if (admins > 0) usageHealthScore += 10;
                if (activeServices > 0) usageHealthScore += 15;
                if (activeProfessionals > 0 || store.BusinessType != BusinessType.Barbershop) usageHealthScore += 10;
                if (customerCount > 0) usageHealthScore += 10;
                if (recentAppointments > 0 || storeTechTickets.Any(t => t.CreatedAt >= recentFrom)) usageHealthScore += 15;
                if (lastActivity.HasValue && lastActivity.Value >= staleFrom) usageHealthScore += 15;

                var riskReasons = new List<string>();
                if (!store.IsActive) riskReasons.Add("Loja inativa");
                if (store.IsSuspended) riskReasons.Add("Loja bloqueada");
                if (overdue) riskReasons.Add("Assinatura vencida");
                if (admins == 0) riskReasons.Add("Sem usuario admin");
                if (activeServices == 0) riskReasons.Add("Sem servicos ativos");
                if (store.BusinessType == BusinessType.Barbershop && activeProfessionals == 0) riskReasons.Add("Sem profissionais ativos");
                if (!lastActivity.HasValue) riskReasons.Add("Sem atividade registrada");
                else if (daysSinceActivity >= 30) riskReasons.Add($"Sem atividade ha {daysSinceActivity} dias");
                if (storeAppointments.Count == 0 && store.CreatedAt < now.AddDays(-7)) riskReasons.Add("Sem agendamentos apos criacao");

                var riskLevel = riskReasons.Any(r => r.Contains("vencida") || r.Contains("inativa") || r.Contains("bloqueada") || r.Contains("30"))
                    ? "high"
                    : riskReasons.Count >= 2
                        ? "medium"
                        : "low";

                var whatsappStatus = string.IsNullOrWhiteSpace(store.BotStatus) ? "unknown" : store.BotStatus.Trim().ToLowerInvariant();

                modulesByStore.TryGetValue(store.Id, out var storeModules);
                storeModules ??= [];

                return new
                {
                    store.Id,
                    StoreId = store.Id,
                    store.Name,
                    store.Slug,
                    Segment = store.BusinessType.ToString(),
                    SegmentLabel = BusinessTypeLabel(store.BusinessType),
                    Plan = plan,
                    CommercialStatus = commercialStatus,
                    AccessStatus = access.Reason,
                    AccessMessage = access.Message,
                    store.IsActive,
                    store.IsSuspended,
                    store.CreatedAt,
                    store.LastAccess,
                    store.ExpiresAt,
                    store.SubscriptionExpiry,
                    DaysUntilExpiry = expiry == default ? null : (int?)Math.Ceiling((expiry - now).TotalDays),
                    BackendUrl = string.IsNullOrWhiteSpace(store.BackendUrl) ? DefaultBackendUrl : store.BackendUrl,
                    BridgeUrl = string.IsNullOrWhiteSpace(store.BridgeUrl) ? BridgeUrlFor(store.Id) : store.BridgeUrl,
                    BridgePort = BridgePortFor(store.Id),
                    WhatsappStatus = whatsappStatus,
                    LastBotInteraction = lastSession,
                    TotalUsers = staffUsers,
                    AdminsCount = admins,
                    ProfessionalsCount = activeProfessionals,
                    ServicesCount = activeServices,
                    CustomersCount = customerCount,
                    CustomerSource = customerSource,
                    AppointmentsCount = storeAppointments.Count,
                    RecentAppointments = recentAppointments,
                    LastAppointment = lastAppointment,
                    RevenueTotal = storeAppointments.Sum(a => a.Price),
                    RevenueRecent = storeAppointments.Where(a => a.DateTime >= recentFrom).Sum(a => a.Price),
                    EstimatedMonthlyRevenue = (double)recurringRevenue,
                    ClientSubscriptionRevenue = subscriptionRevenue,
                    SubscriptionPlansCount = storeSubscriptionPlans.Count(p => p.Ativo),
                    ClientSubscriptionsCount = storeClientSubscriptions.Count,
                    ActiveClientSubscriptions = storeClientSubscriptions.Count(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase)),
                    StorePaymentsCount = storePaymentsRows.Count,
                    StorePaymentsTotal = storePaymentsRows.Where(p => p.Status == StorePaymentStatus.Paid.ToString()).Sum(p => (double)p.Amount),
                    LastStorePayment = storePaymentsRows.FirstOrDefault(p => p.Status == StorePaymentStatus.Paid.ToString())?.ConfirmedAt
                        ?? storePaymentsRows.FirstOrDefault(p => p.Status == StorePaymentStatus.Paid.ToString())?.CreatedAt,
                    TechTicketsCount = storeTechTickets.Count,
                    OpenTechTickets = storeTechTickets.Count(t => t.Status is not "Concluido" and not "Cancelado"),
                    LastActivity = lastActivity,
                    DaysSinceLastActivity = daysSinceActivity,
                    UsageHealthScore = Math.Min(100, usageHealthScore),
                    RiskLevel = riskLevel,
                    RiskReasons = riskReasons,
                    Modules = storeModules.Select(module => new
                    {
                        module.Key,
                        module.Name,
                        module.Description,
                        module.MinimumPlan,
                        module.Enabled,
                        module.IncludedInPlan,
                        module.Overridden,
                        module.SegmentSupported,
                        Source = module.Overridden ? "store_override" : "plan_catalog",
                        Writable = true,
                        Note = module.SegmentSupported
                            ? "Modulo calculado por plano e override opcional da loja."
                            : "Modulo nao aplicavel ao segmento desta loja."
                    }).ToList(),
                    RecentLogs = storeLogs.Select(l => new { l.Id, l.Timestamp, l.User, l.Action, l.Details }).ToList()
                };
            }).ToList();

            var alerts = new List<SuperAdminAlert>();
            foreach (var store in storeSummaries)
            {
                if (!store.IsActive || store.IsSuspended)
                    alerts.Add(new SuperAdminAlert("critical", store.StoreId, store.Name, "store_blocked", "Loja sem operacao", "A loja esta inativa ou bloqueada.", "Revisar status da loja"));

                if (store.CommercialStatus == "overdue")
                    alerts.Add(new SuperAdminAlert("critical", store.StoreId, store.Name, "payment_overdue", "Assinatura vencida", "A validade comercial expirou e pode bloquear a operacao.", "Regularizar plano"));

                if (store.ServicesCount == 0)
                    alerts.Add(new SuperAdminAlert("warning", store.StoreId, store.Name, "setup_services", "Sem servicos ativos", "A loja ainda nao tem servicos ativos para agendamento.", "Cadastrar servicos"));

                if (store.AdminsCount == 0)
                    alerts.Add(new SuperAdminAlert("warning", store.StoreId, store.Name, "setup_admin", "Sem admin operacional", "Nenhum usuario admin foi encontrado para esta loja.", "Criar acesso do dono"));

                if (store.WhatsappStatus is "unknown" or "disconnected" or "offline")
                    alerts.Add(new SuperAdminAlert("info", store.StoreId, store.Name, "whatsapp_pending", "WhatsApp sem estado confirmado", "O backend nao tem estado recente do WhatsApp; a dashboard usa a bridge em tempo real quando disponivel.", "Ver bridge WhatsApp"));

                if (store.DaysSinceLastActivity.HasValue && store.DaysSinceLastActivity.Value >= 30)
                    alerts.Add(new SuperAdminAlert("warning", store.StoreId, store.Name, "churn_risk", "Possivel abandono", $"Sem atividade registrada ha {store.DaysSinceLastActivity.Value} dias.", "Entrar em contato"));

                if (store.CommercialStatus == "trial" && store.DaysUntilExpiry.HasValue && store.DaysUntilExpiry.Value <= 7)
                    alerts.Add(new SuperAdminAlert("info", store.StoreId, store.Name, "trial_ending", "Teste perto do fim", $"Restam {store.DaysUntilExpiry.Value} dias na validade atual.", "Converter plano"));

                var pendingSubscriptions = store.ClientSubscriptionsCount - store.ActiveClientSubscriptions;
                if (pendingSubscriptions > 0 && clientSubscriptionsByStore.TryGetValue(store.StoreId, out var pendingRows))
                {
                    var pendingCount = pendingRows.Count(s => string.Equals(s.Status, "Pending", StringComparison.OrdinalIgnoreCase));
                    if (pendingCount > 0)
                    {
                        alerts.Add(new SuperAdminAlert("warning", store.StoreId, store.Name, "pix_subscription_pending", "Pagamento PIX pendente", $"{pendingCount} assinatura(s) aguardando confirmacao.", "Confirmar assinatura"));
                    }
                }
            }

            var segmentSummaries = stores
                .GroupBy(s => s.BusinessType)
                .Select(g =>
                {
                    var ids = g.Select(s => s.Id).ToHashSet();
                    var segmentAppointments = appointments.Where(a => ids.Contains(a.StoreId)).ToList();
                    var mostUsedServices = segmentAppointments
                        .GroupBy(a => ServiceNameFor(a.StoreId, a.ServiceId))
                        .Select(sg => new { Name = sg.Key, Count = sg.Count(), Revenue = sg.Sum(a => a.Price) })
                        .OrderByDescending(s => s.Count)
                        .Take(5)
                        .ToList();
                    return new
                    {
                        Segment = g.Key.ToString(),
                        Label = BusinessTypeLabel(g.Key),
                        TotalCompanies = g.Count(),
                        ActiveCompanies = g.Count(s => s.IsActive && !s.IsSuspended),
                        InactiveCompanies = g.Count(s => !s.IsActive || s.IsSuspended),
                        Appointments = segmentAppointments.Count,
                        Services = services.Count(s => ids.Contains(s.StoreId) && s.Ativo),
                        Customers = storeSummaries.Where(s => ids.Contains(s.StoreId)).Sum(s => s.CustomersCount),
                        ModulesMostUsed = moduleCatalog.Modules
                            .Where(m => g.Any(store => ModuleEnabledFor(store, m)))
                            .Select(m => m.Name)
                            .Take(4)
                            .ToList(),
                        MostUsedServices = mostUsedServices,
                        Problems = alerts
                            .Where(a => ids.Contains(a.StoreId) && a.Severity != "info")
                            .GroupBy(a => a.Type)
                            .Select(a => new { Type = a.Key, Count = a.Count() })
                            .OrderByDescending(a => a.Count)
                            .Take(4)
                            .ToList()
                    };
                })
                .OrderByDescending(s => s.TotalCompanies)
                .ToList();

            var totalRevenue = appointments.Sum(a => a.Price);
            var totalCustomersFromCrm = customers
                .Select(c => $"{c.StoreId}:{(string.IsNullOrWhiteSpace(c.CustomerKey) ? c.PhoneNumber : c.CustomerKey)}")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var totalCustomersFromAppointments = appointments
                .Select(a => $"{a.StoreId}:{a.PhoneNumber}")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var summary = new
            {
                TotalStores = stores.Count,
                ActiveStores = stores.Count(s => s.IsActive && !s.IsSuspended),
                InactiveStores = stores.Count(s => !s.IsActive),
                SuspendedStores = stores.Count(s => s.IsSuspended),
                TrialStores = storeSummaries.Count(s => s.CommercialStatus == "trial"),
                PayingStores = storeSummaries.Count(s => s.CommercialStatus == "active"),
                OverdueStores = storeSummaries.Count(s => s.CommercialStatus == "overdue"),
                TotalUsers = users.Count(u => !string.Equals(u.Role, "superadmin", StringComparison.OrdinalIgnoreCase)),
                TotalCustomers = totalCustomersFromCrm > 0 ? totalCustomersFromCrm : totalCustomersFromAppointments,
                CustomerSource = totalCustomersFromCrm > 0 ? "crm" : "appointments_phone",
                TotalAppointments = appointments.Count,
                RecentAppointments = appointments.Count(a => a.DateTime >= recentFrom),
                TotalServices = services.Count(s => s.Ativo),
                TotalProfessionals = professionals.Count(p => p.Ativo),
                TotalBotSessions = sessions.Count,
                WhatsappConnected = stores.Count(s => string.Equals(s.BotStatus, "connected", StringComparison.OrdinalIgnoreCase) || string.Equals(s.BotStatus, "online", StringComparison.OrdinalIgnoreCase)),
                WhatsappDisconnected = stores.Count(s => !string.Equals(s.BotStatus, "connected", StringComparison.OrdinalIgnoreCase) && !string.Equals(s.BotStatus, "online", StringComparison.OrdinalIgnoreCase)),
                ActiveSegments = segmentSummaries.Count,
                EstimatedMRR = stores.Where(s => s.IsActive && !s.IsSuspended).Sum(s => (double)PlanCatalog.MonthlyPrice(s.Plan)),
                StorePaymentRevenue = storePayments.Where(p => p.Status == StorePaymentStatus.Paid.ToString()).Sum(p => (double)p.Amount),
                RevenueTotal = totalRevenue,
                TicketAverage = appointments.Count > 0 ? totalRevenue / appointments.Count : 0,
                HealthScore = storeSummaries.Count > 0 ? Math.Round(storeSummaries.Average(s => s.UsageHealthScore), 1) : 0,
                GeneratedAt = DateTime.UtcNow
            };

            var dayRange = Enumerable.Range(0, 14)
                .Select(offset => today.AddDays(offset - 13))
                .ToList();

            var charts = new
            {
                CompaniesBySegment = segmentSummaries.Select(s => new { s.Label, s.TotalCompanies, s.ActiveCompanies }).ToList(),
                CompaniesByStatus = storeSummaries
                    .GroupBy(s => s.CommercialStatus)
                    .Select(g => new { Status = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .ToList(),
                AppointmentsByDay = dayRange.Select(day =>
                {
                    var rows = appointments.Where(a => a.DateTime.Date == day.Date).ToList();
                    return new { Date = day.ToString("yyyy-MM-dd"), Label = day.ToString("dd/MM"), Count = rows.Count, Revenue = rows.Sum(a => a.Price) };
                }).ToList(),
                CustomersByDay = dayRange.Select(day =>
                {
                    var rows = customers.Where(c => c.CreatedAt.Date == day.Date).ToList();
                    return new { Date = day.ToString("yyyy-MM-dd"), Label = day.ToString("dd/MM"), Count = rows.Count };
                }).ToList(),
                TopStoresByAppointments = storeSummaries
                    .OrderByDescending(s => s.RecentAppointments)
                    .ThenByDescending(s => s.AppointmentsCount)
                    .Take(8)
                    .Select(s => new { s.StoreId, s.Name, s.SegmentLabel, s.RecentAppointments, s.AppointmentsCount, s.UsageHealthScore })
                    .ToList(),
                TopServices = appointments
                    .GroupBy(a => ServiceNameFor(a.StoreId, a.ServiceId))
                    .Select(g => new { Name = g.Key, Count = g.Count(), Revenue = g.Sum(a => a.Price) })
                    .OrderByDescending(s => s.Count)
                    .Take(8)
                    .ToList(),
                WhatsappByStatus = storeSummaries
                    .GroupBy(s => s.WhatsappStatus)
                    .Select(g => new { Status = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .ToList()
            };

            var moduleMatrix = moduleCatalog.Modules.Select(module => new
            {
                module.Key,
                module.Name,
                module.Description,
                module.MinimumPlan,
                module.EnabledByDefault,
                EnabledStores = stores.Count(s => ModuleEnabledFor(s, module)),
                EligibleStores = stores.Count(s => ModuleEligibleFor(s, module)),
                Source = "plan_catalog_and_store_override",
                Writable = true,
                Note = "Visualizacao por plano, segmento e overrides persistentes por loja."
            }).ToList();

            var planSummaries = stores
                .GroupBy(s => PlanCatalog.Normalize(s.Plan))
                .Select(g => new
                {
                    Plan = g.Key,
                    Companies = g.Count(),
                    ActiveCompanies = g.Count(s => s.IsActive && !s.IsSuspended),
                    MonthlyValue = (double)PlanCatalog.MonthlyPrice(g.Key),
                    EstimatedMRR = (double)(PlanCatalog.MonthlyPrice(g.Key) * g.Count(s => s.IsActive && !s.IsSuspended))
                })
                .OrderByDescending(p => p.EstimatedMRR)
                .ToList();

            var subscriptionStats = new
            {
                Total = clientSubscriptions.Count,
                Pending = clientSubscriptions.Count(s => string.Equals(s.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
                Active = clientSubscriptions.Count(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase)),
                Expired = clientSubscriptions.Count(s => string.Equals(s.Status, "Expired", StringComparison.OrdinalIgnoreCase)),
                Cancelled = clientSubscriptions.Count(s => string.Equals(s.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)),
                ActiveRevenue = clientSubscriptions
                    .Where(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    .Sum(s => (double)s.PlanPreco),
                PendingPixAmount = clientSubscriptions
                    .Where(s => string.Equals(s.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                    .Sum(s => (double)s.PlanPreco)
            };

            var storeLookup = stores.ToDictionary(s => s.Id);
            var subscriptionPreview = clientSubscriptions
                .OrderByDescending(s => string.Equals(s.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.CreatedAt)
                .Take(30)
                .Select(s =>
                {
                    storeLookup.TryGetValue(s.StoreId, out var store);
                    return new
                    {
                        id = s.Id,
                        storeId = s.StoreId,
                        storeName = store?.Name ?? $"Loja #{s.StoreId}",
                        storeSlug = store?.Slug,
                        businessType = store?.BusinessType.ToString(),
                        segmentLabel = store == null ? "Sem loja" : BusinessTypeLabel(store.BusinessType),
                        isStoreActive = store?.IsActive ?? false,
                        clientPhone = s.ClientPhone,
                        clientName = s.ClientName,
                        planId = s.PlanId,
                        planNome = s.PlanNome,
                        planPreco = s.PlanPreco,
                        creditosTotal = s.CreditosTotal,
                        creditosUsados = s.CreditosUsados,
                        creditosRestantes = Math.Max(0, s.CreditosTotal - s.CreditosUsados),
                        status = s.Status,
                        createdAt = s.CreatedAt,
                        startDate = s.StartDate,
                        endDate = s.EndDate,
                        notes = s.Notes
                    };
                })
                .ToList();

            return Results.Ok(new
            {
                Summary = summary,
                Stores = storeSummaries,
                Segments = segmentSummaries,
                Alerts = alerts
                    .OrderByDescending(a => AlertWeight(a.Severity))
                    .ThenBy(a => a.StoreName)
                    .Take(40)
                    .ToList(),
                Charts = charts,
                Modules = moduleMatrix,
                Plans = planSummaries,
                Subscriptions = new
                {
                    Stats = subscriptionStats,
                    Items = subscriptionPreview,
                    PixFlow = "Confirmacao manual pelo Super Admin apos comprovante PIX; endpoint tenant-scoped das lojas permanece isolado."
                },
                RecentLogs = logs.Take(20),
                Fallbacks = new
                {
                    PaymentData = "PIX funciona como cobranca manual: a landing gera dados de pagamento e o Super Admin confirma a assinatura.",
                    WhatsappRealtime = "Estado em tempo real do WhatsApp e enriquecido pelo proxy Node via bridge-factory.",
                    TrialData = "Periodo de teste e inferido por plano Starter recente quando nao ha entidade comercial propria.",
                    ModuleToggles = "Catalogo de modulos usa plano, segmento e override persistente por loja."
                }
            });
        });

        app.MapGet("/api/superadmin/subscriptions", async (HttpContext ctx, AppDbContext db) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            await ExpireOverdueClientSubscriptions(db, ctx.RequestAborted);
            var payload = await BuildSuperSubscriptionsPayload(db, ctx.RequestAborted);
            return Results.Ok(payload);
        });

        app.MapPost("/api/superadmin/subscriptions/{id:int}/activate", async (HttpContext ctx, AppDbContext db, int id, SuperSubscriptionActionRequest? req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var sub = await db.ClientSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (sub == null) return Results.NotFound(new { error = "Assinatura nao encontrada." });

            var plan = await db.SubscriptionPlans.IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == sub.PlanId && p.StoreId == sub.StoreId, ctx.RequestAborted);
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sub.StoreId, ctx.RequestAborted);

            var nowActivation = DateTime.Now;
            sub.Status = SubscriptionStatus.Active;
            sub.StartDate = nowActivation;
            sub.EndDate = nowActivation.AddDays(plan?.DuracaoDias ?? 30);
            sub.Notes = AppendAdminNote(sub.Notes, req?.Notes, "Pagamento PIX confirmado pelo Super Admin.");

            await db.SaveChangesAsync(ctx.RequestAborted);
            await LogAction(db, "Superadmin", "ACTIVATE_CLIENT_SUBSCRIPTION", $"Assinatura #{id} ativada para loja #{sub.StoreId}");

            return Results.Ok(ToSuperSubscriptionDto(sub, store, plan));
        });

        app.MapPost("/api/superadmin/subscriptions/{id:int}/cancel", async (HttpContext ctx, AppDbContext db, int id, SuperSubscriptionActionRequest? req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var sub = await db.ClientSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (sub == null) return Results.NotFound(new { error = "Assinatura nao encontrada." });

            var plan = await db.SubscriptionPlans.IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == sub.PlanId && p.StoreId == sub.StoreId, ctx.RequestAborted);
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sub.StoreId, ctx.RequestAborted);

            sub.Status = SubscriptionStatus.Cancelled;
            sub.Notes = AppendAdminNote(sub.Notes, req?.Notes, "Assinatura cancelada pelo Super Admin.");

            await db.SaveChangesAsync(ctx.RequestAborted);
            await LogAction(db, "Superadmin", "CANCEL_CLIENT_SUBSCRIPTION", $"Assinatura #{id} cancelada para loja #{sub.StoreId}");

            return Results.Ok(ToSuperSubscriptionDto(sub, store, plan));
        });

        app.MapGet("/api/superadmin/stores/{id}/users", async (HttpContext ctx, AppDbContext db, int id) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == id);
            if (store == null) return Results.NotFound();

            var admins = await db.Users.AsNoTracking()
                .Where(u => u.StoreId == id && u.Role == "admin")
                .Select(u => new { u.Id, u.Username, u.Role, u.PhoneNumber, u.Is2FAEnabled })
                .OrderBy(u => u.Username)
                .ToListAsync();

            var professionals = await db.Users.AsNoTracking()
                .Where(u => u.StoreId == id && u.Role != "admin" && u.Role != "superadmin")
                .Select(u => new { u.Id, u.Username, u.Role, u.PhoneNumber, u.Is2FAEnabled })
                .OrderBy(u => u.Username)
                .ToListAsync();

            return Results.Ok(new { storeId = id, storeName = store.Name, admins, professionals });
        });

        app.MapPost("/api/superadmin/stores/{id}/admins", async (HttpContext ctx, AppDbContext db, AuthService auth, int id, AdminAccountRequest req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (store == null) return Results.NotFound(new { error = "Loja nao encontrada." });

            var username = req.Username?.Trim() ?? "";
            var password = req.Password ?? "";
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return Results.BadRequest(new { error = "Usuario e senha sao obrigatorios." });

            if (password.Length < 4)
                return Results.BadRequest(new { error = "A senha deve ter pelo menos 4 caracteres." });

            var usernameKey = username.ToLowerInvariant();
            var exists = await db.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.Username.ToLower() == usernameKey, ctx.RequestAborted);
            if (exists) return Results.Conflict(new { error = "Ja existe um usuario com este nome." });

            var user = new User
            {
                Username = username,
                PasswordHash = auth.HashPassword(password),
                Role = "admin",
                StoreId = id,
                PhoneNumber = string.IsNullOrWhiteSpace(req.PhoneNumber) ? null : req.PhoneNumber.Trim()
            };

            db.Users.Add(user);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await LogAction(db, "Superadmin", "CREATE_STORE_ADMIN", $"Admin: {user.Username} (UserId: {user.Id}, StoreId: {id})");

            return Results.Created($"/api/superadmin/stores/{id}/users", ToUserDto(user, store.Name));
        });

        app.MapPatch("/api/superadmin/users/{userId}", async (HttpContext ctx, AppDbContext db, AuthService auth, int userId, AdminAccountUpdateRequest req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ctx.RequestAborted);
            if (user == null) return Results.NotFound(new { error = "Usuario nao encontrado." });
            if (string.Equals(user.Role, "superadmin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Conta superadmin nao pode ser alterada por esta tela." });

            var originalRole = user.Role;
            if (!string.IsNullOrWhiteSpace(req.Username))
            {
                var username = req.Username.Trim();
                var usernameKey = username.ToLowerInvariant();
                var exists = await db.Users.IgnoreQueryFilters()
                    .AnyAsync(u => u.Id != userId && u.Username.ToLower() == usernameKey, ctx.RequestAborted);
                if (exists) return Results.Conflict(new { error = "Ja existe outro usuario com este nome." });
                user.Username = username;
            }

            if (!string.IsNullOrWhiteSpace(req.PhoneNumber))
                user.PhoneNumber = req.PhoneNumber.Trim();
            else if (req.PhoneNumber != null)
                user.PhoneNumber = null;

            if (!string.IsNullOrWhiteSpace(req.Password))
            {
                if (req.Password.Length < 4)
                    return Results.BadRequest(new { error = "A senha deve ter pelo menos 4 caracteres." });
                user.PasswordHash = auth.HashPassword(req.Password);
            }

            if (!string.IsNullOrWhiteSpace(req.Role))
            {
                var role = NormalizeManagedUserRole(req.Role);
                if (role == null) return Results.BadRequest(new { error = "Perfil invalido para gestao pelo Super Admin." });
                user.Role = role;
            }

            if (string.Equals(originalRole, "admin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var remainingAdmins = await db.Users.IgnoreQueryFilters()
                    .CountAsync(u => u.StoreId == user.StoreId && u.Id != user.Id && u.Role == "admin", ctx.RequestAborted);
                if (remainingAdmins == 0)
                    return Results.BadRequest(new { error = "A loja precisa manter pelo menos um administrador." });
            }

            await db.SaveChangesAsync(ctx.RequestAborted);
            var storeName = await db.Stores.AsNoTracking()
                .Where(s => s.Id == user.StoreId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(ctx.RequestAborted);
            await LogAction(db, "Superadmin", "UPDATE_STORE_ADMIN", $"UserId: {user.Id}, StoreId: {user.StoreId}");

            return Results.Ok(ToUserDto(user, storeName));
        });

        app.MapDelete("/api/superadmin/users/{userId}", async (HttpContext ctx, AppDbContext db, int userId) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ctx.RequestAborted);
            if (user == null) return Results.NotFound(new { error = "Usuario nao encontrado." });
            if (string.Equals(user.Role, "superadmin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Conta superadmin nao pode ser removida por esta tela." });

            if (string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var remainingAdmins = await db.Users.IgnoreQueryFilters()
                    .CountAsync(u => u.StoreId == user.StoreId && u.Id != user.Id && u.Role == "admin", ctx.RequestAborted);
                if (remainingAdmins == 0)
                    return Results.BadRequest(new { error = "A loja precisa manter pelo menos um administrador." });
            }

            var removed = new { user.Id, user.Username, user.Role, user.StoreId };
            db.Users.Remove(user);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await LogAction(db, "Superadmin", "DELETE_STORE_ADMIN", $"UserId: {removed.Id}, StoreId: {removed.StoreId}");

            return Results.Ok(new { deleted = removed.Id, user = removed });
        });

        app.MapGet("/api/superadmin/stores", async (HttpContext ctx, AppDbContext db) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0; // Superadmin vê tudo

            var hoje = DateTime.Today;
            var amanha = hoje.AddDays(1);

            // Carrega stores base
            var stores = await db.Stores
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .ThenBy(s => s.Id)
                .ToListAsync();

            var storeIds = stores.Select(s => s.Id).ToList();

            // Contagens de usuários por loja (query única em memória)
            var userCounts = await db.Users.AsNoTracking()
                .Where(u => storeIds.Contains(u.StoreId))
                .GroupBy(u => new { u.StoreId, u.Role })
                .Select(g => new { g.Key.StoreId, g.Key.Role, Count = g.Count() })
                .ToListAsync();

            // Agregados de agendamentos por loja
            var apptData = await db.Appointments.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(a => storeIds.Contains(a.StoreId))
                .Select(a => new { a.StoreId, a.DateTime, Preco = (double)a.Preco })
                .ToListAsync();

            var apptByStore = apptData
                .GroupBy(a => a.StoreId)
                .ToDictionary(g => g.Key, g => new
                {
                    Total = g.Count(),
                    RevenueToday = g.Where(a => a.DateTime >= hoje && a.DateTime < amanha).Sum(a => a.Preco),
                    RevenueTotal = g.Sum(a => a.Preco),
                    LastAppt = g.Max(a => (DateTime?)a.DateTime)
                });

            var result = stores.Select(s =>
            {
                apptByStore.TryGetValue(s.Id, out var ac);
                var admins = userCounts.FirstOrDefault(u => u.StoreId == s.Id && u.Role == "admin")?.Count ?? 0;
                var profs = userCounts.FirstOrDefault(u => u.StoreId == s.Id && u.Role == "barbeiro")?.Count ?? 0;
                return (object)new
                {
                    s.Id,
                    StoreId = s.Id,
                    s.Name,
                    s.Slug,
                    Plan = PlanCatalog.Normalize(s.Plan),
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.SubscriptionExpiry,
                    s.IsActive,
                    s.IsSuspended,
                    CommercialStatus = StoreAccessPolicy.Evaluate(s).Reason,
                    s.BusinessType,
                    s.ApiKey,
                    BackendUrl = string.IsNullOrWhiteSpace(s.BackendUrl) ? DefaultBackendUrl : s.BackendUrl,
                    BridgeUrl = string.IsNullOrWhiteSpace(s.BridgeUrl) ? BridgeUrlFor(s.Id) : s.BridgeUrl,
                    BridgePort = BridgePortFor(s.Id),
                    ExpectedBridgeUrl = BridgeUrlFor(s.Id),
                    AdminsCount = admins,
                    ProfessionalsCount = profs,
                    AppointmentsCount = ac?.Total ?? 0,
                    RevenueToday = ac?.RevenueToday ?? 0,
                    RevenueTotal = ac?.RevenueTotal ?? 0,
                    LastAppointment = ac?.LastAppt
                };
            }).ToList();

            return Results.Ok(result);
        });

        app.MapGet("/api/superadmin/stores/{id}", async (HttpContext ctx, AppDbContext db, int id) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FindAsync(id);
            return store == null ? Results.NotFound() : Results.Ok(ToStoreDto(store));
        });

        // 4.7: Export isolado dos dados de UMA loja (backup/migração individual).
        // Retorna JSON com agendamentos, profissionais, serviços e usuários (sem hashes de senha).
        app.MapGet("/api/superadmin/stores/{id}/export", async (HttpContext ctx, AppDbContext db, int id) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FindAsync(id);
            if (store == null) return Results.NotFound(new { error = "Loja não encontrada" });

            var appointments = await db.Appointments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.StoreId == id)
                .OrderBy(a => a.DateTime)
                .Select(a => new
                {
                    a.Id, a.PhoneNumber, a.ContactName, a.DateTime, ServiceId = a.ServiceId,
                    a.BarberId, a.BarberName, a.DuracaoMinutos, a.Preco,
                    a.PresencaConfirmada, a.Notes, a.CreatedAt
                })
                .ToListAsync();

            var barbeiros = await db.Barbeiros.IgnoreQueryFilters().AsNoTracking()
                .Where(b => b.StoreId == id)
                .Select(b => new { b.Id, b.Nome, b.Ativo, b.Especialidade, b.Cor, b.WorkingDays })
                .ToListAsync();

            var servicos = await db.Servicos.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.StoreId == id)
                .Select(s => new { s.Id, s.Nome, s.DuracaoMinutos, s.Preco, s.Ativo, s.Ordem })
                .ToListAsync();

            var users = await db.Users.IgnoreQueryFilters().AsNoTracking()
                .Where(u => u.StoreId == id)
                .Select(u => new { u.Id, u.Username, u.Role, u.PhoneNumber, u.Is2FAEnabled })
                .ToListAsync();

            var export = new
            {
                exportedAt = DateTime.UtcNow,
                store = new { store.Id, store.Name, store.Slug, store.Plan, store.BusinessType, store.BridgeUrl, store.BackendUrl },
                counts = new { appointments = appointments.Count, barbeiros = barbeiros.Count, servicos = servicos.Count, users = users.Count },
                appointments,
                barbeiros,
                servicos,
                users
            };

            var fileName = $"export-{store.Slug}-{DateTime.Now:yyyyMMdd_HHmm}.json";
            ctx.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{fileName}\"");
            await LogAction(db, "Superadmin", "EXPORT_STORE", $"Store: {store.Name} (ID: {id})");
            return Results.Json(export);
        });

        app.MapPost("/api/superadmin/stores", async (HttpContext ctx, AppDbContext db, AuthService auth, CreateStoreRequest req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Slug) ||
                string.IsNullOrWhiteSpace(req.AdminUsername) || string.IsNullOrWhiteSpace(req.AdminPassword))
            {
                return Results.BadRequest(new { error = "Nome, Slug, Usuario Admin e Senha Admin sao obrigatorios." });
            }

            var slug = req.Slug.Trim().ToLowerInvariant();
            if (await db.Stores.AnyAsync(s => s.Slug == slug))
                return Results.Conflict(new { error = "Ja existe uma loja com este Slug." });

            if (string.IsNullOrWhiteSpace(req.BusinessType) ||
                !Enum.TryParse<BusinessType>(req.BusinessType, ignoreCase: true, out var businessType))
            {
                return Results.BadRequest(new { error = $"Tipo de negocio invalido. Valores aceitos: {string.Join(", ", Enum.GetNames<BusinessType>())}" });
            }

            var newStore = new Store
            {
                Name = req.Name.Trim(),
                Slug = slug,
                Plan = PlanCatalog.Normalize(req.Plan),
                IsActive = true,
                BusinessType = businessType,
                ApiKey = string.IsNullOrWhiteSpace(req.ApiKey) ? Guid.NewGuid().ToString() : req.ApiKey.Trim(),
                BackendUrl = string.IsNullOrWhiteSpace(req.BackendUrl) ? null : req.BackendUrl.Trim(),
                BridgeUrl = string.IsNullOrWhiteSpace(req.BridgeUrl) ? null : req.BridgeUrl.Trim()
            };

            if (!string.IsNullOrWhiteSpace(newStore.BridgeUrl) &&
                await db.Stores.AnyAsync(s => s.BridgeUrl == newStore.BridgeUrl))
            {
                return Results.Conflict(new { error = "Ja existe uma loja usando esta BridgeUrl." });
            }

            // BridgeUrl é definida automaticamente após SaveChanges quando o Id é gerado
            // (porta = 3000 + storeId, conforme padrão da bridge-factory)

            db.Stores.Add(newStore);
            await db.SaveChangesAsync(); // Gera o Id

            // Auto-define BridgeUrl com porta calculada pela factory (3000 + storeId)
            if (string.IsNullOrWhiteSpace(newStore.BridgeUrl))
            {
                newStore.BridgeUrl = BridgeUrlFor(newStore.Id);
                await db.SaveChangesAsync();
            }

            db.Users.Add(new User
            {
                Username = req.AdminUsername,
                PasswordHash = auth.HashPassword(req.AdminPassword),
                Role = "admin",
                StoreId = newStore.Id
            });

            db.Set<ServicoItem>().AddRange(BuildDefaultServicos(newStore.Id, businessType));

            await db.SaveChangesAsync();

            await LogAction(db, "Superadmin", "CREATE_STORE", $"Store: {newStore.Name} (ID: {newStore.Id}, Tipo: {businessType})");
            return Results.Created($"/api/superadmin/stores/{newStore.Id}", ToStoreDto(newStore, includeProvisioning: true));
        });

        app.MapMethods("/api/superadmin/stores/{id}", new[] { "PUT", "PATCH" }, async (HttpContext ctx, AppDbContext db, int id, UpdateStoreRequest req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FindAsync(id);
            if (store == null) return Results.NotFound();

            if (!string.IsNullOrWhiteSpace(req.Name)) store.Name = req.Name;
            if (!string.IsNullOrWhiteSpace(req.Slug))
            {
                var newSlug = req.Slug.Trim().ToLowerInvariant();
                if (await db.Stores.AnyAsync(s => s.Slug == newSlug && s.Id != id))
                    return Results.Conflict(new { error = "Ja existe outra loja com este Slug." });
                store.Slug = newSlug;
            }
            if (!string.IsNullOrWhiteSpace(req.Plan)) store.Plan = PlanCatalog.Normalize(req.Plan);
            if (req.IsActive.HasValue) store.IsActive = req.IsActive.Value;
            if (req.IsSuspended.HasValue) store.IsSuspended = req.IsSuspended.Value;
            if (req.ExpiresAt.HasValue) store.ExpiresAt = req.ExpiresAt.Value;
            if (req.SubscriptionExpiry.HasValue) store.SubscriptionExpiry = req.SubscriptionExpiry.Value;
            if (!string.IsNullOrWhiteSpace(req.ApiKey)) store.ApiKey = req.ApiKey;
            if (!string.IsNullOrWhiteSpace(req.BackendUrl)) store.BackendUrl = req.BackendUrl.Trim();
            if (!string.IsNullOrWhiteSpace(req.BridgeUrl))
            {
                var requestedBridge = req.BridgeUrl.Trim();
                if (await db.Stores.AnyAsync(s => s.Id != id && s.BridgeUrl == requestedBridge))
                    return Results.Conflict(new { error = "Ja existe outra loja usando esta BridgeUrl." });
                store.BridgeUrl = requestedBridge;
            }
            if (!string.IsNullOrWhiteSpace(req.BusinessType))
            {
                if (!Enum.TryParse<BusinessType>(req.BusinessType, ignoreCase: true, out var updatedType))
                    return Results.BadRequest(new { error = $"Tipo de negocio invalido. Valores aceitos: {string.Join(", ", Enum.GetNames<BusinessType>())}" });

                if (store.BusinessType != updatedType)
                {
                    store.BusinessType = updatedType;
                    // Resetar serviços se não há agendamentos futuros ligados a eles
                    var hasFutureAppointments = db.Appointments
                        .IgnoreQueryFilters()
                        .Any(a => a.StoreId == id && a.DateTime >= DateTime.Now);

                    if (!hasFutureAppointments)
                    {
                        var oldServicos = db.Set<ServicoItem>().IgnoreQueryFilters()
                            .Where(s => s.StoreId == id).ToList();
                        db.Set<ServicoItem>().RemoveRange(oldServicos);
                        db.Set<ServicoItem>().AddRange(BuildDefaultServicos(id, updatedType));
                    }
                }
            }

            await db.SaveChangesAsync();
            await LogAction(db, "Superadmin", "UPDATE_STORE", $"Store: {store.Name} (ID: {store.Id})");
            return Results.Ok(ToStoreDto(store, includeProvisioning: true));
        });

        app.MapPost("/api/superadmin/stores/{id}/subscription/mark-paid", async (HttpContext ctx, AppDbContext db, int id, StoreSubscriptionPaymentRequest req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FindAsync(new object?[] { id }, ctx.RequestAborted);
            if (store == null) return Results.NotFound(new { error = "Loja nao encontrada." });

            if (!string.IsNullOrWhiteSpace(req.Plan))
                store.Plan = PlanCatalog.Normalize(req.Plan);

            var now = DateTime.Now;
            var baseDate = LatestDate(store.ExpiresAt, store.SubscriptionExpiry) ?? now;
            if (baseDate < now) baseDate = now;

            var months = Math.Clamp(req.Months.GetValueOrDefault(), 0, 24);
            var days = Math.Clamp(req.Days.GetValueOrDefault(), 0, 730);
            DateTime paidUntil;

            if (months > 0)
                paidUntil = baseDate.AddMonths(months);
            else
                paidUntil = baseDate.AddDays(days > 0 ? days : 30);

            store.ExpiresAt = paidUntil;
            store.SubscriptionExpiry = paidUntil;

            if (req.ActivateStore.GetValueOrDefault(true))
            {
                store.IsActive = true;
                store.IsSuspended = false;
            }

            var paymentMode = NormalizePaymentMode(req.PaymentMode);
            var periodMultiplier = months > 0 ? months : Math.Max(1m, (days > 0 ? days : 30) / 30m);
            var amount = req.Amount.GetValueOrDefault(PlanCatalog.MonthlyPrice(store.Plan) * periodMultiplier);
            var payment = new StorePaymentRecord
            {
                StoreId = store.Id,
                Plan = PlanCatalog.Normalize(store.Plan),
                Amount = amount < 0 ? 0 : amount,
                PaymentMode = paymentMode,
                Provider = paymentMode == "automatic_pix" ? "pix_gateway" : "manual",
                ProviderReference = string.IsNullOrWhiteSpace(req.ProviderReference) ? null : req.ProviderReference.Trim(),
                Status = StorePaymentStatus.Paid,
                PaidUntil = paidUntil,
                CreatedAt = now,
                ConfirmedAt = now,
                ConfirmedBy = "Superadmin",
                Notes = req.Notes
            };
            db.StorePaymentRecords.Add(payment);
            await db.SaveChangesAsync(ctx.RequestAborted);
            await LogAction(
                db,
                "Superadmin",
                "MARK_STORE_SUBSCRIPTION_PAID",
                $"Store: {store.Name} (ID: {store.Id}), Plano: {store.Plan}, Validade: {paidUntil:yyyy-MM-dd}, Modo: {paymentMode}, Obs: {req.Notes ?? ""}");

            return Results.Ok(new
            {
                store = ToStoreDto(store, includeProvisioning: true),
                payment = ToStorePaymentDto(payment, store.Name),
                paidUntil,
                paymentMode,
                amount,
                monthsAdded = months,
                daysAdded = months > 0 ? null : (int?)(days > 0 ? days : 30),
                message = "Assinatura da loja marcada como paga e validade atualizada."
            });
        });

        app.MapGet("/api/superadmin/stores/{id}/payments", async (HttpContext ctx, AppDbContext db, int id) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (store == null) return Results.NotFound(new { error = "Loja nao encontrada." });

            var payments = await db.StorePaymentRecords.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.StoreId == id)
                .OrderByDescending(p => p.CreatedAt)
                .Take(100)
                .ToListAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                store = ToStoreDto(store),
                summary = new
                {
                    total = payments.Count,
                    paid = payments.Count(p => p.Status == StorePaymentStatus.Paid),
                    revenue = payments.Where(p => p.Status == StorePaymentStatus.Paid).Sum(p => p.Amount),
                    lastPaymentAt = payments.FirstOrDefault(p => p.Status == StorePaymentStatus.Paid)?.ConfirmedAt
                        ?? payments.FirstOrDefault(p => p.Status == StorePaymentStatus.Paid)?.CreatedAt
                },
                items = payments.Select(p => ToStorePaymentDto(p, store.Name)).ToList()
            });
        });

        app.MapPatch("/api/superadmin/stores/{id}/modules/{moduleKey}", async (
            HttpContext ctx,
            AppDbContext db,
            FeatureAccessService featureAccess,
            int id,
            string moduleKey,
            StoreModuleOverrideRequest req) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (store == null) return Results.NotFound(new { error = "Loja nao encontrada." });

            var module = await featureAccess.SetModuleOverrideAsync(id, moduleKey, req.Enabled, ctx.RequestAborted);
            if (module == null) return Results.NotFound(new { error = "Modulo nao encontrado." });

            var action = req.Enabled.HasValue
                ? (req.Enabled.Value ? "ENABLE_STORE_MODULE" : "DISABLE_STORE_MODULE")
                : "RESET_STORE_MODULE_OVERRIDE";
            await LogAction(db, "Superadmin", action, $"Store: {store.Name} (ID: {id}), Module: {module.Key}");

            return Results.Ok(new
            {
                storeId = id,
                module,
                message = req.Enabled.HasValue
                    ? "Modulo da loja atualizado."
                    : "Override do modulo removido; a loja voltou a seguir plano e segmento."
            });
        });

        app.MapDelete("/api/superadmin/stores/{id}", async (HttpContext ctx, AppDbContext db, int id) =>
        {
            if (!IsSuperAdmin(ctx, apiKey)) return Forbidden();
            db.TenantId = 0;

            var store = await db.Stores.FindAsync(id);
            if (store == null) return Results.NotFound();

            var removed = new
            {
                optimizationEvents = await db.OptimizationTicketEvents.IgnoreQueryFilters().Where(e => e.StoreId == id).ExecuteDeleteAsync(),
                optimizationTickets = await db.OptimizationTickets.IgnoreQueryFilters().Where(t => t.StoreId == id).ExecuteDeleteAsync(),
                optimizationDevices = await db.OptimizationDevices.IgnoreQueryFilters().Where(d => d.StoreId == id).ExecuteDeleteAsync(),
                customerReminders = await db.CustomerReminders.IgnoreQueryFilters().Where(r => r.StoreId == id).ExecuteDeleteAsync(),
                customerEvents = await db.CustomerEvents.IgnoreQueryFilters().Where(e => e.StoreId == id).ExecuteDeleteAsync(),
                customerTagAssignments = await db.CustomerTagAssignments.IgnoreQueryFilters().Where(a => a.StoreId == id).ExecuteDeleteAsync(),
                customerTags = await db.CustomerTags.IgnoreQueryFilters().Where(t => t.StoreId == id).ExecuteDeleteAsync(),
                customerProfiles = await db.CustomerProfiles.IgnoreQueryFilters().Where(p => p.StoreId == id).ExecuteDeleteAsync(),
                clientVehicles = await db.ClientVehicles.IgnoreQueryFilters().Where(v => v.StoreId == id).ExecuteDeleteAsync(),
                appointments = await db.Appointments.IgnoreQueryFilters().Where(a => a.StoreId == id).ExecuteDeleteAsync(),
                conversationSessions = await db.ConversationSessions.IgnoreQueryFilters().Where(cs => cs.StoreId == id).ExecuteDeleteAsync(),
                unavailableDays = await db.UnavailableDays.IgnoreQueryFilters().Where(d => d.StoreId == id).ExecuteDeleteAsync(),
                barbeiroHorarios = await db.BarbeiroHorarios.IgnoreQueryFilters().Where(h => h.StoreId == id).ExecuteDeleteAsync(),
                barbeiros = await db.Barbeiros.IgnoreQueryFilters().Where(b => b.StoreId == id).ExecuteDeleteAsync(),
                servicos = await db.Servicos.IgnoreQueryFilters().Where(s => s.StoreId == id).ExecuteDeleteAsync(),
                subscriptionPlans = await db.SubscriptionPlans.IgnoreQueryFilters().Where(p => p.StoreId == id).ExecuteDeleteAsync(),
                clientSubscriptions = await db.ClientSubscriptions.IgnoreQueryFilters().Where(s => s.StoreId == id).ExecuteDeleteAsync(),
                storePayments = await db.StorePaymentRecords.IgnoreQueryFilters().Where(p => p.StoreId == id).ExecuteDeleteAsync(),
                users = await db.Users.IgnoreQueryFilters().Where(u => u.StoreId == id).ExecuteDeleteAsync(),
                auditLogs = await db.AuditLogs.IgnoreQueryFilters().Where(a => a.StoreId == id).ExecuteDeleteAsync()
            };

            db.Stores.Remove(store);
            await db.SaveChangesAsync();

            await LogAction(db, "Superadmin", "DELETE_STORE", $"Store: {store.Name} (ID: {store.Id})");
            return Results.Ok(new
            {
                deleted = id,
                storeId = id,
                storeName = store.Name,
                bridgePort = BridgePortFor(id),
                bridgeUrl = string.IsNullOrWhiteSpace(store.BridgeUrl) ? BridgeUrlFor(id) : store.BridgeUrl,
                removed,
                message = "Loja excluida com todos os dados associados."
            });
        });

        return app;
    }

    private static bool IsSuperAdmin(HttpContext ctx, string apiKey)
    {
        var hasInternalKey = ctx.Request.Headers["X-API-KEY"] == apiKey && EndpointAuth.IsLoopback(ctx);
        var isSuper = ctx.User.FindFirst(ClaimTypes.Role)?.Value == "superadmin"
            || ctx.User.FindFirst("role")?.Value == "superadmin";
        return hasInternalKey || isSuper;
    }

    private static IResult Forbidden() =>
        Results.Json(new { error = "Acesso restrito ao Superadmin" }, statusCode: 403);

    private static DateTime? LatestDate(params DateTime?[] values)
    {
        var valid = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return valid.Count == 0 ? null : valid.Max();
    }

    private static string BusinessTypeLabel(BusinessType type) =>
        type switch
        {
            BusinessType.CarWash => "Lavajato",
            BusinessType.Pizzeria => "Pizzaria",
            BusinessType.ComputerOptimization => "Tecnologia",
            _ => "Barbearia"
        };

    private static int AlertWeight(string severity) =>
        severity switch
        {
            "critical" => 4,
            "warning" => 3,
            "info" => 2,
            _ => 1
        };

    private record SuperAdminAlert(
        string Severity,
        int StoreId,
        string StoreName,
        string Type,
        string Title,
        string Message,
        string Action);

    internal static List<ServicoItem> BuildDefaultServicos(int storeId, BusinessType type) =>
        type switch
        {
            BusinessType.CarWash => new List<ServicoItem>
            {
                new() { StoreId = storeId, Nome = "Lavagem Simples",      DuracaoMinutos = 40,  Preco = 40m,  Ativo = true, Ordem = 1 },
                new() { StoreId = storeId, Nome = "Veiculos Maiores",     DuracaoMinutos = 50,  Preco = 50m,  Ativo = true, Ordem = 2 },
                new() { StoreId = storeId, Nome = "Lavagem de Moto",      DuracaoMinutos = 25,  Preco = 30m,  Ativo = true, Ordem = 3 },
                new() { StoreId = storeId, Nome = "Lavagem de Caminhao",  DuracaoMinutos = 120, Preco = 500m, Ativo = true, Ordem = 4 },
                new() { StoreId = storeId, Nome = "Polimento Comercial",  DuracaoMinutos = 180, Preco = 120m, Ativo = true, Ordem = 5, OcupaHorario = false },
                new() { StoreId = storeId, Nome = "Polimento Tecnico",    DuracaoMinutos = 240, Preco = 220m, Ativo = true, Ordem = 6, OcupaHorario = false },
            },
            BusinessType.Pizzeria => new List<ServicoItem>
            {
                new() { StoreId = storeId, Nome = "Pizza Calabresa",              DuracaoMinutos = 45, Preco = 45m, Ativo = true, Ordem = 1 },
                new() { StoreId = storeId, Nome = "Pizza Mussarela",              DuracaoMinutos = 45, Preco = 42m, Ativo = true, Ordem = 2 },
                new() { StoreId = storeId, Nome = "Pizza Portuguesa",             DuracaoMinutos = 50, Preco = 55m, Ativo = true, Ordem = 3 },
                new() { StoreId = storeId, Nome = "Pizza Frango com Catupiry",    DuracaoMinutos = 50, Preco = 58m, Ativo = true, Ordem = 4 },
                new() { StoreId = storeId, Nome = "Pizza Quatro Queijos",         DuracaoMinutos = 50, Preco = 60m, Ativo = true, Ordem = 5 },
                new() { StoreId = storeId, Nome = "Combo Pizza + Refrigerante",   DuracaoMinutos = 55, Preco = 75m, Ativo = true, Ordem = 6 },
            },
            BusinessType.ComputerOptimization => new List<ServicoItem>
            {
                new() { StoreId = storeId, Nome = "Otimizacao de console", DuracaoMinutos = 30, Preco = 20m,  Ativo = true, Ordem = 1 },
                new() { StoreId = storeId, Nome = "Otimizacao pro",        DuracaoMinutos = 45, Preco = 40m,  Ativo = true, Ordem = 2 },
                new() { StoreId = storeId, Nome = "Otimizacao elite",      DuracaoMinutos = 60, Preco = 60m,  Ativo = true, Ordem = 3 },
                new() { StoreId = storeId, Nome = "Otimizacao Premium",    DuracaoMinutos = 90, Preco = 100m, Ativo = true, Ordem = 4 },
            },
            _ => new List<ServicoItem>
            {
                new() { StoreId = storeId, Nome = "Corte",                        DuracaoMinutos = 30, Preco = 30m,  Ativo = true, Ordem = 1 },
                new() { StoreId = storeId, Nome = "Barba",                         DuracaoMinutos = 20, Preco = 30m,  Ativo = true, Ordem = 2 },
                new() { StoreId = storeId, Nome = "Sobrancelha",                   DuracaoMinutos = 15, Preco = 15m,  Ativo = true, Ordem = 3 },
                new() { StoreId = storeId, Nome = "Corte + Barba",                 DuracaoMinutos = 50, Preco = 60m,  Ativo = true, Ordem = 4 },
                new() { StoreId = storeId, Nome = "Corte + Sobrancelha",           DuracaoMinutos = 45, Preco = 45m,  Ativo = true, Ordem = 5 },
                new() { StoreId = storeId, Nome = "Corte + Barba + Sobrancelha",   DuracaoMinutos = 65, Preco = 75m,  Ativo = true, Ordem = 6 },
            },
        };

    private static int BridgePortFor(int storeId) => BridgeBasePort + storeId;

    private static string BridgeUrlFor(int storeId) => $"http://127.0.0.1:{BridgePortFor(storeId)}";

    private static object ToStoreDto(Store store, bool includeProvisioning = false) => new
    {
        store.Id,
        StoreId = store.Id,
        store.Name,
        store.Slug,
        Plan = PlanCatalog.Normalize(store.Plan),
        store.CreatedAt,
        store.ExpiresAt,
        store.SubscriptionExpiry,
        store.IsActive,
        store.IsSuspended,
        CommercialStatus = StoreAccessPolicy.Evaluate(store).Reason,
        store.BusinessType,
        store.ApiKey,
        BackendUrl = string.IsNullOrWhiteSpace(store.BackendUrl) ? DefaultBackendUrl : store.BackendUrl,
        BridgeUrl = string.IsNullOrWhiteSpace(store.BridgeUrl) ? BridgeUrlFor(store.Id) : store.BridgeUrl,
        BridgePort = BridgePortFor(store.Id),
        ExpectedBridgeUrl = BridgeUrlFor(store.Id),
        Provisioning = includeProvisioning ? new
        {
            storeId = store.Id,
            bridgeBasePort = BridgeBasePort,
            bridgePort = BridgePortFor(store.Id),
            bridgeUrl = string.IsNullOrWhiteSpace(store.BridgeUrl) ? BridgeUrlFor(store.Id) : store.BridgeUrl,
            backendUrl = string.IsNullOrWhiteSpace(store.BackendUrl) ? DefaultBackendUrl : store.BackendUrl,
            portRule = "bridgePort = 3000 + storeId"
        } : null
    };

    private static async Task ExpireOverdueClientSubscriptions(AppDbContext db, CancellationToken ct)
    {
        var now = DateTime.Now;
        var overdue = await db.ClientSubscriptions.IgnoreQueryFilters()
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate.HasValue && s.EndDate.Value < now)
            .ToListAsync(ct);

        if (overdue.Count == 0) return;

        foreach (var sub in overdue)
        {
            sub.Status = SubscriptionStatus.Expired;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<object> BuildSuperSubscriptionsPayload(AppDbContext db, CancellationToken ct)
    {
        var subscriptions = await db.ClientSubscriptions.IgnoreQueryFilters()
            .AsNoTracking()
            .OrderByDescending(s => s.Status == SubscriptionStatus.Pending)
            .ThenByDescending(s => s.CreatedAt)
            .Take(300)
            .ToListAsync(ct);

        var storeIds = subscriptions.Select(s => s.StoreId).Distinct().ToList();
        var planIds = subscriptions.Select(s => s.PlanId).Distinct().ToList();

        var stores = await db.Stores.AsNoTracking()
            .Where(s => storeIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var plans = await db.SubscriptionPlans.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        object Stats() => new
        {
            total = subscriptions.Count,
            pending = subscriptions.Count(s => s.Status == SubscriptionStatus.Pending),
            active = subscriptions.Count(s => s.Status == SubscriptionStatus.Active),
            expired = subscriptions.Count(s => s.Status == SubscriptionStatus.Expired),
            cancelled = subscriptions.Count(s => s.Status == SubscriptionStatus.Cancelled),
            activeRevenue = subscriptions.Where(s => s.Status == SubscriptionStatus.Active).Sum(s => (double)s.PlanPreco),
            pendingPixAmount = subscriptions.Where(s => s.Status == SubscriptionStatus.Pending).Sum(s => (double)s.PlanPreco)
        };

        var items = subscriptions.Select(sub =>
        {
            stores.TryGetValue(sub.StoreId, out var store);
            plans.TryGetValue(sub.PlanId, out var plan);
            return ToSuperSubscriptionDto(sub, store, plan);
        }).ToList();

        return new
        {
            stats = Stats(),
            items,
            pix = new
            {
                mode = "manual",
                note = "O comprovante PIX deve ser conferido antes de ativar a assinatura."
            },
            generatedAt = DateTime.UtcNow
        };
    }

    private static object ToSuperSubscriptionDto(ClientSubscription sub, Store? store, SubscriptionPlan? plan)
    {
        var now = DateTime.Now;
        var daysUntilEnd = sub.EndDate.HasValue ? (int?)Math.Ceiling((sub.EndDate.Value - now).TotalDays) : null;
        return new
        {
            id = sub.Id,
            storeId = sub.StoreId,
            storeName = store?.Name ?? $"Loja #{sub.StoreId}",
            storeSlug = store?.Slug,
            storePlan = store == null ? null : PlanCatalog.Normalize(store.Plan),
            businessType = store?.BusinessType.ToString(),
            segmentLabel = store == null ? "Sem loja" : BusinessTypeLabel(store.BusinessType),
            isStoreActive = store?.IsActive ?? false,
            isStoreSuspended = store?.IsSuspended ?? false,
            clientPhone = sub.ClientPhone,
            clientName = sub.ClientName,
            planId = sub.PlanId,
            planNome = sub.PlanNome,
            planPreco = sub.PlanPreco,
            planDurationDays = plan?.DuracaoDias ?? 30,
            creditosTotal = sub.CreditosTotal,
            creditosUsados = sub.CreditosUsados,
            creditosRestantes = Math.Max(0, sub.CreditosTotal - sub.CreditosUsados),
            status = sub.Status.ToString(),
            statusCode = (int)sub.Status,
            startDate = sub.StartDate,
            endDate = sub.EndDate,
            daysUntilEnd,
            createdAt = sub.CreatedAt,
            notes = sub.Notes,
            isEffectivelyActive = sub.IsEffectivelyActive,
            isExpired = sub.IsExpired
        };
    }

    private static string AppendAdminNote(string? current, string? requested, string fallback)
    {
        var note = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (string.IsNullOrWhiteSpace(current)) return note;
        return $"{current.Trim()}\n{DateTime.Now:dd/MM/yyyy HH:mm} - {note}";
    }

    private static string? NormalizeManagedUserRole(string role)
    {
        var value = role.Trim().ToLowerInvariant();
        if (value is "admin" or "funcionario" or "barbeiro") return value;
        if (value is "professional" or "profissional") return "funcionario";
        return null;
    }

    private static string NormalizePaymentMode(string? paymentMode)
    {
        var value = paymentMode?.Trim().ToLowerInvariant();
        return value switch
        {
            "automatic_pix" or "automatic" or "auto" or "gateway" => "automatic_pix",
            "admin_adjustment" or "adjustment" or "ajuste" => "admin_adjustment",
            _ => "manual_pix"
        };
    }

    private static object ToUserDto(User user, string? storeName = null) => new
    {
        user.Id,
        user.Username,
        user.Role,
        user.PhoneNumber,
        user.Is2FAEnabled,
        user.StoreId,
        StoreName = storeName
    };

    private static object ToStorePaymentDto(StorePaymentRecord payment, string? storeName = null) => new
    {
        payment.Id,
        payment.StoreId,
        StoreName = storeName,
        Plan = PlanCatalog.Normalize(payment.Plan),
        payment.Amount,
        payment.PaymentMode,
        payment.Provider,
        payment.ProviderReference,
        Status = payment.Status.ToString(),
        StatusCode = (int)payment.Status,
        payment.PaidUntil,
        payment.CreatedAt,
        payment.ConfirmedAt,
        payment.ConfirmedBy,
        payment.Notes
    };

    private static async Task LogAction(AppDbContext db, string user, string action, string details)
    {
        try
        {
            // StoreId = db.TenantId: 0 para operações globais de superadmin
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO AuditLogs (Timestamp, User, Action, Details, StoreId) VALUES (DateTime('now'), {0}, {1}, {2}, {3})",
                user, action, details, db.TenantId);
        }
        catch
        {
            // Falha de log nao pode travar uma operacao administrativa.
        }
    }
}

public record CreateStoreRequest(string Name, string Slug, string? Plan, string? ApiKey, string? BackendUrl, string? BridgeUrl, string AdminUsername, string AdminPassword, string? BusinessType);
public record UpdateStoreRequest(string? Name, string? Slug, string? Plan, bool? IsActive, bool? IsSuspended, DateTime? ExpiresAt, DateTime? SubscriptionExpiry, string? ApiKey, string? BackendUrl, string? BridgeUrl, string? BusinessType);
public record SuperSubscriptionActionRequest(string? Notes);
public record StoreSubscriptionPaymentRequest(string? Plan, int? Months, int? Days, string? PaymentMode, bool? ActivateStore, string? Notes, decimal? Amount, string? ProviderReference);
public record StoreModuleOverrideRequest(bool? Enabled);
public record AdminAccountRequest(string Username, string Password, string? PhoneNumber);
public record AdminAccountUpdateRequest(string? Username, string? Password, string? PhoneNumber, string? Role);
