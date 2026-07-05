using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Serviço centralizado para leitura de configurações por loja.
/// Prioriza a chave específica da loja (<c>Store_{tenantId}_{key}</c>) antes da global (<c>{key}</c>).
/// Garante que cada tenant veja apenas suas próprias configurações, com fallback para o global.
/// </summary>
public class StoreSettingsService
{
    private readonly AppDbContext _db;
    private Dictionary<string, string>? _cache;

    public StoreSettingsService(AppDbContext db) => _db = db;

    // ── Carregamento ────────────────────────────────────────────────────────────

    /// <summary>Carrega (ou retorna do cache de instância) todos os SystemConfigs.</summary>
    private Dictionary<string, string> GetConfigs()
    {
        if (_cache != null) return _cache;
        _cache = _db.SystemConfigs.AsNoTracking().ToDictionary(c => c.Key, c => c.Value);
        return _cache;
    }

    // ── API pública ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna o valor da configuração, priorizando a chave por-loja sobre a global.
    /// Retorna <paramref name="fallback"/> se nenhuma chave for encontrada.
    /// </summary>
    public string? GetString(string key, string? fallback = null)
    {
        var configs = GetConfigs();
        var storeId = _db.TenantId;
        return configs.GetValueOrDefault($"Store_{storeId}_{key}")
            ?? configs.GetValueOrDefault(key)
            ?? fallback;
    }

    /// <summary>
    /// Retorna bool da configuração. Valor "true" (case-insensitive) = true; qualquer outro = false.
    /// </summary>
    public bool GetBool(string key, bool fallback = false)
    {
        var value = GetString(key);
        if (value == null) return fallback;
        return string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retorna int da configuração. Retorna <paramref name="fallback"/> se inválido ou ausente.
    /// </summary>
    public int GetInt(string key, int fallback = 0)
    {
        var value = GetString(key);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Invalida o cache de instância, forçando releitura do banco na próxima chamada.
    /// Use após salvar configurações dentro do mesmo escopo de DI.
    /// </summary>
    public void Invalidate() => _cache = null;
}
