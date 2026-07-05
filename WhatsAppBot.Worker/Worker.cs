using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Services;

namespace WhatsAppBot.Worker;

public class BotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WhatsAppClient _client;
    private readonly ILogger<BotWorker> _logger;

    // TenantId default para o worker (sem HttpContext/JWT).
    // Importante: antes de usar qualquer DbContext/queries filtradas por tenant,
    // precisamos setar manualmente o tenant no ITenantService.
    private const int DefaultTenantId = 1;


    private DateTime _nextTimeoutCheck = DateTime.MinValue;
    private DateTime _nextExport;
    private DateTime _nextReminderCheck = DateTime.MinValue;
    private DateTime _nextBackup;

    // Multi-tenant: contagem de falhas e estado de alerta por loja (storeId → estado).
    // ConcurrentDictionary porque o polling agora roda as lojas EM PARALELO (Task.WhenAll).
    private readonly ConcurrentDictionary<int, int> _bridgeFailures = new();
    private readonly ConcurrentDictionary<int, byte> _bridgeOfflineNotified = new();

    public BotWorker(IServiceScopeFactory scopeFactory, WhatsAppClient client, ILogger<BotWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _logger = logger;

        // Agenda as tarefas pesadas para a próxima ocorrência na madrugada
        _nextBackup = GetNextOccurrence(3); // 03:00 AM
        _nextExport = GetNextOccurrence(4); // 04:00 AM
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Bot iniciado.");
        await RunExportAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollWhatsAppAsync(stoppingToken);
            await RunPeriodicTasksAsync(stoppingToken);
            // Se qualquer loja tem falhas de bridge, espera um pouco mais antes do próximo ciclo
            var anyFailing = _bridgeFailures.Values.Any(v => v > 0);
            await Task.Delay(anyFailing ? 5000 : 3000, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bot encerrando. Atualizando planilhas antes de parar.");
        await RunExportAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Multi-tenant: faz polling da Bridge de CADA loja ativa, usando a BridgeUrl específica de cada uma.
    /// Sem isso, apenas a Store 1 (DefaultTenantId) recebia mensagens — ponto único de falha.
    /// </summary>
    private async Task PollWhatsAppAsync(CancellationToken stoppingToken)
    {
        List<(int Id, string BridgeUrl)> stores;
        try
        {
            using var listScope = _scopeFactory.CreateScope();
            var listDb = listScope.ServiceProvider.GetRequiredService<AppDbContext>();
            listDb.TenantId = 0; // lista todas as lojas ativas
            var candidates = await listDb.Stores
                .AsNoTracking()
                .Where(s => s.IsActive)
                .ToListAsync(stoppingToken);

            stores = candidates
                .Where(StoreAccessPolicy.CanOperate)
                .Where(s => !string.IsNullOrWhiteSpace(s.BridgeUrl))
                .Select(s => new ValueTuple<int, string>(s.Id, s.BridgeUrl!))
                .ToList();

            if (stores.Count == 0)
            {
                var defaultStore = candidates.FirstOrDefault(s => s.Id == DefaultTenantId);
                if (candidates.Count == 0 || StoreAccessPolicy.CanOperate(defaultStore))
                    stores.Add((DefaultTenantId, "http://127.0.0.1:3001"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao listar lojas para polling do WhatsApp.");
            return;
        }

        // Fallback: se nenhuma loja tem BridgeUrl configurada, usa a loja default na bridge padrão
        if (stores.Count == 0)
            return;

        // PARALELO: cada loja é processada em sua própria Task. Uma bridge lenta/inacessível
        // não trava as outras — cada PollStoreAsync usa escopo/DbContext/tenant próprios.
        await Task.WhenAll(stores.Select(s => PollStoreAsync(s.Id, s.BridgeUrl, stoppingToken)));
    }

    private async Task PollStoreAsync(int storeId, string bridgeUrl, CancellationToken stoppingToken)
    {
        try
        {
            if (!await _client.IsBridgeReachableAsync(bridgeUrl, stoppingToken))
            {
                var failures = _bridgeFailures.GetValueOrDefault(storeId) + 1;
                _bridgeFailures[storeId] = failures;

                // 4.9: cap de log — registra na 1ª, 3ª e depois a cada 50 falhas para evitar ruído
                if (failures == 1 || failures == 3 || failures % 50 == 0)
                    _logger.LogWarning("Bridge inacessível (Store {StoreId}, {BridgeUrl}). Falha {Count}", storeId, bridgeUrl, failures);

                if (!_bridgeOfflineNotified.ContainsKey(storeId) && failures >= 3)
                {
                    using var notificationScope = _scopeFactory.CreateScope();
                    var notificationTenant = notificationScope.ServiceProvider.GetRequiredService<ITenantService>();
                    notificationTenant.SetTenantId(storeId); // 4.8: alerta roteado para a loja afetada
                    var notifications = notificationScope.ServiceProvider.GetRequiredService<NotificationService>();
                    await notifications.NotifySystemAlert("Bot WhatsApp offline", "A Bridge desta loja nao respondeu nas ultimas verificacoes.", NotificationPriority.Critical);
                    _bridgeOfflineNotified.TryAdd(storeId, 0);
                }
                return;
            }

            var messages = await _client.GetMessagesAsync(bridgeUrl, stoppingToken);
            if (_bridgeFailures.GetValueOrDefault(storeId) > 0)
            {
                _logger.LogInformation("WhatsApp Bridge da Store {StoreId} recuperada apos {Failures} falhas.", storeId, _bridgeFailures[storeId]);
                _bridgeFailures[storeId] = 0;
                _bridgeOfflineNotified.TryRemove(storeId, out _);
            }

            if (!messages.Any())
                return;

            using var scope = _scopeFactory.CreateScope();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
            var manager = scope.ServiceProvider.GetRequiredService<ConversationStateManager>();

            var processedMessageIds = new List<string>();

            foreach (var msg in messages)
            {
                // O storeId vem da bridge desta loja; msg.StoreId só sobrescreve se explícito (defensivo)
                var msgStoreId = msg.StoreId ?? storeId;
                tenantService.SetTenantId(msgStoreId);

                _logger.LogInformation("Mensagem recebida de [{Phone}] (Store: {StoreId}): {Text}", msg.Phone, msgStoreId, msg.Text);
                await manager.HandleAsync(msg.Phone, msg.Text, msg.PollId, msg.Id, stoppingToken);
                if (!string.IsNullOrWhiteSpace(msg.Id))
                    processedMessageIds.Add(msg.Id);
            }

            await _client.AckMessagesAsync(bridgeUrl, processedMessageIds, stoppingToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var failures = _bridgeFailures.GetValueOrDefault(storeId) + 1;
            _bridgeFailures[storeId] = failures;
            if (failures == 1 || failures == 3 || failures % 50 == 0)
                _logger.LogWarning("WhatsApp Bridge da Store {StoreId} indisponivel ({Failures} falhas): {Message}", storeId, failures, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar mensagens da Store {StoreId}.", storeId);
        }
    }

    private async Task RunPeriodicTasksAsync(CancellationToken stoppingToken)
    {
        if (DateTime.Now >= _nextTimeoutCheck) await RunTimeoutCheckAsync(stoppingToken);
        if (DateTime.Now >= _nextExport) await RunExportAsync(stoppingToken);
        if (DateTime.Now >= _nextReminderCheck) await RunRemindersAsync(stoppingToken);
        if (DateTime.Now >= _nextBackup) await RunBackupAsync();
    }

    private async Task RunTimeoutCheckAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
            tenantService.SetTenantId(0); // TenantId=0: CheckTimeouts varre sessões de todas as lojas

            var manager = scope.ServiceProvider.GetRequiredService<ConversationStateManager>();
            await manager.CheckTimeoutsAsync(stoppingToken);

            _nextTimeoutCheck = DateTime.Now.AddMinutes(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao verificar timeouts de conversa.");
        }
    }

    private async Task RunExportAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<int> storeIds;
            using (var listScope = _scopeFactory.CreateScope())
            {
                var db = listScope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.TenantId = 0;
                var stores = await db.Stores
                    .AsNoTracking()
                    .Where(s => s.IsActive)
                    .ToListAsync(cancellationToken);
                storeIds = stores
                    .Where(StoreAccessPolicy.CanOperate)
                    .Select(s => s.Id)
                    .ToList();
            }

            foreach (var storeId in storeIds)
            {
                using var scope = _scopeFactory.CreateScope();
                var spreadsheets = scope.ServiceProvider.GetRequiredService<SpreadsheetMaintenanceService>();
                var result = await spreadsheets.UpdateAsync(storeId, syncGoogleSheets: true, cancellationToken: cancellationToken);
                if (result.Ok)
                    _logger.LogInformation("Planilha atualizada para Store {StoreId}: {Path}. Google Sheets: {SheetsMessage}", storeId, result.XlsxPath, result.GoogleSheets?.Message ?? "nao solicitado");
                else
                    _logger.LogWarning("Planilha nao atualizada para Store {StoreId}: {Message}", storeId, result.Message);
            }

            _nextExport = GetNextOccurrence(4);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao gerar planilha.");
        }
    }

    private async Task RunRemindersAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Lembretes processados por loja para usar o BridgeUrl correto de cada loja
            List<int> storeIds;
            using (var listScope = _scopeFactory.CreateScope())
            {
                var listDb = listScope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
                listDb.TenantId = 0;
                var stores = await listDb.Stores
                    .AsNoTracking()
                    .Where(s => s.IsActive)
                    .ToListAsync(stoppingToken);
                storeIds = stores
                    .Where(StoreAccessPolicy.CanOperate)
                    .Select(s => s.Id)
                    .ToList();
            }

            foreach (var storeId in storeIds)
            {
                using var storeScope = _scopeFactory.CreateScope();
                var tenantService = storeScope.ServiceProvider.GetRequiredService<ITenantService>();
                tenantService.SetTenantId(storeId);
                var reminder = storeScope.ServiceProvider.GetRequiredService<ReminderService>();
                await reminder.CheckAndSendRemindersAsync(stoppingToken);
            }

            _nextReminderCheck = DateTime.Now.AddMinutes(2);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar lembretes.");
        }
    }

    private async Task RunBackupAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
            tenantService.SetTenantId(0); // Backup é operação de sistema, não filtra por loja

            var backup = scope.ServiceProvider.GetRequiredService<BackupService>();

            await backup.BackupAsync();
            backup.CleanupOldBackups();
            backup.CleanupOldLogs();
            _logger.LogInformation("Backup diario executado.");
            _nextBackup = GetNextOccurrence(3);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao executar backup.");
        }
    }

    private static DateTime GetNextOccurrence(int hour)
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(hour);
        return now >= next ? next.AddDays(1) : next;
    }
}
