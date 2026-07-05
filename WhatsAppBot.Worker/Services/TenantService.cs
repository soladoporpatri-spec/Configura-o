using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace WhatsAppBot.Worker.Services;

public class TenantService : ITenantService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private int? _manualTenantId;

    public TenantService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public int GetTenantId()
    {
        // 1. Prioridade máxima: definido manualmente (Worker/Bot)
        if (_manualTenantId.HasValue) return _manualTenantId.Value;

        // 2. Claim JWT (login direto pelo frontend sem proxy)
        var claim = _httpContextAccessor.HttpContext?.User?.FindFirst("StoreId")?.Value;
        if (int.TryParse(claim, out var jwtId) && jwtId >= 0) return jwtId;

        // 3. Header X-Store-Id (Express proxy com usuário autenticado)
        //    storeId=0  → superadmin (vê todas as lojas)
        //    storeId>0  → loja específica do usuário
        var headerValue = _httpContextAccessor.HttpContext?.Request.Headers["X-Store-Id"].ToString();
        if (int.TryParse(headerValue, out var headerId) && headerId >= 0) return headerId;

        // 4. Fallback final: 0 = superadmin / sem filtro de tenant
        //    (requests via X-API-KEY puro sem Store-Id, ou contexto de startup)
        return 0;
    }

    public bool IsSuperAdmin() => GetTenantId() == 0;

    public void SetTenantId(int tenantId) => _manualTenantId = tenantId;
}
