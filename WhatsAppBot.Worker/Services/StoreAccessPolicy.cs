using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

public record StoreAccessStatus(bool CanOperate, string Reason, string Message);

public static class StoreAccessPolicy
{
    public static StoreAccessStatus Evaluate(Store? store, DateTime? now = null)
    {
        var reference = now ?? DateTime.Now;

        if (store == null)
            return Blocked("not_found", "Loja inexistente. Entre em contato com o suporte.");

        if (!store.IsActive)
            return Blocked("inactive", "Sua unidade esta desativada. Entre em contato com o suporte.");

        if (store.IsSuspended)
            return Blocked("suspended", "Sua unidade esta suspensa. Entre em contato com o suporte.");

        if (store.ExpiresAt.HasValue && store.ExpiresAt.Value < reference)
            return Blocked("expired", "Sua assinatura expirou. Entre em contato com o suporte.");

        if (store.SubscriptionExpiry != default && store.SubscriptionExpiry < reference)
            return Blocked("subscription_expired", "Sua assinatura expirou. Entre em contato com o suporte.");

        return new StoreAccessStatus(true, "ok", "");
    }

    public static bool CanOperate(Store? store)
        => Evaluate(store).CanOperate;

    private static StoreAccessStatus Blocked(string reason, string message)
        => new(false, reason, message);
}
