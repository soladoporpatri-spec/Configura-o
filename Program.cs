using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json;
using System.Data;
using Microsoft.Extensions.Options;
using WhatsAppBot.Worker;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;
using WhatsAppBot.Worker.Services.Modules;
using WhatsAppBot.Worker.Startup;
using Serilog;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using Serilog.Events;
using Serilog.Filters;
using WhatsAppBot.Worker.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Configuracao do Serilog com separacao de arquivos
var logDir = builder.Configuration["LOG_DIR"];
if (string.IsNullOrWhiteSpace(logDir))
    logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning)
    // Arquivo para Webhooks
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.FromSource("WhatsAppWebhook"))
        .WriteTo.File(Path.Combine(logDir, "webhooks-.txt"), rollingInterval: RollingInterval.Day))
    // Arquivo para Agendamentos (Filtra logs vindos do SchedulerService)
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.FromSource<SchedulerService>())
        .WriteTo.File(Path.Combine(logDir, "appointments-.txt"), rollingInterval: RollingInterval.Day))
    // Arquivo Geral (Fallback)
    .WriteTo.File(Path.Combine(logDir, "general-.txt"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Escalabilidade de Banco: Suporte dinâmico a SQLite ou PostgreSQL
var dataDir = builder.Configuration["BARBEARIA_DATA_DIR"];
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = builder.Environment.IsProduction() ? "/opt/barbearia/data" : AppContext.BaseDirectory;
Directory.CreateDirectory(dataDir);
string dbPath = Path.Combine(dataDir, "agendamentos.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<AppDbContext>(opt => {
    // Ensure Npgsql.EntityFrameworkCore.PostgreSQL is installed to resolve UseNpgsql
    bool isPostgres = connectionString?.Contains("Host=") ?? false;

    if (isPostgres)
        opt.UseNpgsql(connectionString);
    else
        opt.UseSqlite(connectionString ?? $"Data Source={dbPath}");
});

builder.Services.AddCors(options =>
{
    var allowedOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:4000,http://127.0.0.1:4000")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
              {
                  if (string.IsNullOrWhiteSpace(origin)) return true;
                  if (allowedOrigins.Contains(origin)) return true;
                  return builder.Environment.IsDevelopment()
                         && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                         && uri.Host.EndsWith(".ngrok-free.app", StringComparison.OrdinalIgnoreCase);
              })
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddHttpClient<WhatsAppClient>((sp, client) => {
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["WhatsAppBridge:BaseUrl"] ?? "http://127.0.0.1:3000");
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    var serviceApiKey = config["ApiKey"] ?? config["API_KEY"];
    if (string.IsNullOrWhiteSpace(serviceApiKey) && !builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("ApiKey/API_KEY ausente. Defina uma chave de servico por variavel de ambiente.");
    }
    client.DefaultRequestHeaders.Add("X-API-KEY", serviceApiKey ?? "dev-key-default");
});



builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantService, TenantService>();

builder.Services.AddMemoryCache();
builder.Services.Configure<AgendaConfig>(builder.Configuration.GetSection("Agenda"));
builder.Services.AddSingleton<ConversationSessionStore>();
builder.Services.AddSingleton<ModuleCatalog>();
builder.Services.AddScoped<FeatureAccessService>();

builder.Services.AddScoped<SchedulerService>();
builder.Services.AddScoped<AgendaService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ServiceCatalogService>();
builder.Services.AddScoped<StoreSettingsService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<ConversationStateManager>();

// State handlers: ConversationStateManager resolves them by DI.
// Garantir registro explícito com o mesmo namespace usado nos arquivos (Services.States).
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.IdleState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingMenuSelectionState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingNameState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingServiceSelectionState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingVehicleState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingBarberSelectionState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingDateSelectionState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingTimeSelectionState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.ConfirmingAppointmentState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingSubscriptionState>();
builder.Services.AddScoped<WhatsAppBot.Worker.Services.States.AwaitingCancelConfirmationState>();



// Centralização das Regras de Negócio de Horário - Agora Dinâmico
builder.Services.AddScoped<BusinessHours>(sp => 
{
    var cache = sp.GetRequiredService<IMemoryCache>();
    // Horário POR LOJA: cada loja tem seu próprio cache/expediente. Antes era um valor global
    // compartilhado entre barbearia e lava-jato (último acoplamento de configuração).
    var storeId = sp.GetRequiredService<WhatsAppBot.Worker.Services.ITenantService>().GetTenantId();
    return cache.GetOrCreate($"BusinessHours_{storeId}", entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IOptions<AgendaConfig>>().Value;

        var dbConfigs = db.Database.SqlQueryRaw<SystemConfigRow>("SELECT Key, Value FROM SystemConfigs")
            .AsEnumerable()
            .ToDictionary(c => c.Key, c => c.Value);

        // Prioridade: horário da loja (Store_{id}_*) → config global (legado) → default do AgendaConfig.
        TimeSpan Resolve(string perStoreKey, string globalKey, TimeSpan fallback)
        {
            if (storeId > 0 && dbConfigs.TryGetValue(perStoreKey, out var ps) && TimeSpan.TryParse(ps, out var pst)) return pst;
            if (dbConfigs.TryGetValue(globalKey, out var g) && TimeSpan.TryParse(g, out var gt)) return gt;
            return fallback;
        }

        var opening = Resolve($"Store_{storeId}_HorarioAbertura", "HorarioAbertura", config.HorarioAbertura);
        var closing = Resolve($"Store_{storeId}_HorarioFechamento", "HorarioFechamento", config.HorarioFechamento);

        return new BusinessHours { OpeningTime = opening, ClosingTime = closing };
    })!;
});

builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddHttpClient<GoogleSheetsService>(client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddSingleton<SpreadsheetMaintenanceService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddHostedService<BotWorker>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Barbearia Bot API",
        Version = "v1",
        Description = "API para Dashboard + WhatsApp Bot"
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var secret = builder.Configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            if (builder.Environment.IsProduction())
                throw new InvalidOperationException("JWT Secret deve ter pelo menos 32 caracteres em produção.");
            secret = "change-in-production-development-secret-32";
        }

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "BarbeariaAPI",
            ValidAudience = "BarbeariaDashboard",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
        };
    });


builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException ex)
    {
        var statusCode = ex.StatusCode > 0 ? ex.StatusCode : StatusCodes.Status400BadRequest;
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Bad request",
            details = app.Environment.IsDevelopment() ? ex.Message : null,
            path = context.Request.Path.Value,
            traceId = context.TraceIdentifier
        });
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Request failed: {Method} {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Internal server error",
            details = app.Environment.IsDevelopment() ? ex.Message : null,
            path = context.Request.Path.Value,
            traceId = context.TraceIdentifier
        });
    }
});

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;
        var statusCode = exception is BadHttpRequestException badRequest
            ? (badRequest.StatusCode > 0 ? badRequest.StatusCode : StatusCodes.Status400BadRequest)
            : StatusCodes.Status500InternalServerError;

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            error = statusCode == StatusCodes.Status400BadRequest ? "Bad request" : "Internal server error",
            details = app.Environment.IsDevelopment() ? exception?.Message : null,
            path = context.Request.Path.Value,
            traceId = context.TraceIdentifier
        });
    });
});

