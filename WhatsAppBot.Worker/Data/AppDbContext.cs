using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Data;

public class AppDbContext : DbContext
{
    // Mantemos ITenantService apenas para obter valor inicial, mas o filtro global NÃO chama service dentro do model.
    private readonly ITenantService _tenantService;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantService tenantService)
        : base(options)
    {
        _tenantService = tenantService;
        TenantId = _tenantService.GetTenantId();
    }

    /// <summary>
    /// TenantId deve ser atualizado pelos fluxos (Worker/Webhook/Endpoints) antes de executar queries.
    /// Isso evita dependência de scoped service dentro do HasQueryFilter.
    /// </summary>
    public int TenantId { get; set; }

    public DbSet<Appointment> Appointments { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<Barbeiro> Barbeiros { get; set; }
    public DbSet<ConversationSession> ConversationSessions { get; set; }
    public DbSet<Store> Stores { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<SystemConfig> SystemConfigs { get; set; }
    public DbSet<UnavailableDay> UnavailableDays { get; set; }
    public DbSet<ServicoItem> Servicos { get; set; }
    public DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }
    public DbSet<ClientSubscription> ClientSubscriptions { get; set; }
    public DbSet<BarbeiroHorario> BarbeiroHorarios { get; set; }
    public DbSet<ClientVehicle> ClientVehicles { get; set; }
    public DbSet<CustomerProfile> CustomerProfiles { get; set; }
    public DbSet<CustomerTag> CustomerTags { get; set; }
    public DbSet<CustomerTagAssignment> CustomerTagAssignments { get; set; }
    public DbSet<CustomerEvent> CustomerEvents { get; set; }
    public DbSet<CustomerReminder> CustomerReminders { get; set; }
    public DbSet<OptimizationDevice> OptimizationDevices { get; set; }
    public DbSet<OptimizationTicket> OptimizationTickets { get; set; }
    public DbSet<OptimizationTicketEvent> OptimizationTicketEvents { get; set; }
    public DbSet<StorePaymentRecord> StorePaymentRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // FILTRO GLOBAL: toda query filtra automaticamente pelo StoreId do tenant.
        // TenantId == 0 → superadmin, vê tudo. TenantId > 0 → apenas a loja correta.
        modelBuilder.Entity<Appointment>()
            .HasQueryFilter(a => TenantId == 0 || a.StoreId == TenantId);

        // FIX 3: índices compostos em Appointments para as queries mais frequentes.
        // (StoreId, PhoneNumber): acelera o lookup por cliente no CheckAndSendRetentionAsync
        //   (FirstOrDefaultAsync após GROUP BY) e futuros filtros por telefone.
        // (StoreId, DateTime): acelera a query de lembretes futuros (Where a.DateTime > now)
        //   e a verificação de hasFuture dentro do loop de retenção.
        // Indexar com StoreId à esquerda é crucial: o HasQueryFilter sempre inclui StoreId
        // na cláusula WHERE, então o índice simples em PhoneNumber ou DateTime seria inútil
        // — o planner usaria full-scan de qualquer forma.
        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.StoreId, a.PhoneNumber })
            .HasDatabaseName("IX_Appointments_StoreId_Phone");

        modelBuilder.Entity<Appointment>()
            .HasIndex(a => new { a.StoreId, a.DateTime })
            .HasDatabaseName("IX_Appointments_StoreId_DateTime");

        // Chave primária composta garante que um mesmo número pode ter sessões em lojas diferentes
        modelBuilder.Entity<ConversationSession>()
            .HasKey(s => new { s.Phone, s.StoreId });

        modelBuilder.Entity<ConversationSession>()
            .HasIndex(s => new { s.Phone, s.StoreId })
            .IsUnique();

        // Corrigido: adicionado TenantId==0 para superadmin ver todas as sessões
        modelBuilder.Entity<ConversationSession>()
            .HasQueryFilter(s => TenantId == 0 || s.StoreId == TenantId);

        modelBuilder.Entity<Barbeiro>()
            .HasQueryFilter(b => TenantId == 0 || b.StoreId == TenantId);

        // User sem filtro global era o único DbSet sem proteção de tenant
        modelBuilder.Entity<User>()
            .HasQueryFilter(u => TenantId == 0 || u.StoreId == TenantId);

        modelBuilder.Entity<SystemConfig>()
            .ToTable("SystemConfigs")
            .HasKey(c => c.Key);

        modelBuilder.Entity<UnavailableDay>()
            .HasQueryFilter(d => TenantId == 0 || d.StoreId == TenantId);

        modelBuilder.Entity<UnavailableDay>()
            .HasIndex(d => new { d.StoreId, d.Date, d.BarberId })
            .IsUnique();

        modelBuilder.Entity<ServicoItem>()
            .HasQueryFilter(s => TenantId == 0 || s.StoreId == TenantId);

        // AuditLog: StoreId=0 = global/superadmin, visível apenas para TenantId=0
        // Logs de loja específica visíveis apenas para aquela loja ou superadmin
        modelBuilder.Entity<AuditLog>()
            .HasQueryFilter(a => TenantId == 0 || a.StoreId == TenantId);

        modelBuilder.Entity<SubscriptionPlan>()
            .HasQueryFilter(p => TenantId == 0 || p.StoreId == TenantId);

        modelBuilder.Entity<ClientSubscription>()
            .HasQueryFilter(s => TenantId == 0 || s.StoreId == TenantId);

        // Um registro por barbeiro por dia — garante que nunca haverá duplicata
        modelBuilder.Entity<BarbeiroHorario>()
            .HasIndex(h => new { h.BarbeiroId, h.DiaSemana })
            .IsUnique();

        modelBuilder.Entity<BarbeiroHorario>()
            .HasQueryFilter(h => TenantId == 0 || h.StoreId == TenantId);

        // ClientVehicle: isolado por loja; índice composto para lookup por cliente.
        modelBuilder.Entity<ClientVehicle>()
            .HasQueryFilter(v => TenantId == 0 || v.StoreId == TenantId);

        modelBuilder.Entity<ClientVehicle>()
            .HasIndex(v => new { v.StoreId, v.PhoneNumber })
            .HasDatabaseName("IX_ClientVehicles_StoreId_Phone");

        modelBuilder.Entity<CustomerProfile>()
            .HasQueryFilter(c => TenantId == 0 || c.StoreId == TenantId);

        modelBuilder.Entity<CustomerProfile>()
            .HasIndex(c => new { c.StoreId, c.CustomerKey })
            .IsUnique()
            .HasDatabaseName("IX_CustomerProfiles_StoreId_Key");

        modelBuilder.Entity<CustomerTag>()
            .HasQueryFilter(t => TenantId == 0 || t.StoreId == TenantId);

        modelBuilder.Entity<CustomerTag>()
            .HasIndex(t => new { t.StoreId, t.Name })
            .IsUnique()
            .HasDatabaseName("IX_CustomerTags_StoreId_Name");

        modelBuilder.Entity<CustomerTagAssignment>()
            .HasQueryFilter(t => TenantId == 0 || t.StoreId == TenantId);

        modelBuilder.Entity<CustomerTagAssignment>()
            .HasIndex(t => new { t.StoreId, t.CustomerKey, t.CustomerTagId })
            .IsUnique()
            .HasDatabaseName("IX_CustomerTagAssignments_Store_Key_Tag");

        modelBuilder.Entity<CustomerEvent>()
            .HasQueryFilter(e => TenantId == 0 || e.StoreId == TenantId);

        modelBuilder.Entity<CustomerEvent>()
            .HasIndex(e => new { e.StoreId, e.CustomerKey, e.CreatedAt })
            .HasDatabaseName("IX_CustomerEvents_Store_Key_Date");

        modelBuilder.Entity<CustomerReminder>()
            .HasQueryFilter(r => TenantId == 0 || r.StoreId == TenantId);

        modelBuilder.Entity<CustomerReminder>()
            .HasIndex(r => new { r.StoreId, r.CustomerKey, r.Status, r.DueDate })
            .HasDatabaseName("IX_CustomerReminders_Store_Key_Status_Date");

        modelBuilder.Entity<OptimizationDevice>()
            .HasQueryFilter(d => TenantId == 0 || d.StoreId == TenantId);

        modelBuilder.Entity<OptimizationDevice>()
            .HasIndex(d => new { d.StoreId, d.PhoneNumber })
            .HasDatabaseName("IX_OptimizationDevices_StoreId_Phone");

        modelBuilder.Entity<OptimizationTicket>()
            .HasQueryFilter(t => TenantId == 0 || t.StoreId == TenantId);

        modelBuilder.Entity<OptimizationTicket>()
            .HasIndex(t => new { t.StoreId, t.Status })
            .HasDatabaseName("IX_OptimizationTickets_StoreId_Status");

        modelBuilder.Entity<OptimizationTicket>()
            .HasIndex(t => new { t.StoreId, t.PhoneNumber })
            .HasDatabaseName("IX_OptimizationTickets_StoreId_Phone");

        modelBuilder.Entity<OptimizationTicket>()
            .HasIndex(t => new { t.StoreId, t.TicketNumber })
            .IsUnique()
            .HasDatabaseName("IX_OptimizationTickets_StoreId_Number");

        modelBuilder.Entity<OptimizationTicketEvent>()
            .HasQueryFilter(e => TenantId == 0 || e.StoreId == TenantId);

        modelBuilder.Entity<OptimizationTicketEvent>()
            .HasIndex(e => new { e.StoreId, e.OptimizationTicketId, e.CreatedAt })
            .HasDatabaseName("IX_OptimizationTicketEvents_Store_Ticket_Date");

        modelBuilder.Entity<StorePaymentRecord>()
            .HasQueryFilter(p => TenantId == 0 || p.StoreId == TenantId);

        modelBuilder.Entity<StorePaymentRecord>()
            .HasIndex(p => new { p.StoreId, p.CreatedAt })
            .HasDatabaseName("IX_StorePaymentRecords_Store_Date");
    }
}

