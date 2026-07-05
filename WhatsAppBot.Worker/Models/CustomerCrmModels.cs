namespace WhatsAppBot.Worker.Models;

public class CustomerProfile
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerKey { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ManualStatus { get; set; }
    public bool IsBlocked { get; set; } = false;
    public string? InternalNotes { get; set; }
    public string? Preferences { get; set; }
    public string? PreferredService { get; set; }
    public string? PreferredProfessional { get; set; }
    public string? BestTime { get; set; }
    public int? ReturnFrequencyDays { get; set; }
    public string? ContactPreference { get; set; }
    public DateTime? Birthday { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CustomerTag
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#64748b";
    public bool IsSystem { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CustomerTagAssignment
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerKey { get; set; } = "";
    public int CustomerTagId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CustomerEvent
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerKey { get; set; } = "";
    public string Type { get; set; } = "note";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int? RelatedAppointmentId { get; set; }
    public string CreatedBy { get; set; } = "Dashboard";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool VisibleToCustomer { get; set; } = false;
}

public class CustomerReminder
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public DateTime DueDate { get; set; }
    public string Status { get; set; } = "pendente";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
