namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Cadastro leve do computador/notebook atendido. Nao e inventario de hardware.
/// </summary>
public class OptimizationDevice
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerName { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string DeviceType { get; set; } = "Desktop";
    public string OperatingSystem { get; set; } = "Windows 11";
    public string? Processor { get; set; }
    public string? Gpu { get; set; }
    public int? RamGb { get; set; }
    public string StorageType { get; set; } = "Nao informado";
    public string MainUse { get; set; } = "Uso geral";
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;
}
