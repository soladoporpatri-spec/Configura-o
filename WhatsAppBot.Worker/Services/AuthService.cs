using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using BCryptNet = BCrypt.Net.BCrypt;

namespace WhatsAppBot.Worker.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public string HashPassword(string password)
    {
        // O BCrypt gera o sal automaticamente e o armazena junto com o hash resultante.
        return BCryptNet.HashPassword(password);
    }

    public bool VerifyPassword(string password, string hash)
    {
        try
        {
            // O método Verify extrai o sal do hash e valida a senha.
            return BCryptNet.Verify(password, hash);
        }
        catch
        {
            // Se o hash for inválido ou não estiver no formato BCrypt, nega o acesso
            return false;
        }
    }

    public async Task<User?> AuthenticateAsync(string username, string password)
    {
        // IgnoreQueryFilters: o login ocorre antes de qualquer JWT, então não há tenant
        // definido no contexto. Usamos IgnoreQueryFilters para garantir que usuários de
        // qualquer loja sejam encontrados, independente do TenantId atual do DbContext.
        var user = await _db.Users
            .IgnoreQueryFilters()
            .OrderByDescending(u => u.Role == "admin")
            .FirstOrDefaultAsync(u => u.Username == username);

        if (user == null) return null;

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            // Hash vazio indica usuário sem senha definida — nega acesso com log claro
            Console.WriteLine($"[Auth] AVISO: usuario '{username}' existe mas sem PasswordHash. Redefina DEFAULT_ADMIN_PASSWORD.");
            return null;
        }

        if (!VerifyPassword(password, user.PasswordHash))
            return null;

        return user;
    }

    public string GenerateJwtToken(User user)
    {
        var secret = _config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException("JWT Secret is not configured.");
        }

        var storeName = user.Role == "superadmin"
            ? "Administracao Global"
            : _db.Stores
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.Id == user.StoreId)
                .Select(s => s.Name)
                .FirstOrDefault() ?? "Minha Barbearia";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("StoreId", user.StoreId.ToString()),
            new Claim("StoreName", storeName)
        };

        if (user.BarberId.HasValue)
        {
            claims.Add(new Claim("BarberId", user.BarberId.Value.ToString()));
        }

        var token = new JwtSecurityToken(
            issuer: "BarbeariaAPI",
            audience: "BarbeariaDashboard",
            claims: claims,
            expires: DateTime.Now.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
