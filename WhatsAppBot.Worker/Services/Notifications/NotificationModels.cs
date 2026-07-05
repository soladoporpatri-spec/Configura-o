namespace WhatsAppBot.Worker.Services;

public enum NotificationType
{
    NewAppointment,
    ConfirmedAppointment,
    CancelledAppointment,
    ReminderAppointment,
    NoShow,
    RescheduledAppointment,
    SystemAlert,
    IncomingMessage
}

public enum NotificationPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public class NotificationMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public NotificationPriority Priority { get; set; } = NotificationPriority.Medium;
    public bool Read { get; set; } = false;
}
