namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Histórico de veículos de um cliente em uma loja (lavajato e similares).
/// Persiste placa + modelo para o bot oferecer re-seleção rápida em atendimentos futuros,
/// eliminando a necessidade de redigitar a placa a cada visita.
/// Isolado por tenant: <see cref="StoreId"/> + HasQueryFilter garantem que uma loja
/// nunca enxerga veículos de outra.
/// </summary>
public class ClientVehicle
{
    public int Id { get; set; }

    /// <summary>ID da loja — chave de isolamento multi-tenant.</summary>
    public int StoreId { get; set; }

    /// <summary>Número de WhatsApp do cliente (chave de lookup, sem formatação).</summary>
    public string PhoneNumber { get; set; } = "";

    /// <summary>
    /// Placa normalizada (maiúsculas, sem hífen nem espaços): "ABC1234" ou "ABC1D23".
    /// Normalização feita por <see cref="Services.VehicleService.NormalizePlate"/>.
    /// </summary>
    public string Plate { get; set; } = "";

    /// <summary>
    /// Modelo/cor/descrição livre informada pelo cliente (ex.: "Civic 2020 Prata").
    /// Atualizado a cada uso se o cliente fornecer nova descrição.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Data/hora UTC do último atendimento com este veículo.
    /// Ordena os veículos na poll — o mais recente aparece primeiro.
    /// </summary>
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}
