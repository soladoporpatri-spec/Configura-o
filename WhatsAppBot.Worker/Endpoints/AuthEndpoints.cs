using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, string defaultBridgeUrl)
    {
        app.MapPost("/api/auth/login", async (AuthService auth, LoginRequest req, AppDbContext db) =>
        {
            var user = await auth.AuthenticateAsync(req.Username, req.Password);
            if (user == null) return Results.Unauthorized();

            Store? store = null;
            if (user.Role != "superadmin")
            {
                store = await db.Stores.FindAsync(user.StoreId);
                var status = StoreAccessPolicy.Evaluate(store);
                if (!status.CanOperate)
                {
                    return Results.Json(new { error = status.Message, reason = status.Reason }, statusCode: 403);
                }
            }

            if (user.Role == "barbeiro" && user.BarberId.HasValue)
            {
                // IgnoreQueryFilters: no momento do login db.TenantId ainda não está setado.
                // O filtro manual && b.StoreId == user.StoreId garante o isolamento.
                db.TenantId = user.StoreId;
                var barber = await db.Set<Barbeiro>()
                    .FirstOrDefaultAsync(b => b.Id == user.BarberId!.Value && b.StoreId == user.StoreId);

                if (barber == null || !barber.Ativo)
                {
                    return Results.Json(new { error = "Seu acesso profissional esta desativado. Entre em contato com o administrador." }, statusCode: 403);
                }
            }

            if (user.Is2FAEnabled)
            {
                var tempToken = auth.GenerateJwtToken(user);
                return Results.Ok(new { requires2FA = true, tempToken });
            }

            var token = auth.GenerateJwtToken(user);
            return Results.Ok(new
            {
                token,
                role = user.Role,
                barberId = user.BarberId,
                storeId = user.StoreId,
                storeName = store?.Name ?? "Administracao Global",
                // Tipo do estabelecimento — o dashboard adapta termos (barbearia x lava-jato).
                businessType = (store?.BusinessType ?? BusinessType.Barbershop).ToString(),
                bridgeUrl = store?.BridgeUrl
            });
        });

        app.MapPost("/api/auth/recover", async (AuthService auth, WhatsAppClient whatsapp, AppDbContext db, [FromBody] RecoverRequest req) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);

            if (user == null || string.IsNullOrEmpty(user.PhoneNumber))
            {
                return Results.Ok(new { message = "Se o usuario existir e tiver um telefone cadastrado, a senha sera enviada." });
            }

            var newPassword = Random.Shared.Next(100000, 999999).ToString();
            user.PasswordHash = auth.HashPassword(newPassword);

            await db.SaveChangesAsync();

            var store = await db.Stores.FindAsync(user.StoreId);
            var bridgeUrl = store?.BridgeUrl ?? defaultBridgeUrl;
            var storeName = store?.Name ?? "Sistema";
            var msg = $"*Recuperacao de Acesso - {storeName}*\n\nOla {user.Username}, sua nova senha temporaria e:\n\n*{newPassword}*\n\nRecomendamos trocar essa senha apos o login.";

            try
            {
                await whatsapp.SendAsync(bridgeUrl, user.PhoneNumber, msg);
                return Results.Ok(new { message = "Senha enviada com sucesso via WhatsApp." });
            }
            catch
            {
                return Results.Problem("Erro ao enviar mensagem para o WhatsApp.");
            }
        });

        return app;
    }
}

public record LoginRequest(string Username, string Password);
public record RecoverRequest(string Username);
