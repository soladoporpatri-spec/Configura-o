using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Services;
using Microsoft.EntityFrameworkCore;

namespace WhatsAppBot.Worker.Middleware;

public class TenantSecurityMiddleware
{
    private readonly RequestDelegate _next;

    public TenantSecurityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantService tenantService, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var storeId = tenantService.GetTenantId();
            // SuperAdmin ignora bloqueio de tenant para poder dar manutenção
            if (!context.User.IsInRole("superadmin"))
            {
                var store = await db.Set<Models.Store>().IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == storeId);
                var status = StoreAccessPolicy.Evaluate(store);
                if (!status.CanOperate)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = status.Message, reason = status.Reason });
                    return;
                }
            }
        }
        await _next(context);
    }
}
