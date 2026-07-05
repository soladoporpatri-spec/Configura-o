using System.Text.Json.Serialization;

namespace WhatsAppBot.Worker.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BusinessType
{
    Barbershop = 0,
    CarWash = 1,
    Pizzeria = 2,
    ComputerOptimization = 3,
}
