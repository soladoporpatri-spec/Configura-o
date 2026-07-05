namespace WhatsAppBot.Worker.Services;

public interface ITenantService
{
    /// <summary> Retorna o ID da loja do contexto atual </summary>
    int GetTenantId();

    /// <summary> Indica se o contexto atual é superadmin (acesso global) </summary>
    bool IsSuperAdmin();

    /// <summary> Define manualmente o ID (usado pelo Worker/Bot) </summary>
    void SetTenantId(int tenantId);
}
