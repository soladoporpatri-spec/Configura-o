using QRCoder;

namespace WhatsAppBot.Worker.Endpoints;

public static class PixEndpoints
{
    public static IEndpointRouteBuilder MapPixEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/public/pix/qrcode", (HttpContext ctx, string payload) =>
        {
            if (string.IsNullOrWhiteSpace(payload))
                return Results.BadRequest(new { error = "Payload PIX ausente." });

            if (payload.Length > 512)
                return Results.BadRequest(new { error = "Payload PIX muito grande." });

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data).GetGraphic(8);

            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(png, "image/png");
        });

        return app;
    }
}
