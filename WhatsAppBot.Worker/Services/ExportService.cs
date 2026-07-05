using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

public class ExportService
{
    private readonly AppDbContext _db;
    private static readonly CultureInfo PtBR = new("pt-BR");

    // Paleta da marca
    private static readonly XLColor HeaderBg     = XLColor.FromHtml("#172033");
    private static readonly XLColor HeaderFg     = XLColor.White;
    private static readonly XLColor TotalBg      = XLColor.FromHtml("#1e2d47");
    private static readonly XLColor AltRow       = XLColor.FromHtml("#F8FAFC");
    private static readonly XLColor ConfirmedBg  = XLColor.FromHtml("#DCFCE7");
    private static readonly XLColor PendenteBg   = XLColor.FromHtml("#FEF9C3");
    private static readonly XLColor AccentGreen  = XLColor.FromHtml("#166534");
    private static readonly XLColor AccentYellow = XLColor.FromHtml("#854D0E");

    private readonly ServiceCatalogService _catalog;

    public ExportService(AppDbContext db, ServiceCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public void GenerateReport(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var wb = new XLWorkbook();
        wb.Style.Font.FontName = "Calibri";
        wb.Style.Font.FontSize = 10;

        var appointments = _db.Appointments.AsNoTracking().OrderBy(a => a.DateTime).ToList();
        var barbers      = _db.Barbeiros.AsNoTracking().OrderBy(b => b.Nome).ToList();

        var storeName = _db.Stores.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefault(s => s.Id == _db.TenantId)?.Name ?? "Relatório";
        string GetServiceName(int id) =>
            _catalog.Get(id, includeInactive: true)?.Name ?? $"Serviço #{id}";

        AddResumo(wb, appointments, barbers, storeName);
        AddAgendamentos(wb, appointments, GetServiceName);
        AddFinanceiro(wb, appointments);
        AddPorServico(wb, appointments, GetServiceName);
        AddProfissionais(wb, appointments, barbers);
        AddClientes(wb, appointments, GetServiceName);

        wb.SaveAs(outputPath);
    }

    // ─────────────────── ABA: RESUMO ───────────────────
    private static void AddResumo(XLWorkbook wb, List<Appointment> appts, List<Barbeiro> barbers, string storeName)
    {
        var ws   = wb.Worksheets.Add("Resumo");
        var hoje = DateTime.Today;
        var semanaStart = hoje.AddDays(-(int)hoje.DayOfWeek + 1);
        var mesStart    = new DateTime(hoje.Year, hoje.Month, 1);

        var apptHoje     = appts.Where(a => a.DateTime.Date == hoje).ToList();
        var apptSemana   = appts.Where(a => a.DateTime.Date >= semanaStart && a.DateTime.Date <= semanaStart.AddDays(6)).ToList();
        var apptMes      = appts.Where(a => a.DateTime >= mesStart && a.DateTime < mesStart.AddMonths(1)).ToList();
        var apptFuturos  = appts.Where(a => a.DateTime.Date > hoje).ToList();
        var confirmados  = appts.Where(a => a.PresencaConfirmada).ToList();
        var clientes     = appts.Select(a => a.PhoneNumber).Distinct().Count();

        decimal ticketMedio = appts.Count > 0 ? appts.Average(a => a.Preco) : 0;
        double taxaConf     = appts.Count > 0 ? (double)confirmados.Count / appts.Count * 100 : 0;

        // Título
        ws.Cell("A1").Value = $"RESUMO OPERACIONAL — {storeName}";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 16;
        ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#172033");
        ws.Range("A1:D1").Merge();

        ws.Cell("A2").Value = "Atualizado em:";
        ws.Cell("B2").Value = DateTime.Now;
        ws.Cell("B2").Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        ws.Cell("A2").Style.Font.Italic = true;
        ws.Cell("B2").Style.Font.Italic = true;

        // Seção KPIs
        void WriteKpi(int row, string label, object value, string? format = null, XLColor? labelColor = null)
        {
            var lbl = ws.Cell(row, 1);
            var val = ws.Cell(row, 2);
            lbl.Value = label;
            lbl.Style.Font.Bold = true;
            if (labelColor != null) lbl.Style.Font.FontColor = labelColor;

            if (value is decimal d) val.Value = d;
            else if (value is int i)    val.Value = i;
            else if (value is double db) val.Value = db;
            else val.Value = value?.ToString() ?? "";

            if (format != null) val.Style.NumberFormat.Format = format;
        }

        int r = 4;
        ws.Cell(r, 1).Value = "HOJE";
        ws.Range(r, 1, r, 4).Style.Font.Bold = true;
        ws.Range(r, 1, r, 4).Style.Fill.BackgroundColor = HeaderBg;
        ws.Range(r, 1, r, 4).Style.Font.FontColor = HeaderFg;
        r++;

        WriteKpi(r++, "Agendamentos hoje",              apptHoje.Count);
        WriteKpi(r++, "Confirmados hoje",               apptHoje.Count(a => a.PresencaConfirmada));
        WriteKpi(r++, "Faturamento previsto hoje",      apptHoje.Sum(a => a.Preco), "R$ #,##0.00");

        r++;
        ws.Cell(r, 1).Value = "ESTA SEMANA";
        ws.Range(r, 1, r, 4).Style.Font.Bold = true;
        ws.Range(r, 1, r, 4).Style.Fill.BackgroundColor = HeaderBg;
        ws.Range(r, 1, r, 4).Style.Font.FontColor = HeaderFg;
        r++;

        WriteKpi(r++, "Agendamentos na semana",         apptSemana.Count);
        WriteKpi(r++, "Faturamento semana",             apptSemana.Sum(a => a.Preco), "R$ #,##0.00");

        r++;
        ws.Cell(r, 1).Value = "ESTE MÊS";
        ws.Range(r, 1, r, 4).Style.Font.Bold = true;
        ws.Range(r, 1, r, 4).Style.Fill.BackgroundColor = HeaderBg;
        ws.Range(r, 1, r, 4).Style.Font.FontColor = HeaderFg;
        r++;

        WriteKpi(r++, "Agendamentos no mês",            apptMes.Count);
        WriteKpi(r++, "Confirmados no mês",             apptMes.Count(a => a.PresencaConfirmada));
        WriteKpi(r++, "Pendentes no mês",               apptMes.Count(a => !a.PresencaConfirmada));
        WriteKpi(r++, "Faturamento mês (todos)",        apptMes.Sum(a => a.Preco), "R$ #,##0.00");
        WriteKpi(r++, "Faturamento mês (confirmados)",  apptMes.Where(a => a.PresencaConfirmada).Sum(a => a.Preco), "R$ #,##0.00");

        r++;
        ws.Cell(r, 1).Value = "GERAL (HISTÓRICO)";
        ws.Range(r, 1, r, 4).Style.Font.Bold = true;
        ws.Range(r, 1, r, 4).Style.Fill.BackgroundColor = HeaderBg;
        ws.Range(r, 1, r, 4).Style.Font.FontColor = HeaderFg;
        r++;

        WriteKpi(r++, "Total de agendamentos",          appts.Count);
        WriteKpi(r++, "Agendamentos futuros",           apptFuturos.Count);
        WriteKpi(r++, "Clientes únicos",               clientes);
        WriteKpi(r++, "Profissionais ativos",           barbers.Count(b => b.Ativo));
        WriteKpi(r++, "Faturamento total (histórico)",  appts.Sum(a => a.Preco), "R$ #,##0.00");
        WriteKpi(r++, "Ticket médio geral",             ticketMedio, "R$ #,##0.00");
        WriteKpi(r++, "Taxa de confirmação",            taxaConf, "0.0\"%\"");

        ws.Column(1).Width = 34;
        ws.Column(2).Width = 22;
    }

    // ─────────────────── ABA: AGENDAMENTOS ───────────────────
    private static void AddAgendamentos(XLWorkbook wb, List<Appointment> appts, Func<int, string> svcName)
    {
        var ws = wb.Worksheets.Add("Agendamentos");
        var headers = new[]
        {
            "ID", "Data", "Hora", "Dia da Semana", "Semana", "Mês/Ano",
            "Cliente", "Telefone", "Serviço", "Duração (min)",
            "Profissional", "Preço", "Status", "Observações", "Criado em"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var a in appts.OrderBy(a => a.DateTime))
        {
            bool alt = row % 2 == 0;
            ws.Cell(row, 1).Value  = a.Id;
            ws.Cell(row, 2).Value  = a.DateTime.Date;
            ws.Cell(row, 3).Value  = a.DateTime.TimeOfDay;
            ws.Cell(row, 4).Value  = a.DateTime.ToString("dddd", PtBR);
            ws.Cell(row, 5).Value  = ISOWeek.GetWeekOfYear(a.DateTime);
            ws.Cell(row, 6).Value  = a.DateTime.ToString("MM/yyyy", PtBR);
            ws.Cell(row, 7).Value  = a.ContactName;
            ws.Cell(row, 8).Value  = a.PhoneNumber;
            ws.Cell(row, 9).Value  = svcName(a.ServiceId);
            ws.Cell(row, 10).Value = a.DuracaoMinutos;
            ws.Cell(row, 11).Value = a.BarberName ?? "-";
            ws.Cell(row, 12).Value = a.Preco;
            ws.Cell(row, 13).Value = a.PresencaConfirmada ? "Confirmado" : "Pendente";
            ws.Cell(row, 14).Value = a.Notes ?? "";
            ws.Cell(row, 15).Value = a.CreatedAt;

            // Colorir linha pelo status
            if (a.PresencaConfirmada)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = ConfirmedBg;
            else if (alt)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = AltRow;

            // Negrito no status
            ws.Cell(row, 13).Style.Font.Bold = true;
            ws.Cell(row, 13).Style.Font.FontColor = a.PresencaConfirmada ? AccentGreen : AccentYellow;

            row++;
        }

        ws.Column(2).Style.DateFormat.Format  = "dd/MM/yyyy";
        ws.Column(3).Style.DateFormat.Format  = "HH:mm";
        ws.Column(12).Style.NumberFormat.Format = "R$ #,##0.00";
        ws.Column(15).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        FinalizeSheet(ws, headers.Length);
    }

    // ─────────────────── ABA: FINANCEIRO ───────────────────
    private static void AddFinanceiro(XLWorkbook wb, List<Appointment> appts)
    {
        var ws = wb.Worksheets.Add("Financeiro");
        var headers = new[]
        {
            "Mês/Ano", "Agendamentos", "Confirmados", "Pendentes",
            "Taxa Confirmação", "Faturamento Total", "Fat. Confirmados", "Fat. Pendentes", "Ticket Médio"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        decimal grandTotal = 0;
        int grandCount = 0;

        foreach (var g in appts
            .GroupBy(a => new { a.DateTime.Year, a.DateTime.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
        {
            var conf     = g.Where(a => a.PresencaConfirmada).ToList();
            var pend     = g.Where(a => !a.PresencaConfirmada).ToList();
            decimal fat  = g.Sum(a => a.Preco);
            decimal ticket = g.Count() > 0 ? g.Average(a => a.Preco) : 0;
            double taxa  = g.Count() > 0 ? (double)conf.Count / g.Count() * 100 : 0;

            bool alt = row % 2 == 0;
            ws.Cell(row, 1).Value = $"{g.Key.Month:00}/{g.Key.Year}";
            ws.Cell(row, 2).Value = g.Count();
            ws.Cell(row, 3).Value = conf.Count;
            ws.Cell(row, 4).Value = pend.Count;
            ws.Cell(row, 5).Value = taxa;
            ws.Cell(row, 6).Value = fat;
            ws.Cell(row, 7).Value = conf.Sum(a => a.Preco);
            ws.Cell(row, 8).Value = pend.Sum(a => a.Preco);
            ws.Cell(row, 9).Value = ticket;

            if (alt) ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = AltRow;

            grandTotal += fat;
            grandCount += g.Count();
            row++;
        }

        // Linha de total
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 2).Value = grandCount;
        ws.Cell(row, 6).Value = grandTotal;
        ws.Cell(row, 9).Value = grandCount > 0 ? grandTotal / grandCount : 0;
        ws.Range(row, 1, row, headers.Length).Style.Font.Bold = true;
        ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = TotalBg;
        ws.Range(row, 1, row, headers.Length).Style.Font.FontColor = XLColor.White;

        ws.Column(5).Style.NumberFormat.Format = "0.0\"%\"";
        ws.Column(6).Style.NumberFormat.Format = "R$ #,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "R$ #,##0.00";
        ws.Column(8).Style.NumberFormat.Format = "R$ #,##0.00";
        ws.Column(9).Style.NumberFormat.Format = "R$ #,##0.00";
        FinalizeSheet(ws, headers.Length);
    }

    // ─────────────────── ABA: POR SERVIÇO ───────────────────
    private static void AddPorServico(XLWorkbook wb, List<Appointment> appts, Func<int, string> svcName)
    {
        var ws = wb.Worksheets.Add("Por Serviço");
        var headers = new[]
        {
            "Serviço", "Agendamentos", "% do Total",
            "Confirmados", "Pendentes", "Faturamento", "Ticket Médio", "Duração Média (min)"
        };

        WriteHeaders(ws, headers);

        int total = appts.Count;
        int row   = 2;

        foreach (var g in appts
            .GroupBy(a => a.ServiceId)
            .OrderByDescending(g => g.Count()))
        {
            var conf  = g.Count(a => a.PresencaConfirmada);
            decimal fat = g.Sum(a => a.Preco);
            decimal ticket = g.Count() > 0 ? g.Average(a => a.Preco) : 0;
            double pct = total > 0 ? (double)g.Count() / total * 100 : 0;
            double durMedia = g.Count() > 0 ? g.Average(a => a.DuracaoMinutos) : 0;

            bool alt = row % 2 == 0;
            ws.Cell(row, 1).Value = svcName(g.Key);
            ws.Cell(row, 2).Value = g.Count();
            ws.Cell(row, 3).Value = pct;
            ws.Cell(row, 4).Value = conf;
            ws.Cell(row, 5).Value = g.Count() - conf;
            ws.Cell(row, 6).Value = fat;
            ws.Cell(row, 7).Value = ticket;
            ws.Cell(row, 8).Value = Math.Round(durMedia, 0);

            if (alt) ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = AltRow;
            row++;
        }

        ws.Column(3).Style.NumberFormat.Format = "0.0\"%\"";
        ws.Column(6).Style.NumberFormat.Format = "R$ #,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "R$ #,##0.00";
        FinalizeSheet(ws, headers.Length);
    }

    // ─────────────────── ABA: PROFISSIONAIS ───────────────────
    private static void AddProfissionais(XLWorkbook wb, List<Appointment> appts, List<Barbeiro> barbers)
    {
        var ws = wb.Worksheets.Add("Profissionais");
        var headers = new[]
        {
            "Profissional", "Ativo", "Especialidade", "Horário",
            "Agendamentos", "Confirmados", "Pendentes", "Taxa Conf.",
            "Faturamento", "Ticket Médio", "Duração Média (min)"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var b in barbers.OrderByDescending(b => b.Ativo).ThenBy(b => b.Nome))
        {
            var ba   = appts.Where(a => a.BarberId == b.Id || a.BarberName == b.Nome).ToList();
            var conf = ba.Count(a => a.PresencaConfirmada);
            decimal fat    = ba.Sum(a => a.Preco);
            decimal ticket = ba.Count > 0 ? ba.Average(a => a.Preco) : 0;
            double taxa    = ba.Count > 0 ? (double)conf / ba.Count * 100 : 0;
            double durMedia = ba.Count > 0 ? ba.Average(a => a.DuracaoMinutos) : 0;

            bool alt = row % 2 == 0;
            ws.Cell(row, 1).Value  = b.Nome;
            ws.Cell(row, 2).Value  = b.Ativo ? "Sim" : "Não";
            ws.Cell(row, 3).Value  = b.Especialidade;
            ws.Cell(row, 4).Value  = $"{b.WorkStart:hh\\:mm} – {b.WorkEnd:hh\\:mm}";
            ws.Cell(row, 5).Value  = ba.Count;
            ws.Cell(row, 6).Value  = conf;
            ws.Cell(row, 7).Value  = ba.Count - conf;
            ws.Cell(row, 8).Value  = taxa;
            ws.Cell(row, 9).Value  = fat;
            ws.Cell(row, 10).Value = ticket;
            ws.Cell(row, 11).Value = Math.Round(durMedia, 0);

            if (!b.Ativo)
                ws.Range(row, 1, row, headers.Length).Style.Font.FontColor = XLColor.Gray;
            else if (alt)
                ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = AltRow;

            row++;
        }

        ws.Column(8).Style.NumberFormat.Format  = "0.0\"%\"";
        ws.Column(9).Style.NumberFormat.Format  = "R$ #,##0.00";
        ws.Column(10).Style.NumberFormat.Format = "R$ #,##0.00";
        FinalizeSheet(ws, headers.Length);
    }

    // ─────────────────── ABA: CLIENTES ───────────────────
    private static void AddClientes(XLWorkbook wb, List<Appointment> appts, Func<int, string> svcName)
    {
        var ws = wb.Worksheets.Add("Clientes");
        var headers = new[]
        {
            "Telefone", "Nome", "Agendamentos", "Confirmados",
            "Serviço Favorito", "Primeiro Atendimento", "Último Atendimento",
            "Dias desde última visita", "Total Gasto", "Ticket Médio"
        };

        WriteHeaders(ws, headers);

        int row = 2;
        var hoje = DateTime.Today;

        foreach (var g in appts
            .GroupBy(a => a.PhoneNumber)
            .OrderByDescending(g => g.Max(a => a.DateTime)))
        {
            var last    = g.OrderByDescending(a => a.DateTime).First();
            var first   = g.OrderBy(a => a.DateTime).First();
            var conf    = g.Count(a => a.PresencaConfirmada);
            decimal fat = g.Sum(a => a.Preco);
            decimal ticket = g.Count() > 0 ? g.Average(a => a.Preco) : 0;
            int diasDesde = (hoje - last.DateTime.Date).Days;
            var favServico = g
                .GroupBy(a => a.ServiceId)
                .OrderByDescending(s => s.Count())
                .Select(s => svcName(s.Key))
                .FirstOrDefault() ?? "-";

            bool alt = row % 2 == 0;
            ws.Cell(row, 1).Value  = g.Key;
            ws.Cell(row, 2).Value  = last.ContactName;
            ws.Cell(row, 3).Value  = g.Count();
            ws.Cell(row, 4).Value  = conf;
            ws.Cell(row, 5).Value  = favServico;
            ws.Cell(row, 6).Value  = first.DateTime.Date;
            ws.Cell(row, 7).Value  = last.DateTime.Date;
            ws.Cell(row, 8).Value  = diasDesde;
            ws.Cell(row, 9).Value  = fat;
            ws.Cell(row, 10).Value = ticket;

            if (alt) ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = AltRow;

            // Clientes com visita recente (≤30 dias) em verde claro
            if (diasDesde <= 30)
                ws.Cell(row, 8).Style.Font.FontColor = AccentGreen;
            else if (diasDesde > 90)
                ws.Cell(row, 8).Style.Font.FontColor = XLColor.Red;

            row++;
        }

        ws.Column(6).Style.DateFormat.Format  = "dd/MM/yyyy";
        ws.Column(7).Style.DateFormat.Format  = "dd/MM/yyyy";
        ws.Column(9).Style.NumberFormat.Format  = "R$ #,##0.00";
        ws.Column(10).Style.NumberFormat.Format = "R$ #,##0.00";
        FinalizeSheet(ws, headers.Length);
    }

    // ─────────────────── HELPERS ───────────────────
    private static void WriteHeaders(IXLWorksheet ws, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Fill.BackgroundColor = HeaderBg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void FinalizeSheet(IXLWorksheet ws, int colCount)
    {
        var used = ws.RangeUsed();
        if (used is null) { ws.Columns().AdjustToContents(); return; }

        // Bordas internas suaves
        used.Style.Border.InsideBorder      = XLBorderStyleValues.Thin;
        used.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E8F0");
        used.Style.Border.OutsideBorder     = XLBorderStyleValues.Medium;
        used.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");

        // Filtro automático e congela cabeçalho
        used.SetAutoFilter();
        ws.SheetView.FreezeRows(1);

        // Centraliza cabeçalho verticalmente
        ws.Row(1).Height = 22;
        ws.Range(1, 1, 1, colCount).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        ws.Columns().AdjustToContents();

        // Garante largura mínima de 10 e máxima de 45 por coluna
        foreach (var col in ws.ColumnsUsed())
        {
            if (col.Width < 10) col.Width = 10;
            if (col.Width > 45) col.Width = 45;
        }
    }
}
