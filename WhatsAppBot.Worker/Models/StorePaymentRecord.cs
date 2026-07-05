namespace WhatsAppBot.Worker.Models;

public enum StorePaymentStatus
{
    Pending = 0,
    Paid = 1,
    Cancelled = 2,
    Failed = 3
}

public class StorePaymentRecord
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Plan { get; set; } = "";
    public decimal Amount { get; set; }
    public string PaymentMode { get; set; } = "manual_pix";
    public string Provider { get; set; } = "manual";
    public string? ProviderReference { get; set; }
    public StorePaymentStatus Status { get; set; } = StorePaymentStatus.Paid;
    public DateTime PaidUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ConfirmedAt { get; set; }
    public string ConfirmedBy { get; set; } = "Superadmin";
    public string? Notes { get; set; }
}
