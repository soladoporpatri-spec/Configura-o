using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

public class ReminderService
{
    private readonly AppDbContext _db;
    private readonly WhatsAppClient _whatsapp;
    private readonly ServiceCatalogService _catalog;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(AppDbContext db, WhatsAppClient whatsapp,
        ServiceCatalogService catalog,
        ILogger<ReminderService> logger)
    {
        _db = db;
        _whatsapp = whatsapp;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>True se o telefone parece um número de WhatsApp real (dígitos, sem letras).</summary>
    private static bool IsRealWhatsAppNumber(string? phone)
        => !string.IsNullOrWhiteSpace(phone) && !phone.Any(char.IsLetter) && phone.Count(char.IsDigit) >= 10;

    /// <summary>
    /// Lê uma config priorizando a chave por-loja (<c>Store_{storeId}_{key}</c>) antes da global (<c>{key}</c>).
    /// Compatível com versões antigas que usavam apenas chave global.
    /// </summary>
    private static string? GetStoreConfig(Dictionary<string, string> configs, string key, int storeId)
        => configs.GetValueOrDefault($"Store_{storeId}_{key}")
        ?? configs.GetValueOrDefault(key);

    /// <summary>
    /// Resolve o nome real do serviço pelo ID, com fallback seguro para serviços removidos/inválidos.
    /// </summary>
    private string ResolveServiceName(int serviceId)
        => _catalog.Get(serviceId, includeInactive: true)?.Name ?? $"Serviço #{serviceId}";

    public async Task CheckAndSendRemindersAsync(CancellationToken cancellationToken = default)
    {
        var now = AgendaService.GetBrazilNow();
        var storeId = _db.TenantId > 0 ? _db.TenantId : 1;

        var currentStore = await _db.Stores
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == storeId, cancellationToken);
        var currentStatus = StoreAccessPolicy.Evaluate(currentStore, now);
        if (!currentStatus.CanOperate)
        {
            _logger.LogInformation("Lembretes ignorados para Store {StoreId}: {Reason}", storeId, currentStatus.Reason);
            return;
        }

        var appointments = await _db.Appointments
            .Where(a => a.Status == "ativo")
            .ToListAsync(cancellationToken);

        var configs = await _db.SystemConfigs
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Key, c => c.Value, cancellationToken);

