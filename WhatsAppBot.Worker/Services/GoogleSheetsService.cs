using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

public class GoogleSheetsService
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<GoogleSheetsService> _logger;
    private readonly ServiceCatalogService _catalog;

    public GoogleSheetsService(AppDbContext db, HttpClient http, IConfiguration config,
        ILogger<GoogleSheetsService> logger, ServiceCatalogService catalog)
    {
        _db      = db;
        _http    = http;
        _config  = config;
        _logger  = logger;
        _catalog = catalog;
    }

    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
        => (await SyncDetailedAsync(cancellationToken)).Synced;

    public async Task<GoogleSheetsSyncResult> SyncDetailedAsync(CancellationToken cancellationToken = default)
    {
        // ── Busca webhook ──────────────────────────────────────────────────────
        var webhookUrl = await _db.Database
            .SqlQueryRaw<string>("SELECT Value FROM SystemConfigs WHERE Key = {0}", "GoogleSheets_WebhookUrl")
            .FirstOrDefaultAsync(cancellationToken);

        webhookUrl = string.IsNullOrWhiteSpace(webhookUrl)
            ? _config["GoogleSheets:WebhookUrl"] ?? _config["GOOGLE_SHEETS_WEBHOOK_URL"]
            : webhookUrl;

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogInformation("Google Sheets nao configurado. Defina GoogleSheets:WebhookUrl para ativar a sincronizacao.");
            return new GoogleSheetsSyncResult(false, "Google Sheets nao configurado.", 0, 0, 0, 0, null);
        }

        // ── Resolver de nomes de serviço via catálogo (com fallback seguro) ──────
        string GetServiceName(int id) =>
            _catalog.Get(id, includeInactive: true)?.Name ?? $"Serviço #{id}";

        // ── Carrega dados ──────────────────────────────────────────────────────
        var appointments = await _db.Appointments
            .AsNoTracking()
            .OrderBy(a => a.DateTime)
            .ToListAsync(cancellationToken);

        var barbers = await _db.Barbeiros
            .AsNoTracking()
            .OrderBy(b => b.Nome)
            .ToListAsync(cancellationToken);

        var logs = await _db.Set<AuditLog>()
            .AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .Take(500)
            .ToListAsync(cancellationToken);

        // ── Cálculos do Resumo ─────────────────────────────────────────────────
        var hoje       = DateTime.Today;
        var mesStart   = new DateTime(hoje.Year, hoje.Month, 1);
        var semStart   = hoje.AddDays(-(int)hoje.DayOfWeek + 1);

        var apptHoje   = appointments.Where(a => a.DateTime.Date == hoje).ToList();
        var apptSemana = appointments.Where(a => a.DateTime.Date >= semStart && a.DateTime.Date <= semStart.AddDays(6)).ToList();
        var apptMes    = appointments.Where(a => a.DateTime >= mesStart && a.DateTime < mesStart.AddMonths(1)).ToList();
        var futuros    = appointments.Where(a => a.DateTime.Date > hoje).ToList();
        var conf       = appointments.Where(a => a.PresencaConfirmada).ToList();
        var clientesCount = appointments.Select(a => a.PhoneNumber).Distinct().Count();

        decimal ticketGeral = appointments.Count > 0 ? appointments.Average(a => a.Preco) : 0;
        double taxaConf     = appointments.Count > 0 ? (double)conf.Count / appointments.Count * 100 : 0;

        // ── Monta payload ──────────────────────────────────────────────────────
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow,

            Resumo = new
            {
                GeradoEm          = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                AgendamentosHoje  = apptHoje.Count,
                ConfirmadosHoje   = apptHoje.Count(a => a.PresencaConfirmada),
                FaturamentoHoje   = apptHoje.Sum(a => a.Preco),
                AgendamentosSemana = apptSemana.Count,
                FaturamentoSemana = apptSemana.Sum(a => a.Preco),
                AgendamentosMes   = apptMes.Count,
                ConfirmadosMes    = apptMes.Count(a => a.PresencaConfirmada),
                FaturamentoMes    = apptMes.Sum(a => a.Preco),
                TotalHistorico    = appointments.Count,
                Futuros           = futuros.Count,
                ClientesUnicos    = clientesCount,
                ProfissionaisAtivos = barbers.Count(b => b.Ativo),
                FaturamentoTotal  = appointments.Sum(a => a.Preco),
                TicketMedioGeral  = Math.Round(ticketGeral, 2),
                TaxaConfirmacaoPct = Math.Round(taxaConf, 1)
            },

            Agendamentos = appointments.Select(a => new
            {
                a.Id,
                Data         = a.DateTime.ToString("yyyy-MM-dd"),
                Hora         = a.DateTime.ToString("HH:mm"),
                DiaSemana    = a.DateTime.ToString("dddd", new System.Globalization.CultureInfo("pt-BR")),
                Semana       = System.Globalization.ISOWeek.GetWeekOfYear(a.DateTime),
                MesAno       = a.DateTime.ToString("MM/yyyy"),
                Cliente      = a.ContactName,
                Telefone     = a.PhoneNumber,
                Servico      = GetServiceName(a.ServiceId),
                DuracaoMin   = a.DuracaoMinutos,
                Profissional = a.BarberName ?? "",
                a.Preco,
                Status       = a.PresencaConfirmada ? "Confirmado" : "Pendente",
                Observacoes  = a.Notes ?? "",
                CriadoEm    = a.CreatedAt.ToString("yyyy-MM-dd HH:mm")
            }),

            Financeiro = appointments
                .GroupBy(a => new { a.DateTime.Year, a.DateTime.Month })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                .Select(g =>
                {
                    var confG = g.Where(a => a.PresencaConfirmada).ToList();
                    decimal fat = g.Sum(a => a.Preco);
                    decimal ticket = g.Count() > 0 ? g.Average(a => a.Preco) : 0;
                    double taxa = g.Count() > 0 ? (double)confG.Count / g.Count() * 100 : 0;
                    return new
                    {
                        MesAno            = $"{g.Key.Month:00}/{g.Key.Year}",
                        Agendamentos      = g.Count(),
                        Confirmados       = confG.Count,
                        Pendentes         = g.Count() - confG.Count,
                        TaxaConfirmacaoPct = Math.Round(taxa, 1),
                        FaturamentoTotal  = fat,
                        FaturamentoConfirmados = confG.Sum(a => a.Preco),
                        FaturamentoPendentes   = g.Where(a => !a.PresencaConfirmada).Sum(a => a.Preco),
                        TicketMedio       = Math.Round(ticket, 2)
                    };
                }),

            PorServico = appointments
                .GroupBy(a => a.ServiceId)
                .OrderByDescending(g => g.Count())
                .Select(g =>
                {
                    var confG  = g.Count(a => a.PresencaConfirmada);
                    decimal fat = g.Sum(a => a.Preco);
                    decimal ticket = g.Count() > 0 ? g.Average(a => a.Preco) : 0;
                    double pct = appointments.Count > 0 ? (double)g.Count() / appointments.Count * 100 : 0;
                    double durMedia = g.Count() > 0 ? g.Average(a => a.DuracaoMinutos) : 0;
                    return new
                    {
                        Servico           = GetServiceName(g.Key),
                        Agendamentos      = g.Count(),
                        PctDoTotal        = Math.Round(pct, 1),
                        Confirmados       = confG,
                        Pendentes         = g.Count() - confG,
                        Faturamento       = fat,
                        TicketMedio       = Math.Round(ticket, 2),
                        DuracaoMediaMin   = (int)Math.Round(durMedia, 0)
                    };
                }),

            Profissionais = barbers.Select(b =>
            {
                var ba    = appointments.Where(a => a.BarberId == b.Id || a.BarberName == b.Nome).ToList();
                var confB = ba.Count(a => a.PresencaConfirmada);
                decimal fat    = ba.Sum(a => a.Preco);
                decimal ticket = ba.Count > 0 ? ba.Average(a => a.Preco) : 0;
                double taxa    = ba.Count > 0 ? (double)confB / ba.Count * 100 : 0;
                double durMedia = ba.Count > 0 ? ba.Average(a => a.DuracaoMinutos) : 0;
                return new
                {
                    b.Id,
                    b.Nome,
                    Ativo             = b.Ativo,
                    b.Especialidade,
                    HorarioInicio     = b.WorkStart.ToString(@"hh\:mm"),
                    HorarioFim        = b.WorkEnd.ToString(@"hh\:mm"),
                    AlmocoInicio      = b.LunchStart?.ToString(@"hh\:mm"),
                    AlmocoFim         = b.LunchEnd?.ToString(@"hh\:mm"),
                    Agendamentos      = ba.Count,
                    Confirmados       = confB,
                    Pendentes         = ba.Count - confB,
                    TaxaConfirmacaoPct = Math.Round(taxa, 1),
                    Faturamento       = fat,
                    TicketMedio       = Math.Round(ticket, 2),
                    DuracaoMediaMin   = (int)Math.Round(durMedia, 0)
                };
            }),

            Clientes = appointments
                .GroupBy(a => a.PhoneNumber)
                .OrderByDescending(g => g.Max(a => a.DateTime))
                .Select(g =>
                {
                    var last    = g.OrderByDescending(a => a.DateTime).First();
                    var first   = g.OrderBy(a => a.DateTime).First();
                    var confC   = g.Count(a => a.PresencaConfirmada);
                    decimal fat = g.Sum(a => a.Preco);
                    decimal ticket = g.Count() > 0 ? g.Average(a => a.Preco) : 0;
                    int diasDesde = (DateTime.Today - last.DateTime.Date).Days;
                    var favServico = g
                        .GroupBy(a => a.ServiceId)
                        .OrderByDescending(s => s.Count())
                        .Select(s => GetServiceName(s.Key))
                        .FirstOrDefault() ?? "-";
                    return new
                    {
                        Telefone          = g.Key,
                        Nome              = last.ContactName,
                        Agendamentos      = g.Count(),
                        Confirmados       = confC,
                        ServicoFavorito   = favServico,
                        PrimeiroAtendimento = first.DateTime.ToString("yyyy-MM-dd"),
                        UltimoAtendimento = last.DateTime.ToString("yyyy-MM-dd HH:mm"),
                        DiasDesdeUltima   = diasDesde,
                        TotalGasto        = fat,
                        TicketMedio       = Math.Round(ticket, 2)
                    };
                }),

            Logs = logs.Select(l => new
            {
                Timestamp = l.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                l.User,
                l.Action,
                l.Details
            })
        };

        // ── Envia ──────────────────────────────────────────────────────────────
        using var response = await _http.PostAsJsonAsync(webhookUrl, payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google Sheets retornou {StatusCode}", response.StatusCode);
            return new GoogleSheetsSyncResult(
                false,
                $"Google Sheets retornou {(int)response.StatusCode}.",
                appointments.Count, clientesCount, barbers.Count, logs.Count, webhookUrl);
        }

        _logger.LogInformation("Google Sheets sincronizado: {Appts} agendamentos, {Clients} clientes, {Barbers} profissionais, {Logs} logs.",
            appointments.Count, clientesCount, barbers.Count, logs.Count);

        return new GoogleSheetsSyncResult(
            true,
            "Google Sheets sincronizado com sucesso.",
            appointments.Count, clientesCount, barbers.Count, logs.Count, webhookUrl);
    }
}

public record GoogleSheetsSyncResult(
    bool Synced,
    string Message,
    int Appointments,
    int Clients,
    int Professionals,
    int Logs,
    string? WebhookUrl);
