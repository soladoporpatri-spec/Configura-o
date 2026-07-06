namespace WhatsAppBot.Worker.Endpoints;

public static class EndpointAuth
{
    public static bool IsAuthenticated(HttpContext ctx, string apiKey) =>
        (ctx.Request.Headers["X-API-KEY"] == apiKey && IsLoopback(ctx)) ||
        (ctx.User.Identity?.IsAuthenticated ?? false);

    public static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is not null && System.Net.IPAddress.IsLoopback(ip);
    }
}
