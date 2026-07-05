using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;

namespace WhatsAppBot.Worker.Services;

public class SpreadsheetMaintenanceService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SpreadsheetMaintenanceService> _logger;

    public SpreadsheetMaintenanceService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<SpreadsheetMaintenanceService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    public string GetExportPath(int? storeId = null)
    {
        var configured = _config["EXPORT_XLSX_PATH"] ?? _config["Export:XlsxPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return storeId.HasValue
                ? configured.Replace("{storeId}", storeId.Value.ToString(), StringComparison.OrdinalIgnoreCase)
                : configured;
        }

        var dataDir = _config["BARBEARIA_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production" ? "/opt/barbearia/data" : AppContext.BaseDirectory;

        return storeId is > 0
            ? Path.Combine(dataDir, "exports", $"store-{storeId.Value}", "agendamentos.xlsx")
            : Path.Combine(dataDir, "exports", "agendamentos.xlsx");
    }

    public async Task<SpreadsheetMaintenanceResult> UpdateAsync(int storeId, bool syncGoogleSheets = true, CancellationToken cancellationToken = default)
    {
        if (storeId <= 0)
        {
            return new SpreadsheetMaintenanceResult(
                false,
                "Informe uma loja valida para atualizar planilhas.",
                "",
                0,
                0,
                null);
        }

        using var scope = _scopeFactory.CreateScope();
        var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
        tenantService.SetTenantId(storeId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TenantId = storeId;
        var export = scope.ServiceProvider.GetRequiredService<ExportService>();
        var sheets = scope.ServiceProvider.GetRequiredService<GoogleSheetsService>();

        var store = await db.Stores
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == storeId, cancellationToken);

        var status = StoreAccessPolicy.Evaluate(store);
        if (!status.CanOperate)
        {
            return new SpreadsheetMaintenanceResult(
                false,
                status.Message,
                "",
                0,
                0,
                null);
        }

        var path = GetExportPath(storeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        export.GenerateReport(path);
        var appointments = db.Appointments.Count();

        GoogleSheetsSyncResult? sheetsResult = null;
        if (syncGoogleSheets)
        {
            try
            {
                sheetsResult = await sheets.SyncDetailedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao sincronizar Google Sheets durante manutencao de planilhas.");
                sheetsResult = new GoogleSheetsSyncResult(false, $"Erro ao sincronizar Google Sheets: {ex.Message}", appointments, 0, 0, 0, null);
            }
        }

        var info = new FileInfo(path);
        _logger.LogInformation("Planilha Excel atualizada em {Path} ({Bytes} bytes). Google Sheets: {SheetsStatus}",
            path, info.Exists ? info.Length : 0, sheetsResult?.Message ?? "nao solicitado");

        return new SpreadsheetMaintenanceResult(
            true,
            "Planilhas atualizadas.",
            path,
            info.Exists ? info.Length : 0,
            appointments,
            sheetsResult);
    }
}

public record SpreadsheetMaintenanceResult(
    bool Ok,
    string Message,
    string XlsxPath,
    long XlsxBytes,
    int Appointments,
    GoogleSheetsSyncResult? GoogleSheets);
