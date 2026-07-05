using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.Modules;

public record TenantModuleStatus(
    string Key,
    string Name,
    string Description,
    string MinimumPlan,
    bool Enabled,
    bool IncludedInPlan,
    bool Overridden,
    bool SegmentSupported);

public class FeatureAccessService
{
    private readonly AppDbContext _db;
    private readonly ITenantService _tenantService;
    private readonly ModuleCatalog _catalog;

    public FeatureAccessService(AppDbContext db, ITenantService tenantService, ModuleCatalog catalog)
    {
        _db = db;
        _tenantService = tenantService;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<TenantModuleStatus>> GetModulesAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantService.GetTenantId();
        var store = await GetTenantStoreAsync(tenantId, cancellationToken);
        var plan = PlanCatalog.Normalize(store?.Plan);
        var businessType = store?.BusinessType ?? BusinessType.Barbershop;
        var overrides = await GetModuleOverridesAsync(tenantId, cancellationToken);

        return _catalog.Modules.Select(module =>
        {
            var segmentSupported = module.Supports(businessType);
            var included = module.EnabledByDefault && segmentSupported && _catalog.PlanAllows(plan, module.MinimumPlan);
            var hasOverride = overrides.TryGetValue(module.Key, out var overrideEnabled);
            return new TenantModuleStatus(
                module.Key,
                module.Name,
                module.Description,
                module.MinimumPlan,
                segmentSupported && (hasOverride ? overrideEnabled : included),
                included,
                hasOverride,
                segmentSupported);
        }).ToList();
    }

    public async Task<bool> IsEnabledAsync(string moduleKey, CancellationToken cancellationToken = default)
    {
        var modules = await GetModulesAsync(cancellationToken);
        return modules.FirstOrDefault(m => string.Equals(m.Key, moduleKey, StringComparison.OrdinalIgnoreCase))?.Enabled ?? false;
    }

    public async Task<IReadOnlyList<TenantModuleStatus>> GetModulesForStoreAsync(int storeId, CancellationToken cancellationToken = default)
    {
        var store = await GetTenantStoreAsync(storeId, cancellationToken);
        if (store == null) return [];

        var plan = PlanCatalog.Normalize(store.Plan);
        var overrides = await GetModuleOverridesAsync(storeId, cancellationToken);

        return _catalog.Modules.Select(module =>
        {
            var segmentSupported = module.Supports(store.BusinessType);
            var included = module.EnabledByDefault && segmentSupported && _catalog.PlanAllows(plan, module.MinimumPlan);
            var hasOverride = overrides.TryGetValue(module.Key, out var overrideEnabled);
            return new TenantModuleStatus(
                module.Key,
                module.Name,
                module.Description,
                module.MinimumPlan,
                segmentSupported && (hasOverride ? overrideEnabled : included),
                included,
                hasOverride,
                segmentSupported);
        }).ToList();
    }

    public async Task<TenantModuleStatus?> SetModuleOverrideAsync(
        int storeId,
        string moduleKey,
        bool? enabled,
        CancellationToken cancellationToken = default)
    {
        var module = _catalog.Find(moduleKey);
        if (module == null) return null;

        var normalizedKey = ModuleCatalog.NormalizeKey(module.Key);
        var configKey = StoreModuleConfigKey(storeId, normalizedKey);
        var existing = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.Key == configKey, cancellationToken);

        if (enabled.HasValue)
        {
            if (existing == null)
            {
                _db.SystemConfigs.Add(new SystemConfig { Key = configKey, Value = enabled.Value ? "true" : "false" });
            }
            else
            {
                existing.Value = enabled.Value ? "true" : "false";
            }
        }
        else if (existing != null)
        {
            _db.SystemConfigs.Remove(existing);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (await GetModulesForStoreAsync(storeId, cancellationToken))
            .FirstOrDefault(m => string.Equals(m.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Store?> GetTenantStoreAsync(int tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == 0)
        {
            return new Store
            {
                Id = 0,
                Name = "Superadmin",
                Plan = PlanCatalog.Enterprise,
                BusinessType = BusinessType.Barbershop
            };
        }

        return await _db.Stores.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == tenantId, cancellationToken);
    }

    private async Task<Dictionary<string, bool>> GetModuleOverridesAsync(int tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _db.Database
                .SqlQueryRaw<SystemConfigValueRow>("SELECT Key, Value FROM SystemConfigs WHERE Key LIKE 'Module_%_Enabled'")
                .ToListAsync(cancellationToken);
            var storeRows = new List<SystemConfigValueRow>();
            if (tenantId > 0)
            {
                var storePattern = $"Store_{tenantId}_Module_%_Enabled";
                storeRows = await _db.Database
                    .SqlQueryRaw<SystemConfigValueRow>("SELECT Key, Value FROM SystemConfigs WHERE Key LIKE {0}", storePattern)
                    .ToListAsync(cancellationToken);
            }

            var result = rows
                .Select(row => new { Key = NormalizeLegacyOverrideKey(row.Key), Enabled = ParseEnabled(row.Value) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(item => item.Key!, item => item.Enabled, StringComparer.OrdinalIgnoreCase);

            foreach (var row in storeRows)
            {
                var key = NormalizeStoreOverrideKey(row.Key, tenantId);
                if (!string.IsNullOrWhiteSpace(key))
                    result[key] = ParseEnabled(row.Value);
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, bool>();
        }
    }

    public static string StoreModuleConfigKey(int storeId, string moduleKey)
        => $"Store_{storeId}_Module_{ModuleCatalog.NormalizeKey(moduleKey)}_Enabled";

    private static string? NormalizeLegacyOverrideKey(string key)
        => key.Replace("Module_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_Enabled", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();

    private static string? NormalizeStoreOverrideKey(string key, int storeId)
        => key.Replace($"Store_{storeId}_Module_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_Enabled", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();

    private static bool ParseEnabled(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
           || value == "1"
           || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}

file record SystemConfigValueRow(string Key, string Value);
