using WhatsAppBot.Worker.Data;
using System.IO.Compression;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Serviço para backup automático da base SQLite agendamentos.db
/// Backup diário zipado em ../backups/
/// </summary>
public class BackupService
{
    private readonly AppDbContext _db;
    private readonly ILogger<BackupService> _logger;
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly string _logsDir;

    public BackupService(AppDbContext db, ILogger<BackupService> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        var dataDir = config["BARBEARIA_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production" ? "/opt/barbearia/data" : AppContext.BaseDirectory;
        _dbPath = Path.Combine(dataDir, "agendamentos.db");
        _backupDir = config["BACKUP_DIR"] ?? "/opt/barbearia/backups";
        _logsDir = config["LOG_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(_backupDir);
    }

    /// <summary>
    /// Executa backup da DB com timestamp
    /// </summary>
    public async Task BackupAsync()
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            var zipPath = Path.Combine(_backupDir, $"agendamentos_{timestamp}.zip");

            // Copia DB com lock (usa temp)
            var tempDb = Path.ChangeExtension(_dbPath, $".tmp_{timestamp}.db");
            File.Copy(_dbPath, tempDb, true);

            // Zipa
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(tempDb, Path.GetFileName(tempDb));
            File.Delete(tempDb);

            _logger.LogInformation("💾 Backup criado: {ZipPath}", zipPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro no backup");
        }
    }

    /// <summary>
    /// Limpa backups antigos (>30 dias)
    /// </summary>
    public void CleanupOldBackups()
    {
        var cutoff = DateTime.Now.AddDays(-30);
        var oldFiles = Directory.GetFiles(_backupDir, "agendamentos_*.zip")
            .Where(f => File.GetCreationTime(f) < cutoff);

        foreach (var file in oldFiles)
        {
            try
            {
                File.Delete(file);
                _logger.LogInformation("🗑️ Backup antigo removido: {File}", file);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Limpa logs antigos (>7 dias)
    /// </summary>
    public void CleanupOldLogs()
    {
        if (!Directory.Exists(_logsDir)) return;

        var cutoff = DateTime.Now.AddDays(-7);
        var logFiles = Directory.GetFiles(_logsDir, "*.log");

        foreach (var file in logFiles)
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.LogInformation("🗑️ Log antigo removido: {File}", file);
                }
            }
            catch { /* ignore */ }
        }
    }
}
