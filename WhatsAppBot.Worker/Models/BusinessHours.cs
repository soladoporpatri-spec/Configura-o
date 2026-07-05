using System;

namespace WhatsAppBot.Worker.Models;

public class BusinessHours
{
    public TimeSpan OpeningTime { get; set; }
    public TimeSpan ClosingTime { get; set; }
}