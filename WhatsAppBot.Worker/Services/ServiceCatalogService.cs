using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Item do catálogo de serviços. Id é um inteiro simples — para lojas legado (Barbershop)
/// os IDs 1-6 coincidem com os valores do enum TipoServico.
/// </summary>
public record ServiceCatalogItem(int Id, string Name, int DurationMinutes, decimal Price, bool Active, bool OccupiesSlot = true);

public class ServiceCatalogService
{
    private readonly AppDbContext _db;

    public ServiceCatalogService(AppDbContext db)
    {
        _db = db;
    }

    // ─── API pública ───────────────────────────────────────────────────────────

    public List<ServiceCatalogItem> GetAll(bool includeInactive = false)
    {
        var dbItems = _db.Set<ServicoItem>()
            .AsNoTracking()
            .OrderBy(s => s.Ordem)
            .ThenBy(s => s.Id)
            .ToList();

        if (dbItems.Count > 0)
        {
            return dbItems
                .Where(s => includeInactive || s.Ativo)
                .Select(s => new ServiceCatalogItem(s.Id, s.Nome, s.DuracaoMinutos, s.Preco, s.Ativo, s.OcupaHorario))
                .ToList();
        }

        // Fallback legacy (ServicoInfo = serviços de BARBEARIA) só para lojas barbearia/legado.
        // Um lava-jato sem ServicoItem NÃO deve exibir serviços de barbearia.
        return ShouldUseLegacyBarbershopFallback() ? GetAllFromConfig(includeInactive) : new();
    }

    /// <summary>
    /// Decide se o fallback de serviços fixos (ServicoInfo, ramo barbearia) é aplicável
    /// para a loja atual (tenant). Evita vazar serviços de barbearia para lava-jato.
    /// </summary>
    private bool ShouldUseLegacyBarbershopFallback()
    {
        try
        {
            var storeId = _db.TenantId;
            if (storeId <= 0) return true; // contexto global/legado
            var store = _db.Set<Store>().AsNoTracking().FirstOrDefault(s => s.Id == storeId);
            return store == null || store.BusinessType == BusinessType.Barbershop;
        }
        catch { return true; }
    }

    public ServiceCatalogItem? Get(int id, bool includeInactive = false)
    {
        var dbItem = _db.Set<ServicoItem>().AsNoTracking().FirstOrDefault(s => s.Id == id);
        if (dbItem != null)
            return (includeInactive || dbItem.Ativo)
                ? new ServiceCatalogItem(dbItem.Id, dbItem.Nome, dbItem.DuracaoMinutos, dbItem.Preco, dbItem.Ativo, dbItem.OcupaHorario)
                : null;

        // Fallback legacy só para barbearia/legado (evita serviço de barbearia em lava-jato)
        return ShouldUseLegacyBarbershopFallback() ? GetFromConfig(id, includeInactive) : null;
    }

    // Overload de compatibilidade para código que ainda passa TipoServico
    public ServiceCatalogItem? Get(TipoServico service, bool includeInactive = false)
        => Get((int)service, includeInactive);

    public bool IsActive(TipoServico service) => Get((int)service) != null;

    // ─── Helpers legado (SystemConfigs + ServicoInfo) ──────────────────────────

    private List<ServiceCatalogItem> GetAllFromConfig(bool includeInactive)
    {
        var configs = _db.SystemConfigs.AsNoTracking().ToDictionary(c => c.Key, c => c.Value);
        return ServicoInfo.Servicos
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => BuildItemFromConfig(kv.Key, kv.Value, configs))
            .Where(item => includeInactive || item.Active)
            .ToList();
    }

    private ServiceCatalogItem? GetFromConfig(int id, bool includeInactive)
    {
        var tipoServico = (TipoServico)id;
        if (!ServicoInfo.Servicos.TryGetValue(tipoServico, out var defaults)) return null;
        var configs = _db.SystemConfigs.AsNoTracking().ToDictionary(c => c.Key, c => c.Value);
        var item = BuildItemFromConfig(tipoServico, defaults, configs);
        return includeInactive || item.Active ? item : null;
    }

    private static ServiceCatalogItem BuildItemFromConfig(
        TipoServico id,
        (string Nome, int DuracaoMinutos, decimal Preco) defaults,
        IReadOnlyDictionary<string, string> configs)
    {
        var numericId = (int)id;

        var name = configs.TryGetValue($"Service_{numericId}_Name", out var cfgName) && !string.IsNullOrWhiteSpace(cfgName)
            ? cfgName.Trim()
            : defaults.Nome;

        var duration = configs.TryGetValue($"DurationService_{numericId}", out var cfgDur) &&
                       int.TryParse(cfgDur, out var parsedDur) && parsedDur is >= 5 and <= 480
            ? parsedDur
            : defaults.DuracaoMinutos;

        var price = configs.TryGetValue($"Service_{numericId}_Price", out var cfgPrice) &&
                    decimal.TryParse(cfgPrice.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice) &&
                    parsedPrice >= 0
            ? parsedPrice
            : defaults.Preco;

        var active = !configs.TryGetValue($"Service_{numericId}_Active", out var cfgActive) ||
                     !string.Equals(cfgActive, "false", StringComparison.OrdinalIgnoreCase);

        return new ServiceCatalogItem(numericId, name, duration, price, active);
    }
}