app.UseAuthentication();
app.UseAuthorization();
app.MapHub<NotificationHub>("/hubs/notifications");

// Middleware de autenticação
var apiKey = builder.Configuration["ApiKey"] ?? builder.Configuration["API_KEY"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Development"))
        apiKey = "dev-key-default";
    else
        throw new InvalidOperationException("ApiKey/API_KEY ausente. Defina uma chave de servico por variavel de ambiente.");
}

app.MapSaasEndpoints(apiKey);
app.MapSpreadsheetEndpoints(apiKey);
app.MapSettingsEndpoints(apiKey);
app.MapOperationalEndpoints(apiKey);
app.MapBarberEndpoints(apiKey);
app.MapSchedulingEndpoints(apiKey);
app.MapServicosEndpoints(apiKey);
app.MapCustomerCrmEndpoints(apiKey);
app.MapOptimizationEndpoints(apiKey);
app.MapAnalyticsEndpoints(apiKey);
app.MapAuthEndpoints(builder.Configuration["WhatsAppBridge:BaseUrl"] ?? "http://127.0.0.1:3000");
app.MapPixEndpoints();
app.MapWebhookEndpoints(apiKey);
app.MapSuperAdminEndpoints(apiKey);
app.MapSubscriptionEndpoints(apiKey);

// DB init
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

// Durante o seeding de startup não há contexto de usuário/JWT.
// TenantId = 0 (superadmin) garante visibilidade total de todos os registros.
db.TenantId = 0;

