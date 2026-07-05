using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker.Endpoints;

public static class EndpointTenantGuard
{
    public static async Task<(IResult? Error, int StoreId, Store? Store)> RequireOperationalStoreAsync(
        AppDbContext db,
        ITenantService tenantService,
        CancellationToken cancellationToken = default)
    {
        var storeId = tenantService.GetTenantId();
        if (storeId <= 0)
        {
            return (Results.BadRequest(new { error = "Informe uma loja valida para esta operacao." }), storeId, null);
        }

        var store = await db.Stores
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == storeId, cancellationToken);

        var status = StoreAccessPolicy.Evaluate(store);
        if (!status.CanOperate)
        {
            return (Results.Json(new { error = status.Message, reason = status.Reason }, statusCode: 403), storeId, store);
        }

        return (null, storeId, store);
    }
}
