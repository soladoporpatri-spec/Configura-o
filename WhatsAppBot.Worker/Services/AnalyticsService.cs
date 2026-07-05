using WhatsAppBot.Worker.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace WhatsAppBot.Worker.Services;

public class AnalyticsService
{
    private readonly AppDbContext _db;
    private readonly ServiceCatalogService _catalog;
    private readonly ILogger<AnalyticsService> _logger;
    private readonly IMemoryCache _cache;

    public AnalyticsService(AppDbContext db, ServiceCatalogService catalog, ILogger<AnalyticsService> logger, IMemoryCache cache)
    {
        _db = db;
        _catalog = catalog;
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Gera chave de cache isolada por tenant, evitando que analytics de uma loja
    /// sejam retornados para outra (bug de vazamento de dados multi-tenant).
    /// </summary>
    private string BuildCacheKey() => $"analytics-tenant-{_db.TenantId}";

    /// <summary>Invalida o cache de analytics da loja atual — chamar após mutações relevantes.</summary>
    public void InvalidateCache() => _cache.Remove(BuildCacheKey());

    public async Task<AnalyticsData> GetAnalyticsAsync()
    {
        var cacheKey = BuildCacheKey();
        if (_cache.TryGetValue(cacheKey, out AnalyticsData? data))
            return data!;

        // Cada tenant tem seu próprio AppDbContext.TenantId que ativa o HasQueryFilter global,
        // garantindo que _db.Appointments retorne APENAS dados desta loja.
        var appointments = await _db.Appointments.AsNoTracking().ToListAsync();
        var today = DateTime.Today;
        var thisMonth = new DateTime(today.Year, today.Month, 1);

        data = new AnalyticsData
        {
            TotalAppointments  = appointments.Count,
            TodayAppointments  = appointments.Count(a => a.DateTime.Date == today),
            MonthAppointments  = appointments.Count(a => a.DateTime >= thisMonth),
            TotalRevenue       = appointments.Sum(a => a.Preco),
            MonthRevenue       = appointments.Where(a => a.DateTime >= thisMonth).Sum(a => a.Preco),
            PopularServices    = appointments
                .GroupBy(a => a.ServiceId)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => new ServiceStats
                {
                    // Usa o catálogo para obter o nome real — funciona com qualquer ID,
                    // não apenas os 6 do enum TipoServico.
                    Service  = _catalog.Get(g.Key, includeInactive: true)?.Name ?? $"Serviço #{g.Key}",
                    Count    = g.Count(),
                    Revenue  = g.Sum(a => a.Preco)
                })
                .ToList(),
            AttendanceRate = appointments.Any()
                ? (double)appointments.Count(a => a.PresencaConfirmada) / appointments.Count * 100
                : 0
        };

        _cache.Set(cacheKey, data, TimeSpan.FromMinutes(10));
        _logger.LogInformation("Analytics gerados (Tenant {TenantId}): {Total} agendamentos, R$ {Revenue}",
            _db.TenantId, data.TotalAppointments, data.TotalRevenue);
        return data;
    }
}

public class AnalyticsData
{
    public int TotalAppointments { get; set; }
    public int TodayAppointments { get; set; }
    public int MonthAppointments { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal MonthRevenue { get; set; }
    public List<ServiceStats> PopularServices { get; set; } = new();
    public double AttendanceRate { get; set; }
}

public class ServiceStats
{
    public string Service { get; set; } = "";
    public int Count { get; set; }
    public decimal Revenue { get; set; }
}
