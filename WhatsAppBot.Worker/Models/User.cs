using System.ComponentModel.DataAnnotations;

namespace WhatsAppBot.Worker.Models;

public class User
{
    public int Id { get; set; }
    [Required]
    public string Username { get; set; } = "";
    [Required]
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "funcionario"; // superadmin, admin, funcionario
    public bool Is2FAEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public int? BarberId { get; set; }
    public string? PhoneNumber { get; set; }
    // No arquivo WhatsAppBot.Worker/Models/User.cs
    public int StoreId { get; set; } = 1;

}