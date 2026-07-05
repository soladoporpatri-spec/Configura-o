using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WhatsAppBot.Worker.Services;

public class ConversationSessionStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    // Chave composta "phone:storeId" garante isolamento entre lojas para o mesmo número
    public ConcurrentDictionary<string, ConversationSession> Sessions { get; } = new();

    public ConversationSessionStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    private static string CacheKey(string phone, int storeId) => $"{phone}:{storeId}";

    public ConversationSession GetOrAdd(string phone, int storeId, Func<string, ConversationSession> factory)
    {
        return Sessions.GetOrAdd(CacheKey(phone, storeId), _ => factory(phone));
    }

    public async Task LoadIntoCacheIfMissingAsync(string phone, int storeId, CancellationToken ct = default)
    {
        var key = CacheKey(phone, storeId);
        if (Sessions.ContainsKey(key)) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantId = storeId; // Garante que o filtro global filtre pelo store correto

        var entity = await db.ConversationSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Phone == phone && s.StoreId == storeId, ct);

        if (entity == null) return;

        Sessions[key] = new ConversationSession
        {
            Phone        = entity.Phone,
            StoreId      = entity.StoreId,
            State        = entity.State,
            CustomerName = entity.CustomerName,
            SelectedServiceId  =entity.SelectedServiceId,
            SelectedVehicle  = entity.SelectedVehicle,
            SelectedBarberId = entity.SelectedBarberId,
            SelectedBarberName = entity.SelectedBarberName,
            SelectedDate = entity.SelectedDate,
            // LastPollId intencionalmente NÃO restaurado: enquetes do WhatsApp são
            // efêmeras — após reinício do bot, a poll antiga não existe mais na
            // interface do cliente. Zerar aqui garante que os state handlers
            // re-enviem o prompt correto em vez de travar aguardando um voto impossível.
            LastPollId   = null,
            LastInteraction   = entity.LastInteraction,
            TimeOffset   = entity.TimeOffset,
            InvalidResponseCount = entity.InvalidResponseCount,
            SubscriptionPendingPlanId  = entity.SubscriptionPendingPlanId,
            SubscriptionPendingBarberId    = entity.SubscriptionPendingBarberId,
            SubscriptionPendingBarberNome  = entity.SubscriptionPendingBarberNome,
            PendingCancelAppointmentId = entity.PendingCancelAppointmentId
        };
    }

    public async Task<ConversationSession> GetOrAddAsync(string phone, int storeId, Func<string, ConversationSession> factory, CancellationToken ct = default)
    {
        await LoadIntoCacheIfMissingAsync(phone, storeId, ct);
        return Sessions.GetOrAdd(CacheKey(phone, storeId), _ => factory(phone));
    }

    public async Task UpsertAsync(ConversationSession session, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantId = session.StoreId;

        // Filtra por phone E storeId para evitar sobrescrever sessão de outra loja
        var existing = await db.ConversationSessions
            .FirstOrDefaultAsync(s => s.Phone == session.Phone && s.StoreId == session.StoreId, ct);

        if (existing == null)
        {
            db.ConversationSessions.Add(new ConversationSession
            {
                Phone        = session.Phone,
                StoreId      = session.StoreId,
                State        = session.State,
                CustomerName = session.CustomerName,
                SelectedServiceId  =session.SelectedServiceId,
                SelectedVehicle  = session.SelectedVehicle,
                SelectedBarberId = session.SelectedBarberId,
                SelectedBarberName = session.SelectedBarberName,
                SelectedDate = session.SelectedDate,
                LastPollId   = session.LastPollId,
                LastInteraction   = session.LastInteraction,
                TimeOffset   = session.TimeOffset,
                InvalidResponseCount = session.InvalidResponseCount,
                SubscriptionPendingPlanId     = session.SubscriptionPendingPlanId,
                SubscriptionPendingBarberId   = session.SubscriptionPendingBarberId,
                SubscriptionPendingBarberNome = session.SubscriptionPendingBarberNome,
                PendingCancelAppointmentId    = session.PendingCancelAppointmentId
            });
        }
        else
        {
            existing.State        = session.State;
            existing.StoreId      = session.StoreId;
            existing.CustomerName = session.CustomerName;
            existing.SelectedServiceId  =session.SelectedServiceId;
            existing.SelectedVehicle  = session.SelectedVehicle;
            existing.SelectedBarberId = session.SelectedBarberId;
            existing.SelectedBarberName = session.SelectedBarberName;
            existing.SelectedDate = session.SelectedDate;
            existing.LastPollId   = session.LastPollId;
            existing.LastInteraction   = session.LastInteraction;
            existing.TimeOffset   = session.TimeOffset;
            existing.InvalidResponseCount = session.InvalidResponseCount;
            existing.SubscriptionPendingPlanId     = session.SubscriptionPendingPlanId;
            existing.SubscriptionPendingBarberId   = session.SubscriptionPendingBarberId;
            existing.SubscriptionPendingBarberNome = session.SubscriptionPendingBarberNome;
            existing.PendingCancelAppointmentId    = session.PendingCancelAppointmentId;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string phone, int storeId, CancellationToken ct = default)
    {
        Sessions.TryRemove(CacheKey(phone, storeId), out _);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantId = storeId;

        var entity = await db.ConversationSessions
            .FirstOrDefaultAsync(s => s.Phone == phone && s.StoreId == storeId, ct);

        if (entity != null)
        {
            db.ConversationSessions.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> RemoveExpiredAsync(TimeSpan activeTimeout, TimeSpan idleTimeout, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var activeCutoff = now - activeTimeout;
        var idleCutoff   = now - idleTimeout;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantId = 0; // Limpa sessões expiradas de TODAS as lojas

        var expired = await db.ConversationSessions
            .IgnoreQueryFilters() // Garante que o TenantId=0 não seja limitado pelo filtro antigo sem || 0
            .Where(s =>
                (s.State != ConversationState.Idle && s.LastInteraction < activeCutoff) ||
                (s.State == ConversationState.Idle  && s.LastInteraction < idleCutoff))
            .ToListAsync(ct);

        if (expired.Count == 0) return 0;

        foreach (var session in expired)
        {
            Sessions.TryRemove(CacheKey(session.Phone, session.StoreId), out _);
        }

        db.ConversationSessions.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
