using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Gerencia o histórico de veículos de clientes (lavajato e similares).
/// Persiste placa + modelo para reutilização em atendimentos futuros —
/// o bot oferece uma poll com os veículos já registrados e o cliente
/// seleciona em vez de redigitar a placa.
/// </summary>
public class VehicleService(AppDbContext db)
{
    /// <summary>
    /// Retorna os veículos mais recentes do cliente nesta loja (max 3,
    /// para caber confortavelmente numa poll do WhatsApp).
    /// Ordenados por último uso — o veículo mais recente aparece primeiro.
    /// </summary>
    public async Task<List<ClientVehicle>> GetClientVehiclesAsync(
        string phone, CancellationToken ct = default)
        => await db.ClientVehicles
            .Where(v => v.PhoneNumber == phone)
            .OrderByDescending(v => v.LastUsed)
            .Take(3)
            .ToListAsync(ct);

    /// <summary>
    /// Cria ou atualiza o registro de veículo para este cliente.
    /// <list type="bullet">
    ///   <item>Se a placa já existe: atualiza <c>Model</c> (se fornecido) e <c>LastUsed</c>.</item>
    ///   <item>Se é nova: cria um novo registro.</item>
    /// </list>
    /// A placa é normalizada (maiúsculas, sem hífens/espaços) antes da comparação.
    /// </summary>
    public async Task<ClientVehicle> UpsertAsync(
        string phone, string plate, string? model, CancellationToken ct = default)
    {
        var normalizedPlate = NormalizePlate(plate);
        var existing = await db.ClientVehicles
            .FirstOrDefaultAsync(v => v.PhoneNumber == phone && v.Plate == normalizedPlate, ct);

        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(model)) existing.Model = model.Trim();
            existing.LastUsed = DateTime.UtcNow;
        }
        else
        {
            existing = new ClientVehicle
            {
                StoreId     = db.TenantId,
                PhoneNumber = phone,
                Plate       = normalizedPlate,
                Model       = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                LastUsed    = DateTime.UtcNow
            };
            db.ClientVehicles.Add(existing);
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    // ── Helpers estáticos (reutilizáveis sem instância) ──────────────────────

    /// <summary>
    /// Normaliza placa: maiúsculas, remove hífens e espaços.
    /// "abc-1234" → "ABC1234" · "abc 1 d23" → "ABC1D23"
    /// </summary>
    public static string NormalizePlate(string plate)
        => plate.Trim().ToUpperInvariant()
                .Replace("-", "")
                .Replace(" ", "");

    /// <summary>
    /// Label de exibição: "ABC1234" quando sem modelo,
    /// "ABC1234 — Civic 2020 Prata" quando modelo disponível.
    /// </summary>
    public static string FormatLabel(ClientVehicle v)
        => string.IsNullOrWhiteSpace(v.Model)
            ? v.Plate
            : $"{v.Plate} — {v.Model}";

    /// <summary>
    /// Tenta extrair placa e modelo de um texto livre do cliente.
    /// Convenção: primeira "palavra" = placa, restante = modelo.
    /// Ex.: "ABC-1234 Civic Prata" → ("ABC1234", "Civic Prata")
    /// Retorna null para modelo se o texto tiver só uma palavra.
    /// </summary>
    public static (string Plate, string? Model) ParseFreeText(string text)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var plate = NormalizePlate(parts[0]);
        var model = parts.Length > 1 ? parts[1].Trim() : null;
        return (plate, model);
    }
}