if (db.Database.IsSqlite())
{
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

// Ensure base schema is created before running any column-alter logic (avoids 'no such table').
db.Database.ExecuteSqlRaw(@"
    CREATE TABLE IF NOT EXISTS Appointments (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        PhoneNumber TEXT NOT NULL,
        ContactName TEXT NOT NULL,
        Notes TEXT NULL,
        DateTime DATETIME NOT NULL,
        ServiceId INTEGER NOT NULL DEFAULT 1,
        BarberId INTEGER NULL,
        BarberName TEXT NULL,
        DuracaoMinutos INTEGER NOT NULL,
        Preco DECIMAL NOT NULL,
        ReminderSent INTEGER NOT NULL DEFAULT 0,
        ReminderDayBefore INTEGER NOT NULL DEFAULT 0,
        ReminderOneHour INTEGER NOT NULL DEFAULT 0,
        ThanksSent INTEGER NOT NULL DEFAULT 0,
        RetentionReminderSent INTEGER NOT NULL DEFAULT 0,
        PresencaConfirmada INTEGER NOT NULL DEFAULT 0,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        Status TEXT NOT NULL DEFAULT 'ativo',
        CancelledAt DATETIME NULL,
        CancelledBy TEXT NULL,
        VehicleInfo TEXT NULL,
        IsWalkIn INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS AuditLogs (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Timestamp DATETIME NOT NULL,
        User TEXT NOT NULL,
        Action TEXT NOT NULL,
        Details TEXT NULL,
        StoreId INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS SystemConfigs (
        Key TEXT PRIMARY KEY,
        Value TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS UnavailableDays (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        Date DATETIME NOT NULL,
        Type TEXT NOT NULL DEFAULT 'fechado',
        Reason TEXT NOT NULL DEFAULT '',
        BarberId INTEGER NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE TABLE IF NOT EXISTS Barbeiros (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        Nome TEXT NOT NULL,
        Ativo INTEGER NOT NULL DEFAULT 1,
        Cor TEXT NOT NULL DEFAULT '#3498db',
        Especialidade TEXT NOT NULL DEFAULT 'Geral',
        Adicional TEXT NOT NULL DEFAULT '',
        WorkStart TEXT NOT NULL DEFAULT '00:00:00',
        WorkEnd TEXT NOT NULL DEFAULT '00:00:00',
        LunchStart TEXT NULL,
        LunchEnd TEXT NULL,
        WorkingDays TEXT NOT NULL DEFAULT '1,2,3,4,5,6',
        BlockedSlotsJson TEXT NOT NULL DEFAULT '{{}}',
        CustomHoursJson TEXT NOT NULL DEFAULT '{{}}'
    );
    CREATE TABLE IF NOT EXISTS Users (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Username TEXT NOT NULL COLLATE NOCASE,
        PasswordHash TEXT NOT NULL,
        Role TEXT NOT NULL,
        StoreId INTEGER NOT NULL DEFAULT 1,
        Is2FAEnabled INTEGER NOT NULL DEFAULT 0,
        PhoneNumber TEXT NULL,
        TwoFactorSecret TEXT NULL,
        BarberId INTEGER NULL
    );
    CREATE TABLE IF NOT EXISTS Stores (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Name TEXT NOT NULL,
        Slug TEXT NOT NULL UNIQUE,
        Plan TEXT NOT NULL DEFAULT 'Free',
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        ExpiresAt DATETIME NULL,
        IsActive INTEGER NOT NULL DEFAULT 1,
        ApiKey TEXT NULL,
        BackendUrl TEXT NULL,
        BridgeUrl TEXT NULL,
        BusinessType INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS ConversationSessions (
        Phone       TEXT    NOT NULL,
        StoreId     INTEGER NOT NULL DEFAULT 1,
        State       INTEGER NOT NULL,
        CustomerName TEXT   NULL,
        SelectedServiceId INTEGER NULL,
        SelectedBarberId  INTEGER NULL,
        SelectedBarberName TEXT NULL,
        SelectedVehicle TEXT NULL,
        SelectedDate DATETIME NULL,
        TimeOffset  INTEGER NOT NULL DEFAULT 0,
        InvalidResponseCount INTEGER NOT NULL DEFAULT 0,
        IsWalkInMode INTEGER NOT NULL DEFAULT 0,
        LastPollId  TEXT    NULL,
        LastInteraction DATETIME NOT NULL,
        PRIMARY KEY (Phone, StoreId)
    );
    CREATE TABLE IF NOT EXISTS Servicos (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        Nome TEXT NOT NULL,
        DuracaoMinutos INTEGER NOT NULL DEFAULT 30,
        Preco DECIMAL NOT NULL DEFAULT 0,
        Ativo INTEGER NOT NULL DEFAULT 1,
        Ordem INTEGER NOT NULL DEFAULT 0,
        OcupaHorario INTEGER NOT NULL DEFAULT 1
    );
    CREATE TABLE IF NOT EXISTS SubscriptionPlans (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        Nome TEXT NOT NULL,
        Descricao TEXT NOT NULL DEFAULT '',
        Preco DECIMAL NOT NULL DEFAULT 0,
        Creditos INTEGER NOT NULL DEFAULT 4,
        DuracaoDias INTEGER NOT NULL DEFAULT 30,
        ServicosPermitidos TEXT NOT NULL DEFAULT '*',
        Ativo INTEGER NOT NULL DEFAULT 1,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE TABLE IF NOT EXISTS ClientSubscriptions (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        ClientPhone TEXT NOT NULL,
        ClientName TEXT NOT NULL DEFAULT '',
        PlanId INTEGER NOT NULL DEFAULT 0,
        PlanNome TEXT NOT NULL DEFAULT '',
        PlanPreco DECIMAL NOT NULL DEFAULT 0,
        ServicosPermitidos TEXT NOT NULL DEFAULT '*',
        CreditosTotal INTEGER NOT NULL DEFAULT 4,
        CreditosUsados INTEGER NOT NULL DEFAULT 0,
        Status INTEGER NOT NULL DEFAULT 0,
        StartDate DATETIME NULL,
        EndDate DATETIME NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        Notes TEXT NULL
    );
    CREATE TABLE IF NOT EXISTS ClientVehicles (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        PhoneNumber TEXT NOT NULL,
        Plate TEXT NOT NULL,
        Model TEXT NULL,
        LastUsed DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE TABLE IF NOT EXISTS CustomerProfiles (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        CustomerKey TEXT NOT NULL,
        PhoneNumber TEXT NOT NULL DEFAULT '',
        DisplayName TEXT NOT NULL DEFAULT '',
        ManualStatus TEXT NULL,
        IsBlocked INTEGER NOT NULL DEFAULT 0,
        InternalNotes TEXT NULL,
        Preferences TEXT NULL,
        PreferredService TEXT NULL,
        PreferredProfessional TEXT NULL,
        BestTime TEXT NULL,
        ReturnFrequencyDays INTEGER NULL,
        ContactPreference TEXT NULL,
        Birthday DATETIME NULL,
        Source TEXT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE TABLE IF NOT EXISTS CustomerTags (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        Name TEXT NOT NULL,
        Color TEXT NOT NULL DEFAULT '#64748b',
        IsSystem INTEGER NOT NULL DEFAULT 0,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE TABLE IF NOT EXISTS CustomerTagAssignments (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        CustomerKey TEXT NOT NULL,
        CustomerTagId INTEGER NOT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE TABLE IF NOT EXISTS CustomerEvents (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        CustomerKey TEXT NOT NULL,
        Type TEXT NOT NULL DEFAULT 'note',
        Title TEXT NOT NULL DEFAULT '',
        Description TEXT NULL,
        RelatedAppointmentId INTEGER NULL,
        CreatedBy TEXT NOT NULL DEFAULT 'Dashboard',
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        VisibleToCustomer INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS CustomerReminders (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL DEFAULT 1,
        CustomerKey TEXT NOT NULL,
        Title TEXT NOT NULL DEFAULT '',
        Description TEXT NULL,
        DueDate DATETIME NOT NULL,
        Status TEXT NOT NULL DEFAULT 'pendente',
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        CompletedAt DATETIME NULL
    );
    CREATE TABLE IF NOT EXISTS BarbeiroHorarios (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        BarbeiroId INTEGER NOT NULL,
        StoreId INTEGER NOT NULL DEFAULT 1,
        DiaSemana INTEGER NOT NULL,
        Folga INTEGER NOT NULL DEFAULT 0,
        Entrada TEXT NOT NULL DEFAULT '08:00:00',
        Saida TEXT NOT NULL DEFAULT '18:00:00',
        InicioAlmoco TEXT NULL,
        FimAlmoco TEXT NULL,
        UNIQUE(BarbeiroId, DiaSemana)
    );
    CREATE TABLE IF NOT EXISTS OptimizationDevices (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL,
        CustomerName TEXT NOT NULL,
        PhoneNumber TEXT NOT NULL,
        DeviceType TEXT NOT NULL DEFAULT 'Desktop',
        OperatingSystem TEXT NOT NULL DEFAULT 'Windows 11',
        Processor TEXT NULL,
        Gpu TEXT NULL,
        RamGb INTEGER NULL,
        StorageType TEXT NOT NULL DEFAULT 'Nao informado',
        MainUse TEXT NOT NULL DEFAULT 'Uso geral',
        Notes TEXT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        IsActive INTEGER NOT NULL DEFAULT 1
    );
    CREATE TABLE IF NOT EXISTS OptimizationTickets (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL,
        TicketNumber TEXT NOT NULL,
        PhoneNumber TEXT NOT NULL,
        CustomerName TEXT NOT NULL,
        OptimizationDeviceId INTEGER NULL,
        AppointmentId INTEGER NULL,
        ServiceId INTEGER NULL,
        ServiceMode TEXT NOT NULL DEFAULT 'Presencial',
        Goal TEXT NOT NULL DEFAULT 'Melhorar desempenho geral',
        ReportedProblem TEXT NOT NULL DEFAULT '',
        Urgency TEXT NOT NULL DEFAULT 'Essa semana',
        Status INTEGER NOT NULL DEFAULT 0,
        BeforeNotes TEXT NULL,
        OptimizationChecklistJson TEXT NOT NULL DEFAULT '[]',
        AfterNotes TEXT NULL,
        ResultSummary TEXT NULL,
        EstimatedAmount DECIMAL NULL,
        FinalAmount DECIMAL NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        StartedAt DATETIME NULL,
        CompletedAt DATETIME NULL,
        ClosedAt DATETIME NULL
    );
    CREATE TABLE IF NOT EXISTS OptimizationTicketEvents (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL,
        OptimizationTicketId INTEGER NOT NULL,
        Type TEXT NOT NULL DEFAULT 'note',
        FromStatus TEXT NULL,
        ToStatus TEXT NULL,
        Message TEXT NOT NULL DEFAULT '',
        CreatedBy TEXT NOT NULL DEFAULT 'Dashboard',
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        VisibleToCustomer INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS StorePaymentRecords (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        StoreId INTEGER NOT NULL,
        Plan TEXT NOT NULL DEFAULT 'Professional',
        Amount DECIMAL NOT NULL DEFAULT 0,
        PaymentMode TEXT NOT NULL DEFAULT 'manual_pix',
        Provider TEXT NOT NULL DEFAULT 'manual',
        ProviderReference TEXT NULL,
        Status INTEGER NOT NULL DEFAULT 1,
        PaidUntil DATETIME NOT NULL,
        CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
        ConfirmedAt DATETIME NULL,
        ConfirmedBy TEXT NOT NULL DEFAULT 'Superadmin',
        Notes TEXT NULL
    );");

// Verificação segura de colunas para evitar falhas de inicialização
if (db.Database.IsSqlite())
{
    var appointmentColumns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Appointments')").ToList();
    var barberColumns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Barbeiros')").ToList();
    var userColumns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Users')").ToList();

    if (!userColumns.Contains("BarberId")) {
        db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN BarberId INTEGER NULL;");
    }
    if (!userColumns.Contains("StoreId")) {
        db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN StoreId INTEGER NOT NULL DEFAULT 1;");
    }
    if (!userColumns.Contains("PhoneNumber")) {
        db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN PhoneNumber TEXT NULL;");
    }
    if (!userColumns.Contains("Is2FAEnabled")) {
        db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN Is2FAEnabled INTEGER NOT NULL DEFAULT 0;");
    }
    if (!userColumns.Contains("TwoFactorSecret")) {
        db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN TwoFactorSecret TEXT NULL;");
    }

    var sessionColumns = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('ConversationSessions')").ToList();
    var sessionHadSelectedServiceId = sessionColumns.Any(c => c.Equals("SelectedServiceId", StringComparison.OrdinalIgnoreCase));
    var sessionHadSelectedService = sessionColumns.Any(c => c.Equals("SelectedService", StringComparison.OrdinalIgnoreCase));
    if (!sessionColumns.Any(c => c.Equals("StoreId", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN StoreId INTEGER NOT NULL DEFAULT 1;");
    }
    if (!sessionHadSelectedServiceId) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN SelectedServiceId INTEGER NULL;");
    }
    if (!sessionHadSelectedServiceId && sessionHadSelectedService) {
        db.Database.ExecuteSqlRaw("UPDATE ConversationSessions SET SelectedServiceId = SelectedService WHERE SelectedServiceId IS NULL;");
    }
    if (!sessionColumns.Any(c => c.Equals("LastPollId", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN LastPollId TEXT NULL;");
    }
    if (!sessionColumns.Any(c => c.Equals("TimeOffset", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN TimeOffset INTEGER NOT NULL DEFAULT 0;");
    }
    if (!sessionColumns.Any(c => c.Equals("InvalidResponseCount", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN InvalidResponseCount INTEGER NOT NULL DEFAULT 0;");
    }
    if (!sessionColumns.Any(c => c.Equals("IsWalkInMode", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN IsWalkInMode INTEGER NOT NULL DEFAULT 0;");
    }
    if (!sessionColumns.Any(c => c.Equals("SelectedVehicle", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN SelectedVehicle TEXT NULL;");
    }
    if (!sessionColumns.Any(c => c.Equals("SubscriptionPendingPlanId", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN SubscriptionPendingPlanId INTEGER NULL;");
    }
    if (!sessionColumns.Any(c => c.Equals("PendingCancelAppointmentId", StringComparison.OrdinalIgnoreCase))) {
        db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN PendingCancelAppointmentId INTEGER NULL;");
    }

    // Migração: trocar PRIMARY KEY de (Phone) para (Phone, StoreId)
    // Necessário para permitir que o mesmo número de WhatsApp tenha sessões em lojas diferentes
    // sem misturar estado do chatbot entre barbearia e lavajato.
    try
    {
        // Conta quantas colunas compõem a PK atual.
        // pk=0 → não é PK; pk=1 → 1ª coluna da PK; pk=2 → 2ª coluna, etc.
        var pkColumns = db.Database
            .SqlQueryRaw<int>("SELECT pk AS Value FROM pragma_table_info('ConversationSessions') WHERE pk > 0")
            .ToList();

        if (pkColumns.Count < 2) // Ainda está com PK simples (apenas Phone)
        {
            db.Database.ExecuteSqlRaw(@"
                BEGIN TRANSACTION;

                ALTER TABLE ConversationSessions RENAME TO ConversationSessions_pk_old;

                CREATE TABLE ConversationSessions (
                    Phone       TEXT    NOT NULL,
                    StoreId     INTEGER NOT NULL DEFAULT 1,
                    State       INTEGER NOT NULL,
                    CustomerName TEXT   NULL,
                    SelectedServiceId INTEGER NULL,
                    SelectedBarberId  INTEGER NULL,
                    SelectedBarberName TEXT NULL,
                    SelectedVehicle TEXT NULL,
                    SelectedDate DATETIME NULL,
                    TimeOffset  INTEGER NOT NULL DEFAULT 0,
                    InvalidResponseCount INTEGER NOT NULL DEFAULT 0,
                    IsWalkInMode INTEGER NOT NULL DEFAULT 0,
                    LastPollId  TEXT    NULL,
                    LastInteraction DATETIME NOT NULL,
                    PRIMARY KEY (Phone, StoreId)
                );

                INSERT INTO ConversationSessions
                    (Phone, StoreId, State, CustomerName, SelectedServiceId, SelectedBarberId,
                     SelectedBarberName, SelectedVehicle, SelectedDate, TimeOffset, InvalidResponseCount,
                     IsWalkInMode, LastPollId, LastInteraction)
                SELECT
                    Phone, StoreId, State, CustomerName, SelectedServiceId, SelectedBarberId,
                    SelectedBarberName, SelectedVehicle, SelectedDate, TimeOffset, InvalidResponseCount,
                    IsWalkInMode, LastPollId, LastInteraction
                FROM ConversationSessions_pk_old;

                DROP TABLE ConversationSessions_pk_old;

                CREATE UNIQUE INDEX IF NOT EXISTS IX_ConversationSessions_Phone_StoreId
                    ON ConversationSessions (Phone, StoreId);

                COMMIT;
            ");
            Console.WriteLine("Migração aplicada: ConversationSessions agora usa PK composta (Phone, StoreId).");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Erro ao migrar PK de ConversationSessions: " + ex.Message);
    }

    var appointmentHadServiceId = appointmentColumns.Any(c => c.Equals("ServiceId", StringComparison.OrdinalIgnoreCase));
    var appointmentHadServico = appointmentColumns.Any(c => c.Equals("Servico", StringComparison.OrdinalIgnoreCase));

    void EnsureColumn(string name, string type, string? defaultValue = null) {
        if (!appointmentColumns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase))) {
            try { 
                string sql = $"ALTER TABLE Appointments ADD COLUMN {name} {type}";
                if (defaultValue != null) sql += $" NOT NULL DEFAULT {defaultValue}";
                db.Database.ExecuteSqlRaw(sql + ";"); 
            } catch (Exception ex) { Console.WriteLine($"Erro ao adicionar coluna {name}: " + ex.Message); }
        }
    }

    EnsureColumn("StoreId", "INTEGER", "1");
    EnsureColumn("Notes", "TEXT");
    EnsureColumn("ServiceId", "INTEGER", "1");
    if (!appointmentHadServiceId && appointmentHadServico)
    {
        db.Database.ExecuteSqlRaw("UPDATE Appointments SET ServiceId = Servico WHERE ServiceId IS NULL OR ServiceId = 1;");
    }
    EnsureColumn("BarberId", "INTEGER", "1");
    EnsureColumn("BarberName", "TEXT");
    EnsureColumn("LastPollId", "TEXT");
    EnsureColumn("ReminderSent", "INTEGER", "0");
    EnsureColumn("ReminderDayBefore", "INTEGER", "0");
    EnsureColumn("ReminderOneHour", "INTEGER", "0");
    EnsureColumn("ThanksSent", "INTEGER", "0");
    EnsureColumn("RetentionReminderSent", "INTEGER", "0");
    EnsureColumn("PresencaConfirmada", "INTEGER", "0");
    EnsureColumn("CreatedAt", "DATETIME", "CURRENT_TIMESTAMP");
    EnsureColumn("Status", "TEXT", "'ativo'");
    EnsureColumn("CancelledAt", "DATETIME");
    EnsureColumn("CancelledBy", "TEXT");
    EnsureColumn("VehicleInfo", "TEXT");
    EnsureColumn("IsWalkIn", "INTEGER", "0");

    // Garantir coluna Ativo na tabela de Barbeiros
    try {
        if (!barberColumns.Contains("StoreId")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN StoreId INTEGER NOT NULL DEFAULT 1;");
        }
        if (!barberColumns.Contains("Ativo")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN Ativo INTEGER NOT NULL DEFAULT 1;");
        }
        if (!barberColumns.Contains("Cor")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN Cor TEXT NOT NULL DEFAULT '#3498db';");
        }
        if (!barberColumns.Contains("Especialidade")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN Especialidade TEXT NOT NULL DEFAULT 'Geral';");
        }
        if (!barberColumns.Contains("Adicional")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN Adicional TEXT NOT NULL DEFAULT '';");
        }
        if (!barberColumns.Contains("WorkStart")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN WorkStart TEXT NOT NULL DEFAULT '00:00:00';");
        }
        if (!barberColumns.Contains("WorkEnd")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN WorkEnd TEXT NOT NULL DEFAULT '00:00:00';");
        }
        if (!barberColumns.Contains("LunchStart")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN LunchStart TEXT NULL;");
        }
        if (!barberColumns.Contains("LunchEnd")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN LunchEnd TEXT NULL;");
        }
        if (!barberColumns.Contains("WorkingDays")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN WorkingDays TEXT NOT NULL DEFAULT '1,2,3,4,5,6';");
        }
        if (!barberColumns.Contains("BlockedSlotsJson")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN BlockedSlotsJson TEXT NOT NULL DEFAULT '{{}}';");
        }
        if (!barberColumns.Contains("CustomHoursJson")) {
            db.Database.ExecuteSqlRaw("ALTER TABLE Barbeiros ADD COLUMN CustomHoursJson TEXT NOT NULL DEFAULT '{{}}';");
        }
        var storeColumns = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Stores')").ToList();
        bool StoreColumnExists(string name) => storeColumns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!StoreColumnExists("SubscriptionExpiry")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN SubscriptionExpiry DATETIME NOT NULL DEFAULT '2027-12-31 23:59:59';");
        if (!StoreColumnExists("ExpiresAt")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN ExpiresAt DATETIME NULL;");
        if (!StoreColumnExists("IsSuspended")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN IsSuspended INTEGER NOT NULL DEFAULT 0;");
        if (!StoreColumnExists("LastAccess")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN LastAccess DATETIME NULL;");
        if (!StoreColumnExists("BotStatus")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN BotStatus TEXT NULL;");
        if (!StoreColumnExists("ApiKey")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN ApiKey TEXT NULL;");
        if (!StoreColumnExists("BackendUrl")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN BackendUrl TEXT NULL;");
        if (!StoreColumnExists("BridgeUrl")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN BridgeUrl TEXT NULL;");
        if (!StoreColumnExists("BusinessType")) db.Database.ExecuteSqlRaw("ALTER TABLE Stores ADD COLUMN BusinessType INTEGER NOT NULL DEFAULT 0;");

        // AuditLog: StoreId para segregação por loja
        var auditColumns = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('AuditLogs')").ToList();
        if (!auditColumns.Any(c => c.Equals("StoreId", StringComparison.OrdinalIgnoreCase)))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE AuditLogs ADD COLUMN StoreId INTEGER NOT NULL DEFAULT 0;");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_AuditLogs_StoreId ON AuditLogs (StoreId);");
            Console.WriteLine("Migração: coluna StoreId adicionada à tabela AuditLogs.");
        }
    } catch (Exception ex) { Console.WriteLine("Erro ao atualizar tabela Barbeiros: " + ex.Message); }

    // Índices que dependem de StoreId são criados AQUI, após as migrações de coluna.
    // Antes eram criados no bloco inicial de CREATE TABLE — o que crashava o startup
    // ("no such column: StoreId") em bancos com tabelas legadas sem a coluna ainda adicionada.
    try
    {
        var udCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('UnavailableDays')").ToList();
        if (!udCols.Any(c => c.Equals("StoreId", StringComparison.OrdinalIgnoreCase)))
            db.Database.ExecuteSqlRaw("ALTER TABLE UnavailableDays ADD COLUMN StoreId INTEGER NOT NULL DEFAULT 1;");

        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_AuditLogs_StoreId ON AuditLogs (StoreId);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_UnavailableDays_Store_Date_Barber ON UnavailableDays (StoreId, Date, BarberId);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_ConversationSessions_Phone_StoreId ON ConversationSessions (Phone, StoreId);");

        // FIX 3: índices de performance em Appointments criados de forma idempotente (IF NOT EXISTS)
        // para bancos existentes que já passaram pelas migrações mas não têm esses índices.
        // A migration EF cria nos bancos novos; este bloco garante a cobertura em upgrades in-place.
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Appointments_StoreId_Phone ON Appointments (StoreId, PhoneNumber);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Appointments_StoreId_DateTime ON Appointments (StoreId, DateTime);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ClientVehicles_StoreId_Phone ON ClientVehicles (StoreId, PhoneNumber);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomerProfiles_StoreId_Key ON CustomerProfiles (StoreId, CustomerKey);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomerTags_StoreId_Name ON CustomerTags (StoreId, Name);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomerTagAssignments_Store_Key_Tag ON CustomerTagAssignments (StoreId, CustomerKey, CustomerTagId);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CustomerEvents_Store_Key_Date ON CustomerEvents (StoreId, CustomerKey, CreatedAt);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_CustomerReminders_Store_Key_Status_Date ON CustomerReminders (StoreId, CustomerKey, Status, DueDate);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_OptimizationDevices_StoreId_Phone ON OptimizationDevices (StoreId, PhoneNumber);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_OptimizationTickets_StoreId_Status ON OptimizationTickets (StoreId, Status);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_OptimizationTickets_StoreId_Phone ON OptimizationTickets (StoreId, PhoneNumber);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_OptimizationTickets_StoreId_Number ON OptimizationTickets (StoreId, TicketNumber);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_OptimizationTicketEvents_Store_Ticket_Date ON OptimizationTicketEvents (StoreId, OptimizationTicketId, CreatedAt);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_StorePaymentRecords_Store_Date ON StorePaymentRecords (StoreId, CreatedAt);");

        // Coluna OcupaHorario em Servicos (serviços não-bloqueantes, ex.: polimento no lava-jato).
        var svcCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Servicos')").ToList();
        if (!svcCols.Any(c => c.Equals("OcupaHorario", StringComparison.OrdinalIgnoreCase)))
            db.Database.ExecuteSqlRaw("ALTER TABLE Servicos ADD COLUMN OcupaHorario INTEGER NOT NULL DEFAULT 1;");

        // ServicosPermitidos em SubscriptionPlans e ClientSubscriptions (adicionado na versão 2.1).
        // DEFAULT '*' preserva comportamento anterior: planos/assinaturas existentes cobrem todos os serviços.
        var planCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('SubscriptionPlans')").ToList();
        if (!planCols.Any(c => c.Equals("ServicosPermitidos", StringComparison.OrdinalIgnoreCase)))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE SubscriptionPlans ADD COLUMN ServicosPermitidos TEXT NOT NULL DEFAULT '*';");
            Console.WriteLine("Migração: coluna ServicosPermitidos adicionada a SubscriptionPlans.");
        }

        var subCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('ClientSubscriptions')").ToList();
        if (!subCols.Any(c => c.Equals("ServicosPermitidos", StringComparison.OrdinalIgnoreCase)))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE ClientSubscriptions ADD COLUMN ServicosPermitidos TEXT NOT NULL DEFAULT '*';");
            Console.WriteLine("Migração: coluna ServicosPermitidos adicionada a ClientSubscriptions.");
        }

        // BarbeiroId / BarbeiroNome em ClientSubscriptions (v2.2 — assinatura atrelada a barbeiro)
        if (!subCols.Any(c => c.Equals("BarbeiroId", StringComparison.OrdinalIgnoreCase)))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE ClientSubscriptions ADD COLUMN BarbeiroId INTEGER NULL;");
            Console.WriteLine("Migração: coluna BarbeiroId adicionada a ClientSubscriptions.");
        }
        if (!subCols.Any(c => c.Equals("BarbeiroNome", StringComparison.OrdinalIgnoreCase)))
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE ClientSubscriptions ADD COLUMN BarbeiroNome TEXT NULL;");
            Console.WriteLine("Migração: coluna BarbeiroNome adicionada a ClientSubscriptions.");
        }

        // SubscriptionPendingBarberId / SubscriptionPendingBarberNome em ConversationSessions
        if (!sessionColumns.Any(c => c.Equals("SubscriptionPendingBarberId", StringComparison.OrdinalIgnoreCase)))
            db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN SubscriptionPendingBarberId INTEGER NULL;");
        if (!sessionColumns.Any(c => c.Equals("SubscriptionPendingBarberNome", StringComparison.OrdinalIgnoreCase)))
            db.Database.ExecuteSqlRaw("ALTER TABLE ConversationSessions ADD COLUMN SubscriptionPendingBarberNome TEXT NULL;");
    }
    catch (Exception ex) { Console.WriteLine("Erro ao criar indices dependentes de StoreId: " + ex.Message); }
}

// Corrigir schema legado: em alguns bancos, BarberId foi criado como NOT NULL sem DEFAULT.
// Isso quebra o fluxo quando o bot não define barbeiro.
// Estratégia: se existir NOT NULL em BarberId, reconstruir a tabela Appointments com BarberId NULL.
try
{
    var barberIdNotNull = db.Database
        .SqlQueryRaw<int>("SELECT [notnull] AS Value FROM pragma_table_info('Appointments') WHERE name = 'BarberId' LIMIT 1")
        .FirstOrDefault();

    if (barberIdNotNull == 1)
    {
        db.Database.ExecuteSqlRaw(@"
            BEGIN TRANSACTION;

            ALTER TABLE Appointments RENAME TO Appointments_old;

            CREATE TABLE Appointments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StoreId INTEGER NOT NULL DEFAULT 1,
                PhoneNumber TEXT NOT NULL,
                ContactName TEXT NOT NULL,
                Notes TEXT NULL,
                DateTime DATETIME NOT NULL,
                ServiceId INTEGER NOT NULL DEFAULT 1,
                BarberId INTEGER NULL,
                BarberName TEXT NULL,
                DuracaoMinutos INTEGER NOT NULL,
                Preco DECIMAL NOT NULL,
                ReminderSent INTEGER NOT NULL DEFAULT 0,
                ReminderDayBefore INTEGER NOT NULL DEFAULT 0,
                ReminderOneHour INTEGER NOT NULL DEFAULT 0,
                ThanksSent INTEGER NOT NULL DEFAULT 0,
                RetentionReminderSent INTEGER NOT NULL DEFAULT 0,
                PresencaConfirmada INTEGER NOT NULL DEFAULT 0,
                CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                Status TEXT NOT NULL DEFAULT 'ativo',
                CancelledAt DATETIME NULL,
                CancelledBy TEXT NULL,
                VehicleInfo TEXT NULL,
                IsWalkIn INTEGER NOT NULL DEFAULT 0
            );

            INSERT INTO Appointments (
                Id, StoreId, PhoneNumber, ContactName, Notes, DateTime, ServiceId, BarberId, BarberName,
                DuracaoMinutos, Preco, ReminderSent, ReminderDayBefore, ReminderOneHour,
                ThanksSent, RetentionReminderSent, PresencaConfirmada, CreatedAt, Status, CancelledAt, CancelledBy,
                VehicleInfo, IsWalkIn
            )
            SELECT
                Id, StoreId, PhoneNumber, ContactName, Notes, DateTime, ServiceId, BarberId, BarberName,
                DuracaoMinutos, Preco, ReminderSent, ReminderDayBefore, ReminderOneHour,
                ThanksSent, RetentionReminderSent, PresencaConfirmada, CreatedAt, Status, CancelledAt, CancelledBy,
                VehicleInfo, IsWalkIn
            FROM Appointments_old;

            DROP TABLE Appointments_old;

            COMMIT;
        ");

        Console.WriteLine("Schema corrigido: BarberId ajustado para permitir NULL.");
    }
}
catch (Exception ex)
{
    Console.WriteLine("Falha ao corrigir schema legado de BarberId: " + ex.Message);
}

try
{
    var finalAppointmentColumns = db.Database
        .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Appointments')")
        .ToList();

    if (!finalAppointmentColumns.Any(c => c.Equals("RetentionReminderSent", StringComparison.OrdinalIgnoreCase)))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Appointments ADD COLUMN RetentionReminderSent INTEGER NOT NULL DEFAULT 0;");
        Console.WriteLine("Schema corrigido: RetentionReminderSent adicionada em Appointments.");
    }
}
catch (Exception ex)
{
    Console.WriteLine("Falha ao garantir coluna RetentionReminderSent: " + ex.Message);
}

var appointments = db.Appointments.ToList();
var appointmentsUpdated = false;
var startupCatalog = scope.ServiceProvider.GetRequiredService<ServiceCatalogService>();
foreach (var appointment in appointments)
{
    var info = startupCatalog.Get(appointment.ServiceId, includeInactive: true);
    if (info != null && (appointment.Preco != info.Price || appointment.DuracaoMinutos != info.DurationMinutes))
    {
        appointment.Preco = info.Price;
        appointment.DuracaoMinutos = info.DurationMinutes;
        appointmentsUpdated = true;
    }
}
if (appointmentsUpdated)
{
    await db.SaveChangesAsync();
}

var defaultStoreName = builder.Configuration["DefaultStore:Name"] ?? builder.Configuration["DEFAULT_STORE_NAME"] ?? "Minha Barbearia";

async Task ConsolidateClinicHairStoresAsync(AppDbContext dbContext)
{
    var candidates = await dbContext.Stores
        .IgnoreQueryFilters()
        .Where(s =>
            s.Slug == "clinica-hair" ||
            s.Slug == "clinica-hair-v2" ||
            s.Slug.StartsWith("clinica-hair-") ||
            s.Name.ToLower() == "clinica hair" ||
            s.Name.ToLower() == "clinic hair")
        .ToListAsync();

    if (candidates.Count == 0) return;

    var scored = candidates
        .Select(s => new
        {
            Store = s,
            Score =
                dbContext.Users.IgnoreQueryFilters().Count(u => u.StoreId == s.Id) +
                dbContext.Barbeiros.IgnoreQueryFilters().Count(b => b.StoreId == s.Id) +
                dbContext.Appointments.IgnoreQueryFilters().Count(a => a.StoreId == s.Id)
        })
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Store.Slug == "clinica-hair-v2")
        .ThenBy(x => x.Store.Id)
        .ToList();

    var canonical = scored.First().Store;
    var duplicates = candidates.Where(s => s.Id != canonical.Id).ToList();

    foreach (var duplicate in duplicates)
    {
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE Users SET StoreId = {0} WHERE StoreId = {1}", canonical.Id, duplicate.Id);
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE Barbeiros SET StoreId = {0} WHERE StoreId = {1}", canonical.Id, duplicate.Id);
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE Appointments SET StoreId = {0} WHERE StoreId = {1}", canonical.Id, duplicate.Id);
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE ConversationSessions SET StoreId = {0} WHERE StoreId = {1}", canonical.Id, duplicate.Id);

        if (string.IsNullOrWhiteSpace(canonical.ApiKey) && !string.IsNullOrWhiteSpace(duplicate.ApiKey)) canonical.ApiKey = duplicate.ApiKey;
        if (string.IsNullOrWhiteSpace(canonical.BackendUrl) && !string.IsNullOrWhiteSpace(duplicate.BackendUrl)) canonical.BackendUrl = duplicate.BackendUrl;
        if (string.IsNullOrWhiteSpace(canonical.BridgeUrl) && !string.IsNullOrWhiteSpace(duplicate.BridgeUrl)) canonical.BridgeUrl = duplicate.BridgeUrl;
        if (string.Equals(canonical.Plan, "Free", StringComparison.OrdinalIgnoreCase) && !string.Equals(duplicate.Plan, "Free", StringComparison.OrdinalIgnoreCase)) canonical.Plan = duplicate.Plan;

        duplicate.Slug = $"merged-{duplicate.Id}-{duplicate.Slug}";
        dbContext.Stores.Remove(duplicate);
    }

    if (string.IsNullOrWhiteSpace(canonical.Name))
        canonical.Name = defaultStoreName;
    canonical.Slug = "clinica-hair";
    canonical.IsActive = true;
    if (string.IsNullOrWhiteSpace(canonical.ApiKey)) canonical.ApiKey = apiKey;

    await dbContext.SaveChangesAsync();
}

await ConsolidateClinicHairStoresAsync(db);

async Task DeduplicateBarbersAsync(AppDbContext dbContext)
{
    var groups = await dbContext.Barbeiros
        .IgnoreQueryFilters()
        .AsNoTracking()
        .GroupBy(b => new { b.StoreId, Name = b.Nome.Trim().ToLower() })
        .Where(g => g.Count() > 1)
        .Select(g => new { g.Key.StoreId, g.Key.Name, Ids = g.Select(b => b.Id).ToList() })
        .ToListAsync();

    foreach (var group in groups)
    {
        var barbers = await dbContext.Barbeiros.IgnoreQueryFilters()
            .Where(b => group.Ids.Contains(b.Id))
            .ToListAsync();

        var primary = barbers
            .OrderByDescending(b => dbContext.Appointments.IgnoreQueryFilters().Count(a => a.BarberId == b.Id))
            .ThenBy(b => b.Id)
            .First();

        foreach (var duplicate in barbers.Where(b => b.Id != primary.Id))
        {
            await dbContext.Database.ExecuteSqlRawAsync("UPDATE Appointments SET BarberId = {0}, BarberName = {1} WHERE BarberId = {2}", primary.Id, primary.Nome, duplicate.Id);
            await dbContext.Database.ExecuteSqlRawAsync("UPDATE Users SET BarberId = {0} WHERE BarberId = {1}", primary.Id, duplicate.Id);
            await dbContext.Database.ExecuteSqlRawAsync("UPDATE ConversationSessions SET SelectedBarberId = {0}, SelectedBarberName = {1} WHERE SelectedBarberId = {2}", primary.Id, primary.Nome, duplicate.Id);
            dbContext.Barbeiros.Remove(duplicate);
        }
    }

    await dbContext.SaveChangesAsync();
}

await DeduplicateBarbersAsync(db);

// Seed users - Garantir que o admin especificamente exista
// Seed Default Store - necessario para o login do admin funcionar.
// O slug legado permanece por compatibilidade, mas o nome visivel default ja nasce comercial.
var defaultStore = await db.Stores.FirstOrDefaultAsync(s => s.Id == 1 || s.Slug == "clinica-hair");
if (defaultStore == null)
{
    defaultStore = new Store { Id = 1, Name = defaultStoreName, Slug = "clinica-hair", Plan = "Premium", IsActive = true, ApiKey = apiKey };
    db.Stores.Add(defaultStore);
    await db.SaveChangesAsync();
}

// Seed de serviços padrão para lojas que ainda não têm ServicoItem cadastrado.
// Garante que stores legado (barbearia) continuem funcionando sem migração manual.
await SeedDefaultServicosAsync(db);

async Task SeedDefaultServicosAsync(AppDbContext dbContext)
{
    var activeStores = await dbContext.Stores
        .IgnoreQueryFilters()
        .Where(s => s.IsActive)
        .ToListAsync();

    foreach (var store in activeStores)
    {
        var hasServicos = dbContext.Set<ServicoItem>().IgnoreQueryFilters().Any(s => s.StoreId == store.Id);
        if (hasServicos) continue;

        dbContext.Set<ServicoItem>().AddRange(
            SuperAdminEndpoints.BuildDefaultServicos(store.Id, store.BusinessType));
    }

    await dbContext.SaveChangesAsync();
}

// Seed de plano padrão de assinatura para lojas do tipo Barbershop que ainda não têm plano.
await SeedDefaultSubscriptionPlanAsync(db);

async Task SeedDefaultSubscriptionPlanAsync(AppDbContext dbContext)
{
    var barbershops = await dbContext.Stores
        .IgnoreQueryFilters()
        .Where(s => s.IsActive && s.BusinessType == BusinessType.Barbershop)
        .ToListAsync();

    foreach (var store in barbershops)
    {
        var hasPlan = dbContext.Set<SubscriptionPlan>().IgnoreQueryFilters().Any(p => p.StoreId == store.Id);
        if (hasPlan) continue;

        dbContext.Set<SubscriptionPlan>().Add(new SubscriptionPlan
        {
            StoreId = store.Id,
            Nome = "Plano Mensal",
            Descricao = "4 cortes por mês com desconto exclusivo",
            Preco = 100m,
            Creditos = 4,
            DuracaoDias = 30,
            Ativo = true,
            CreatedAt = DateTime.Now
        });
    }

    await dbContext.SaveChangesAsync();
}

// Seed do admin — sincroniza senha a cada startup (igual ao superadmin).
// Isso garante que o admin sempre consegue entrar com a senha definida em DEFAULT_ADMIN_PASSWORD,
// mesmo que o .env.local tenha sido regenerado ou que a senha anterior estivesse incorreta.
var defaultAdminPassword = builder.Configuration["DefaultAdmin:Password"] ?? builder.Configuration["DEFAULT_ADMIN_PASSWORD"];
var adminUser = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Username == "admin");

if (!string.IsNullOrWhiteSpace(defaultAdminPassword))
{
    if (adminUser == null)
    {
        adminUser = new User
        {
            Username = "admin",
            Role = "admin",
            StoreId = defaultStore.Id,
            PasswordHash = auth.HashPassword(defaultAdminPassword)
        };
        db.Users.Add(adminUser);
        await db.SaveChangesAsync();
        Console.WriteLine("Usuario 'admin' criado com senha de DEFAULT_ADMIN_PASSWORD.");
    }
    else
    {
        // Sempre sincroniza — garante acesso mesmo após reinstalação ou troca do .env.local
        adminUser.PasswordHash = auth.HashPassword(defaultAdminPassword);
        adminUser.StoreId = defaultStore.Id;
        await db.SaveChangesAsync();
        Console.WriteLine("Senha do usuario 'admin' sincronizada com DEFAULT_ADMIN_PASSWORD.");
    }
}
else
{
    Console.WriteLine("AVISO: DEFAULT_ADMIN_PASSWORD nao definido — usuario 'admin' nao sera criado/atualizado.");
    Console.WriteLine("       Defina DEFAULT_ADMIN_PASSWORD no .env.local e reinicie.");
}

var superadminUsername = builder.Configuration["SuperAdmin:Username"] ?? builder.Configuration["SUPERADMIN_USERNAME"];
var superadminPassword = builder.Configuration["SuperAdmin:Password"] ?? builder.Configuration["SUPERADMIN_PASSWORD"];
if (!string.IsNullOrWhiteSpace(superadminUsername) && !string.IsNullOrWhiteSpace(superadminPassword))
{
    var superUser = await db.Users.FirstOrDefaultAsync(u => u.Username == superadminUsername && u.Role == "superadmin");
    if (superUser == null)
    {
        db.Users.Add(new User { Username = superadminUsername, PasswordHash = auth.HashPassword(superadminPassword), Role = "superadmin", StoreId = 0 });
        await db.SaveChangesAsync();
        Console.WriteLine($"Superadmin '{superadminUsername}' criado com sucesso.");
    }
    else
    {
        // Força a sincronização da senha com o que está no .bat para evitar 401
        superUser.PasswordHash = auth.HashPassword(superadminPassword);
        await db.SaveChangesAsync();
        Console.WriteLine($"Senha do Superadmin '{superadminUsername}' sincronizada.");
    }
}

var defaultStaffPassword = builder.Configuration["DefaultStaff:Password"] ?? builder.Configuration["DEFAULT_STAFF_PASSWORD"];
if (!string.IsNullOrWhiteSpace(defaultStaffPassword) && !await db.Users.AnyAsync(u => u.Role == "barbeiro"))
{
    db.Users.Add(new User { Username = "barbeiro", PasswordHash = auth.HashPassword(defaultStaffPassword), Role = "barbeiro", StoreId = 1 });
    db.Users.Add(new User { Username = "recepcao", PasswordHash = auth.HashPassword(defaultStaffPassword), Role = "recepcao", StoreId = 1 });
    await db.SaveChangesAsync();
}

// Seed da loja principal e admin padrao.
var itamarStore = await db.Stores.FirstOrDefaultAsync(s => s.Slug == "clinica-hair");
if (itamarStore == null)
{
    itamarStore = new Store { Name = defaultStoreName, Slug = "clinica-hair", Plan = "Premium", IsActive = true, ApiKey = apiKey };
    db.Stores.Add(itamarStore);
    await db.SaveChangesAsync();
    Console.WriteLine($"Loja '{defaultStoreName}' criada.");
}
else
{
    itamarStore.Plan = string.IsNullOrWhiteSpace(itamarStore.Plan) ? "Premium" : itamarStore.Plan;
    itamarStore.ApiKey = string.IsNullOrWhiteSpace(itamarStore.ApiKey) ? apiKey : itamarStore.ApiKey;
    await db.SaveChangesAsync();
}

var defaultOwnerUsername = builder.Configuration["DefaultOwner:Username"] ?? builder.Configuration["DEFAULT_OWNER_USERNAME"] ?? "Itamar";
var defaultOwnerPassword = builder.Configuration["DefaultOwner:Password"] ?? builder.Configuration["DEFAULT_OWNER_PASSWORD"] ?? defaultAdminPassword;
var itamarUser = await db.Users.FirstOrDefaultAsync(u => u.Username == defaultOwnerUsername);
if (string.IsNullOrWhiteSpace(defaultOwnerPassword))
{
    Console.WriteLine("AVISO: DEFAULT_OWNER_PASSWORD nao definido — usuario admin da loja nao sera criado/atualizado.");
}
else if (itamarUser == null)
{
    db.Users.Add(new User { 
        Username = defaultOwnerUsername, 
        PasswordHash = auth.HashPassword(defaultOwnerPassword), 
        Role = "admin", 
        StoreId = itamarStore.Id 
    });
    await db.SaveChangesAsync();
    Console.WriteLine($"Admin '{defaultOwnerUsername}' criado.");
}
else
{
    itamarUser.PasswordHash = auth.HashPassword(defaultOwnerPassword);
    itamarUser.StoreId = itamarStore.Id;
    await db.SaveChangesAsync();
}

var seedBusinessHours = scope.ServiceProvider.GetRequiredService<BusinessHours>();

var itamarBarber = await db.Barbeiros.FirstOrDefaultAsync(b => b.StoreId == itamarStore.Id && b.Nome == "Itamar");
if (itamarBarber == null)
{
    itamarBarber = new Barbeiro
    {
        StoreId = itamarStore.Id,
        Nome = "Itamar",
        Ativo = true,
        Cor = "#2563eb",
        Especialidade = "Corte e barba",
        WorkStart = seedBusinessHours.OpeningTime,
        WorkEnd = seedBusinessHours.ClosingTime
    };
    db.Barbeiros.Add(itamarBarber);
    await db.SaveChangesAsync();
}
if (itamarUser != null && itamarUser.BarberId == null)
{
    itamarUser.BarberId = itamarBarber.Id;
    await db.SaveChangesAsync();
}

var kauanBarber = await db.Barbeiros.FirstOrDefaultAsync(b => b.StoreId == itamarStore.Id && b.Nome == "Kauan Vitor");
if (kauanBarber == null)
{
    kauanBarber = new Barbeiro
    {
        StoreId = itamarStore.Id,
        Nome = "Kauan Vitor",
        Ativo = true,
        Cor = "#16a34a",
        Especialidade = "Corte e acabamento",
        WorkStart = seedBusinessHours.OpeningTime,
        WorkEnd = seedBusinessHours.ClosingTime
    };
    db.Barbeiros.Add(kauanBarber);
    await db.SaveChangesAsync();
}

var kauanUser = await db.Users.FirstOrDefaultAsync(u => u.Username == "Kauan Vitor" && u.StoreId == itamarStore.Id);
if (kauanUser == null)
{
    db.Users.Add(new User
    {
        Username = "Kauan Vitor",
        PasswordHash = auth.HashPassword("kauanenicolly"),
        Role = "barbeiro",
        StoreId = itamarStore.Id,
        BarberId = kauanBarber.Id
    });
    await db.SaveChangesAsync();
}
else if (kauanUser.BarberId == null)
{
    kauanUser.BarberId = kauanBarber.Id;
    await db.SaveChangesAsync();
}

await DefaultAutomationSeeder.EnsureSeededAsync(db);

// Muda para 0.0.0.0 para permitir que outros dispositivos (celular) acessem a API
app.Run(builder.Configuration["ASPNETCORE_URLS"] ?? "http://0.0.0.0:5000");

record SystemConfigRow(string Key, string Value);