        var storeBridgeUrls = await _db.Stores
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.IsActive && !s.IsSuspended)
            .ToDictionaryAsync(s => s.Id,
                s => string.IsNullOrWhiteSpace(s.BridgeUrl)
                    ? $"http://127.0.0.1:{3000 + s.Id}"
                    : s.BridgeUrl,
                cancellationToken);
        string GetBridgeUrl(int sid) =>
            storeBridgeUrls.TryGetValue(sid, out var url) ? url : $"http://127.0.0.1:{3000 + sid}";

        var storeName = await _db.Stores
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.Id == storeId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "nossa empresa";

        await CheckAndSendRetentionAsync(now, storeId, configs, storeName, GetBridgeUrl, cancellationToken);

        foreach (var appt in appointments)
        {
            if (!IsRealWhatsAppNumber(appt.PhoneNumber)) continue;

            var timeUntil = appt.DateTime - now;
            var servico = ResolveServiceName(appt.ServiceId);
            var hasChanged = false;

            // Walk-in: D-1 e 1h não fazem sentido (cliente já está/estava presente).
            // Agradecimento pós-atendimento (Thanks) é mantido e enviado normalmente.
            if (!appt.IsWalkIn)
            {
                // ── Lembrete 24h ────────────────────────────────────────────────────
                if (GetStoreConfig(configs, "Active_Reminder24h", storeId) == "true"
                    && timeUntil.TotalHours <= 25 && timeUntil.TotalHours >= 23
                    && !appt.ReminderDayBefore)
                {
                    var template = GetStoreConfig(configs, "Msg_Reminder24h", storeId)
                        ?? "Lembrete: {servico} amanhã às {horario}";
                    var ctx = BuildContext(appt, servico, storeName);
                    var msg = MessageTemplateService.Apply(template, ctx);

                    try
                    {
                        await _whatsapp.SendAsync(GetBridgeUrl(appt.StoreId), appt.PhoneNumber, msg, cancellationToken);
                        appt.ReminderDayBefore = true;
                        hasChanged = true;
                        _logger.LogInformation("🔔 Lembrete 24h enviado para {Phone} (Store {StoreId})", appt.PhoneNumber, appt.StoreId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Falha ao enviar lembrete 24h para {Phone}", appt.PhoneNumber);
                    }
                }

                // ── Lembrete 1h ─────────────────────────────────────────────────────
                if (GetStoreConfig(configs, "Active_Reminder1h", storeId) == "true"
                    && timeUntil.TotalMinutes <= 65 && timeUntil.TotalMinutes >= 55
                    && !appt.ReminderOneHour)
                {
                    var template = GetStoreConfig(configs, "Msg_Reminder1h", storeId)
                        ?? "Falta 1 hora! {nome}, te esperamos às {horario} para {servico}.";
                    var ctx = BuildContext(appt, servico, storeName);
                    var msg = MessageTemplateService.Apply(template, ctx);

                    try
                    {
                        await _whatsapp.SendAsync(GetBridgeUrl(appt.StoreId), appt.PhoneNumber, msg, cancellationToken);
                        appt.ReminderOneHour = true;
                        hasChanged = true;
                        _logger.LogInformation("🔔 Lembrete 1h enviado para {Phone} (Store {StoreId})", appt.PhoneNumber, appt.StoreId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Falha ao enviar lembrete 1h para {Phone}", appt.PhoneNumber);
                    }
                }
            }

            // ── Mensagem de agradecimento (pós-atendimento) ──────────────────────
            // Janela: entre 2h e 4h após o horário do agendamento.
            // Não envia para cancelados nem se já enviou.
            if (GetStoreConfig(configs, "Active_Thanks", storeId) == "true"
                && timeUntil.TotalHours <= -2 && timeUntil.TotalHours >= -4
                && appt.Status == "ativo"
                && !appt.ThanksSent)
            {
                var template = GetStoreConfig(configs, "Msg_Thanks", storeId)
                    ?? "Obrigado pela preferência, {nome}! Esperamos que tenha gostado do {servico}. Até a próxima! 😊";
                var ctx = BuildContext(appt, servico, storeName);
                var msg = MessageTemplateService.Apply(template, ctx);

                try
                {
                    await _whatsapp.SendAsync(GetBridgeUrl(appt.StoreId), appt.PhoneNumber, msg, cancellationToken);
                    appt.ThanksSent = true;
                    hasChanged = true;
                    _logger.LogInformation("🙏 Agradecimento enviado para {Phone} (Store {StoreId})", appt.PhoneNumber, appt.StoreId);
                }
                catch (Exception ex)
                {
                    // Falha no envio: NÃO marca como enviado — tenta novamente na próxima execução.
                    _logger.LogWarning(ex, "Falha ao enviar agradecimento para {Phone}", appt.PhoneNumber);
                }
            }

            if (hasChanged)
            {
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao salvar atualizações de lembrete para {Phone}", appt.PhoneNumber);
                }
            }
        }
    }

    private async Task CheckAndSendRetentionAsync(
        DateTime now, int storeId, Dictionary<string, string> configs,
        string storeName, Func<int, string> getBridgeUrl, CancellationToken cancellationToken)
    {
        if (GetStoreConfig(configs, "Active_Retention", storeId) != "true") return;

        var days = int.TryParse(GetStoreConfig(configs, "Retention_Days", storeId), out var parsedDays)
            ? Math.Clamp(parsedDays, 7, 90)
            : 15;
        var cutoff = now.Date.AddDays(-days);
        var template = GetStoreConfig(configs, "Msg_Retention", storeId)
            ?? "Olá, {nome}! Sentimos sua falta em *{loja}*. Que tal agendar uma visita esta semana? Digite *oi* para começar.";

        var phoneMaxDates = await _db.Appointments
            .Where(a => a.DateTime.Date <= cutoff)
            .GroupBy(a => a.PhoneNumber)
            .Select(g => new { Phone = g.Key, MaxDate = g.Max(a => a.DateTime) })
            .ToListAsync(cancellationToken);

        foreach (var item in phoneMaxDates)
        {
            var appt = await _db.Appointments
                .FirstOrDefaultAsync(a => a.PhoneNumber == item.Phone && a.DateTime == item.MaxDate, cancellationToken);

            if (appt == null || appt.RetentionReminderSent) continue;
            if (!IsRealWhatsAppNumber(appt.PhoneNumber)) continue;

            var hasFuture = await _db.Appointments
                .AnyAsync(a => a.PhoneNumber == appt.PhoneNumber && a.DateTime > now, cancellationToken);
            if (hasFuture) continue;

            var servico = ResolveServiceName(appt.ServiceId);
            var ctx = BuildContext(appt, servico, storeName);
            var msg = MessageTemplateService.Apply(template, ctx);

            try
            {
                await _whatsapp.SendAsync(getBridgeUrl(appt.StoreId), appt.PhoneNumber, msg, cancellationToken);
                appt.RetentionReminderSent = true;
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Lembrete de retorno enviado para {Phone} (Store {StoreId})", appt.PhoneNumber, appt.StoreId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao enviar lembrete de retorno para {Phone}", appt.PhoneNumber);
            }
        }
    }

    /// <summary>
    /// Constrói o contexto de template a partir de um agendamento.
    /// Centraliza a lógica de extração de dados para uso consistente em todos os lembretes.
    /// </summary>
    private static MessageTemplateContext BuildContext(Appointment appt, string serviceName, string storeName)
        => new(
            Nome:         appt.ContactName,
            Servico:      serviceName,
            Profissional: appt.BarberName ?? "nossa equipe",
            Loja:         storeName,
            Data:         appt.DateTime.ToString("dd/MM"),
            Hora:         appt.DateTime.ToString("HH:mm")
        );
}
