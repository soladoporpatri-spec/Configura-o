using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

public class AgendaService
{
    private readonly AppDbContext _db;
    private readonly BusinessHours _hours;
    private readonly ServiceCatalogService _catalog;
    private readonly IMemoryCache _cache;

    /// <summary>
    /// TTL curto para o cache de horários usado APENAS na paginação do bot ("ver mais"/"anteriores").
    /// A seleção final do horário sempre revalida com dados frescos (IsSlotAvailable / SaveAsync),
    /// então uma leve defasagem aqui nunca causa agendamento duplo — só evita recalcular tudo
    /// do zero a cada toque de paginação.
    /// </summary>
    private static readonly TimeSpan PaginationCacheTtl = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Cache de dados do barbeiro por instância de AgendaService (que é Scoped = por request).
    /// Evita N queries repetidas ao iterar os 14 dias em SendDatePollAsync: sem este cache,
    /// cada dia carregaria Barbeiro + BarbeiroHorario novamente do banco (até 28 queries extras).
    /// Com o cache, são 2 queries na 1ª iteração e 0 nas demais 13.
    /// </summary>
    private readonly Dictionary<int, (Barbeiro? Barber, List<BarbeiroHorario> Horarios)> _barberCache = new();

    private (Barbeiro? Barber, List<BarbeiroHorario> Horarios) LoadBarberData(int barberId)
    {
        if (_barberCache.TryGetValue(barberId, out var cached)) return cached;
        var barber = _db.Set<Barbeiro>().AsNoTracking().FirstOrDefault(b => b.Id == barberId && b.Ativo);
        var horarios = _db.Set<BarbeiroHorario>().AsNoTracking()
            .Where(h => h.BarbeiroId == barberId).ToList();
        var result = (barber, horarios);
        _barberCache[barberId] = result;
        return result;
    }

    private static readonly TimeSpan LegacyTemplateStart = new(8, 0, 0);
    private static readonly TimeSpan LegacyTemplateEnd = new(18, 0, 0);
    private static readonly TimeSpan LegacySecondTemplateStart = new(9, 0, 0);
    private static readonly TimeSpan LegacySecondTemplateEnd = new(19, 0, 0);

    /// <summary>
    /// Intervalo de almoço universal: 12:00–13:00 (exclusive).
    /// Bloqueia os slots das 12:00 e das 12:30 para todos os profissionais.
    /// Configuração por profissional foi removida em favor deste padrão fixo.
    /// </summary>
    private static readonly TimeSpan UniversalLunchStart = new(12, 0, 0);
    private static readonly TimeSpan UniversalLunchEnd   = new(13, 0, 0);

    public AgendaService(AppDbContext db, BusinessHours hours, ServiceCatalogService catalog, IMemoryCache cache)
    {
        _db = db;
        _hours = hours;
        _catalog = catalog;
        _cache = cache;
    }

    /// <summary>
    /// Retorna os horários disponíveis para a data/serviço/profissional.
    /// </summary>
    /// <param name="useCache">
    /// Quando true, usa um cache de curta duração (paginação do bot). NÃO use para validar
    /// a disponibilidade final de um agendamento — para isso, deixe false (dados sempre frescos).
    /// </param>
    /// <summary>Retorna horários disponíveis para o serviço (por ID inteiro, não enum).</summary>
    public List<TimeSpan> GetHorariosDisponiveis(DateTime date, int serviceId, int? barberId = null, bool useCache = false)
    {
        if (!useCache)
            return ComputeHorariosDisponiveis(date, serviceId, barberId);

        var normalized = NormalizeBusinessDate(date);
        var key = $"horarios|{_db.TenantId}|{normalized:yyyyMMdd}|{serviceId}|{barberId?.ToString() ?? "none"}";
        return _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = PaginationCacheTtl;
            return ComputeHorariosDisponiveis(date, serviceId, barberId);
        })!;
    }

    private List<TimeSpan> ComputeHorariosDisponiveis(DateTime date, int serviceId, int? barberId = null)
    {
        var availableSlots = new List<TimeSpan>();
        date = NormalizeBusinessDate(date);
        if (IsDateUnavailable(date, barberId)) return availableSlots;

        var info = _catalog.Get(serviceId);
        if (info == null) return availableSlots;
        int durationMinutes = info.DurationMinutes;

        // Serviço NÃO-BLOQUEANTE (ex.: polimento): não ocupa vaga nem bloqueia horários futuros.
        var serviceOccupies = info.OccupiesSlot;
        var nonBlockingIds = _catalog.GetAll(includeInactive: true)
            .Where(s => !s.OccupiesSlot).Select(s => s.Id).ToHashSet();

        var nowLocal = GetBrazilNow();
        if (date.Date < nowLocal.Date) return availableSlots;

        // Limite diário por serviço — lê a chave por-loja via Store_{id}_LimitService_{sid}
        // com fallback para a chave global LimitService_{sid}.
        var limitKey      = $"LimitService_{serviceId}";
        var storeId       = _db.TenantId;
        var limitKeyStore = $"Store_{storeId}_{limitKey}";
        var limitStr = _db.Database.SqlQueryRaw<string>(
            "SELECT Value FROM SystemConfigs WHERE Key = {0}", limitKeyStore).FirstOrDefault()
            ?? _db.Database.SqlQueryRaw<string>(
            "SELECT Value FROM SystemConfigs WHERE Key = {0}", limitKey).FirstOrDefault();

        if (int.TryParse(limitStr, out int limit) && limit > 0)
        {
            int currentCount = _db.Appointments.Count(a => a.DateTime.Date == date.Date && a.ServiceId == serviceId && a.Status == "ativo");
            if (currentCount >= limit) return availableSlots;
        }

        var query = _db.Appointments.AsNoTracking().Where(a => a.DateTime.Date == date.Date && a.Status == "ativo");
        if (barberId.HasValue)
            query = query.Where(a => a.BarberId == barberId.Value);

        var existingAppointments = query
            .Select(a => new { a.DateTime, a.DuracaoMinutos, a.ServiceId })
            .ToList()
            // Agendamentos não-bloqueantes (polimento) NÃO contam para ocupação do horário.
            .Where(a => !nonBlockingIds.Contains(a.ServiceId))
            .ToList();

        int capacity = barberId.HasValue ? 1 : GetStoreWideCapacity();

        var blockedSlots = new HashSet<TimeSpan>();

        // Carrega horários e verifica disponibilidade do barbeiro para esta data.
        // Usa LoadBarberData (cache por instância) para evitar N queries repetidas quando
        // este método é chamado em loop para múltiplos dias (enquete de datas do bot).
        List<BarbeiroHorario> horarios = new();
        if (barberId.HasValue)
        {
            var (barber, barberHorarios) = LoadBarberData(barberId.Value);
            horarios = barberHorarios;

            if (barber == null || !WorksOnDay(barber, date, horarios)) return availableSlots;
            blockedSlots = GetBlockedSlots(barber, date);
        }

        // Não-bloqueante: usa um "pé" de 30min só para iterar os horários (não precisa caber até o fim
        // do expediente nem reservar a duração real — pode ser selecionado mesmo perto do fechamento).
        var serviceDuration = TimeSpan.FromMinutes(serviceOccupies ? durationMinutes : 30);
        foreach (var segment in GetScheduleSegments(date, barberId, horarios))
        {
            var currentSlot = segment.Start;
            while (currentSlot + serviceDuration <= segment.End)
            {
                if (date.Date == nowLocal.Date && currentSlot < nowLocal.TimeOfDay.Add(TimeSpan.FromMinutes(5)))
                {
                    currentSlot = currentSlot.Add(TimeSpan.FromMinutes(30));
                    continue;
                }

                if (blockedSlots.Contains(currentSlot) || OverlapsLunch(currentSlot, serviceDuration, UniversalLunchStart, UniversalLunchEnd))
                {
                    currentSlot = currentSlot.Add(TimeSpan.FromMinutes(30));
                    continue;
                }

                // Não-bloqueante sempre disponível (não consome capacidade).
                // Bloqueante: precisa ter vaga (overlap < capacidade da loja).
                var disponivel = !serviceOccupies;
                if (!disponivel)
                {
                    var overlappingCount = existingAppointments.Count(a =>
                        currentSlot < a.DateTime.TimeOfDay.Add(TimeSpan.FromMinutes(a.DuracaoMinutos)) &&
                        currentSlot + serviceDuration > a.DateTime.TimeOfDay);
                    disponivel = overlappingCount < capacity;
                }

                if (disponivel)
                {
                    availableSlots.Add(currentSlot);
                }

                currentSlot = currentSlot.Add(TimeSpan.FromMinutes(30));
            }
        }

        return availableSlots.Distinct().OrderBy(h => h).ToList();
    }

    /// <summary>
    /// Capacidade de atendimentos simultâneos da loja (quando não é por profissional).
    /// Lava-jato: até N por horário (config "Store_{id}_Capacidade", default 3).
    /// Barbearia: número de profissionais ativos (mínimo 1).
    /// </summary>
    private int GetStoreWideCapacity()
    {
        var storeId = _db.TenantId;
        var businessType = _db.Stores.AsNoTracking()
            .Where(s => s.Id == storeId)
            .Select(s => (BusinessType?)s.BusinessType)
            .FirstOrDefault();

        if (businessType is BusinessType.CarWash or BusinessType.Pizzeria or BusinessType.ComputerOptimization)
        {
            var capStr = _db.Database
                .SqlQueryRaw<string>("SELECT Value FROM SystemConfigs WHERE Key = {0}", $"Store_{storeId}_Capacidade")
                .FirstOrDefault();
            if (int.TryParse(capStr, out var c) && c > 0) return c;
            if (businessType == BusinessType.ComputerOptimization) return 4;
            return businessType == BusinessType.Pizzeria ? 8 : 3;
        }

        var barbers = _db.Set<Barbeiro>().Count(b => b.Ativo);
        return barbers > 0 ? barbers : 1;
    }

    public ScheduleWindow GetScheduleWindow(DateTime date, int? barberId = null)
    {
        date = NormalizeBusinessDate(date);
        var workStart = _hours.OpeningTime;
        var workEnd   = _hours.ClosingTime;
        string? barberName = null;

        List<BarbeiroHorario> horarios = new();
        if (barberId.HasValue)
        {
            var barber = _db.Set<Barbeiro>().AsNoTracking().FirstOrDefault(b => b.Id == barberId.Value && b.Ativo);
            horarios = _db.Set<BarbeiroHorario>().AsNoTracking()
                .Where(h => h.BarbeiroId == barberId.Value)
                .ToList();

            if (barber != null)
            {
                barberName = barber.Nome;
                var hours = GetHoursForDate(barber, date, horarios);
                workStart = hours.Start;
                workEnd   = hours.End;
            }
        }

        var segments = GetScheduleSegments(date, barberId, horarios);
        if (segments.Count > 0)
        {
            workStart = segments.Min(s => s.Start);
            workEnd   = segments.Max(s => s.End);
        }

        // Almoço universal fixo: 12:00–12:59. Não depende de configuração por profissional.
        return new ScheduleWindow(workStart, workEnd, UniversalLunchStart, UniversalLunchEnd, barberName);
    }

    public bool IsSlotAvailable(DateTime dateTime, int serviceId, int? barberId = null)
    {
        var localDateTime = NormalizeBusinessDateTime(dateTime);
        if (IsDateUnavailable(localDateTime.Date, barberId)) return false;
        var slots = GetHorariosDisponiveis(localDateTime.Date, serviceId, barberId);
        return slots.Contains(new TimeSpan(localDateTime.Hour, localDateTime.Minute, 0));
    }

    public bool IsDateUnavailable(DateTime date, int? barberId = null)
    {
        var targetDate = NormalizeBusinessDate(date);
        var query = _db.UnavailableDays.AsNoTracking().Where(d => d.Date.Date == targetDate);

        if (barberId.HasValue)
            query = query.Where(d => d.BarberId == null || d.BarberId == barberId.Value);
        else
            query = query.Where(d => d.BarberId == null);

        return query.Any();
    }

    public UnavailableDay? GetUnavailableDay(DateTime date, int? barberId = null)
    {
        var targetDate = NormalizeBusinessDate(date);
        var query = _db.UnavailableDays.AsNoTracking().Where(d => d.Date.Date == targetDate);

        if (barberId.HasValue)
            query = query.Where(d => d.BarberId == null || d.BarberId == barberId.Value)
                .OrderByDescending(d => d.BarberId.HasValue);
        else
            query = query.Where(d => d.BarberId == null);

        return query.FirstOrDefault();
    }

    public int GetServiceDuration(int serviceId, int fallback)
        => _catalog.Get(serviceId, includeInactive: true)?.DurationMinutes ?? fallback;

    public static DateTime GetBrazilNow() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, GetBrazilTimeZone());

    private static DateTime NormalizeBusinessDate(DateTime date) =>
        NormalizeBusinessDateTime(date).Date;

    private static DateTime NormalizeBusinessDateTime(DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Utc)
            return TimeZoneInfo.ConvertTimeFromUtc(dateTime, GetBrazilTimeZone());

        return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, dateTime.Hour, dateTime.Minute, 0, DateTimeKind.Unspecified);
    }

    private static TimeZoneInfo GetBrazilTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
    }

    /// <param name="horarios">Grade semanal estruturada. Se não-vazia, tem precedência sobre WorkingDays.</param>
    private static bool WorksOnDay(Barbeiro barber, DateTime date,
        IReadOnlyList<BarbeiroHorario>? horarios = null)
    {
        var day = (int)date.DayOfWeek;

        // Prioridade 1: BarbeiroHorario — se há entradas para este barbeiro, usa exclusivamente.
        if (horarios != null && horarios.Count > 0)
        {
            var h = horarios.FirstOrDefault(x => x.DiaSemana == day);
            // Se há entrada para o dia: Folga determina disponibilidade.
            // Se não há entrada para o dia: sem registro = folga implícita.
            return h != null && !h.Folga;
        }

        // Prioridade 2: WorkingDays legado (string CSV "1,2,3,4,5,6")
        return barber.WorkingDays
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var value) ? value : -1)
            .Contains(day);
    }

    /// <summary>
    /// Determina o início e fim do expediente para um barbeiro numa data específica.
    /// Nota: horário de almoço não é mais configurável por profissional — é fixo (UniversalLunchStart/End).
    /// </summary>
    private (TimeSpan Start, TimeSpan End, TimeSpan? LunchStart, TimeSpan? LunchEnd) GetHoursForDate(
        Barbeiro barber, DateTime date, IReadOnlyList<BarbeiroHorario>? horarios = null)
    {
        // Prioridade 1: data específica em CustomHoursJson (exceções / feriados)
        var key = date.ToString("yyyy-MM-dd");
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(barber.CustomHoursJson) ? "{}" : barber.CustomHoursJson);
            if (doc.RootElement.TryGetProperty(key, out var custom))
            {
                var start2 = _hours.OpeningTime;
                var end2   = _hours.ClosingTime;
                if (custom.TryGetProperty("start", out var startEl) && TimeSpan.TryParse(startEl.GetString(), out var cs)) start2 = cs;
                if (custom.TryGetProperty("end",   out var endEl)   && TimeSpan.TryParse(endEl.GetString(),   out var ce)) end2   = ce;
                return (start2, end2, null, null);
            }
        }
        catch { }

        // Prioridade 2: BarbeiroHorario (grade semanal estruturada)
        if (horarios != null && horarios.Count > 0)
        {
            var h = horarios.FirstOrDefault(x => x.DiaSemana == (int)date.DayOfWeek);
            if (h != null && !h.Folga)
                return (h.Entrada, h.Saida, null, null);
        }

        // Prioridade 3: WorkStart/WorkEnd do barbeiro (campo legado)
        if (HasProfessionalScheduleOverride(barber))
            return (barber.WorkStart, barber.WorkEnd, null, null);

        // Prioridade 4: horário padrão da loja
        return (_hours.OpeningTime, _hours.ClosingTime, null, null);
    }

    /// <param name="horarios">
    /// Registros de BarbeiroHorario pré-carregados pelo chamador para evitar query duplicada.
    /// Se null, são carregados internamente — use apenas quando este método é chamado isoladamente.
    /// </param>
    public List<ScheduleSegment> GetScheduleSegments(DateTime date, int? barberId = null,
        IReadOnlyList<BarbeiroHorario>? horarios = null)
    {
        date = NormalizeBusinessDate(date);
        if (!barberId.HasValue)
            return new List<ScheduleSegment> { new(_hours.OpeningTime, _hours.ClosingTime, null, null) };

        // Usa LoadBarberData (cache por instância) para evitar query extra ao banco quando
        // GetScheduleSegments é chamado logo após ComputeHorariosDisponiveis para o mesmo barbeiro.
        var (barber, cachedHorarios) = LoadBarberData(barberId.Value);

        // Horários passados pelo chamador têm precedência (já pré-carregados); caso contrário usa cache.
        var horariosResolvidos = (IReadOnlyList<BarbeiroHorario>)(horarios ?? cachedHorarios);

        if (barber == null || !WorksOnDay(barber, date, horariosResolvidos))
            return new List<ScheduleSegment>();

        // ── Prioridade 1: data específica em CustomHoursJson (feriados, exceções) ──────────
        var dateSegments = TryReadSingleKey(barber.CustomHoursJson, date.ToString("yyyy-MM-dd"));
        if (dateSegments != null) return dateSegments;

        // ── Prioridade 2: BarbeiroHorario — grade semanal estruturada ─────────────────────
        var horario = horariosResolvidos.FirstOrDefault(h => h.DiaSemana == (int)date.DayOfWeek);
        if (horario != null)
        {
            // Folga já tratada por WorksOnDay → se chegou aqui, Folga == false
            // LunchStart/LunchEnd são null aqui pois o almoço é fixo (UniversalLunchStart/End)
            return new List<ScheduleSegment> { new(horario.Entrada, horario.Saida, null, null) };
        }

        // ── Prioridade 3: dia-da-semana em CustomHoursJson (legado JSON) ─────────────────
        // ── Prioridade 4: WorkStart/WorkEnd + horário da loja (fallback) ─────────────────
        var legacySegments = TryReadCustomSegments(barber.CustomHoursJson, date);
        if (legacySegments.Count > 0) return legacySegments;

        var fallback = GetHoursForDate(barber, date, horariosResolvidos);
        return new List<ScheduleSegment> { new(fallback.Start, fallback.End, fallback.LunchStart, fallback.LunchEnd) };
    }

    /// <summary>
    /// Lê segmentos de uma única chave exata no CustomHoursJson.
    /// Retorna null se a chave não existir, lista vazia se existir mas marcar "closed: true",
    /// ou a lista de segmentos encontrada.
    /// Usado para ler exceções de data específica sem cair nas chaves de dia-da-semana.
    /// </summary>
    private List<ScheduleSegment>? TryReadSingleKey(string? customHoursJson, string key)
    {
        if (string.IsNullOrWhiteSpace(customHoursJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(customHoursJson);
            if (!doc.RootElement.TryGetProperty(key, out var custom)) return null;

            if (custom.TryGetProperty("closed", out var closedEl) && closedEl.ValueKind == JsonValueKind.True)
                return new List<ScheduleSegment>(); // explicitamente fechado nesta data

            if (custom.TryGetProperty("shifts", out var shiftsEl) && shiftsEl.ValueKind == JsonValueKind.Array)
            {
                var result = new List<ScheduleSegment>();
                foreach (var shift in shiftsEl.EnumerateArray())
                    if (TryReadSegment(shift, out var seg)) result.Add(seg);
                return result.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
            }

            if (TryReadSegment(custom, out var single))
                return new List<ScheduleSegment> { single };
        }
        catch { }
        return null;
    }

    private List<ScheduleSegment> TryReadCustomSegments(string? customHoursJson, DateTime date)
    {
        var result = new List<ScheduleSegment>();
        if (string.IsNullOrWhiteSpace(customHoursJson)) return result;

        var keys = new[]
        {
            date.ToString("yyyy-MM-dd"),
            ((int)date.DayOfWeek).ToString(),
            date.DayOfWeek.ToString().ToLowerInvariant()
        };

        try
        {
            using var doc = JsonDocument.Parse(customHoursJson);
            JsonElement custom = default;
            var found = keys.Any(key => doc.RootElement.TryGetProperty(key, out custom));
            if (!found) return result;

            if (custom.TryGetProperty("closed", out var closedEl) && closedEl.ValueKind == JsonValueKind.True)
                return result;

            if (custom.TryGetProperty("shifts", out var shiftsEl) && shiftsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var shift in shiftsEl.EnumerateArray())
                {
                    if (TryReadSegment(shift, out var segment)) result.Add(segment);
                }
            }
            else if (TryReadSegment(custom, out var single))
            {
                result.Add(single);
            }
        }
        catch
        {
        }

        return result.Where(s => s.End > s.Start).OrderBy(s => s.Start).ToList();
    }

    private static bool TryReadSegment(JsonElement source, out ScheduleSegment segment)
    {
        segment = default;
        if (!source.TryGetProperty("start", out var startEl) ||
            !source.TryGetProperty("end",   out var endEl)   ||
            !TimeSpan.TryParse(startEl.GetString(), out var start) ||
            !TimeSpan.TryParse(endEl.GetString(),   out var end))
            return false;

        // lunchStart/lunchEnd ignorados — almoço é fixo (UniversalLunchStart/End)
        segment = new ScheduleSegment(start, end, null, null);
        return end > start;
    }

    private static bool HasProfessionalScheduleOverride(Barbeiro barber)
    {
        if (barber.WorkEnd <= barber.WorkStart) return false;

        var looksLikeOldTemplate =
            (barber.WorkStart == LegacyTemplateStart && barber.WorkEnd == LegacyTemplateEnd) ||
            (barber.WorkStart == LegacySecondTemplateStart && barber.WorkEnd == LegacySecondTemplateEnd);

        return !looksLikeOldTemplate;
    }

    private static HashSet<TimeSpan> GetBlockedSlots(Barbeiro barber, DateTime date)
    {
        var result = new HashSet<TimeSpan>();
        var key = date.ToString("yyyy-MM-dd");

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(barber.BlockedSlotsJson) ? "{}" : barber.BlockedSlotsJson);
            if (doc.RootElement.TryGetProperty(key, out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var slot in slots.EnumerateArray())
                {
                    if (TimeSpan.TryParse(slot.GetString(), out var time)) result.Add(time);
                }
            }
        }
        catch { }

        return result;
    }

    private static bool OverlapsLunch(TimeSpan start, TimeSpan duration, TimeSpan? lunchStart, TimeSpan? lunchEnd)
    {
        if (!lunchStart.HasValue || !lunchEnd.HasValue || lunchEnd <= lunchStart) return false;
        var end = start + duration;
        return start < lunchEnd.Value && end > lunchStart.Value;
    }
}

public record ScheduleWindow(TimeSpan WorkStart, TimeSpan WorkEnd, TimeSpan? LunchStart, TimeSpan? LunchEnd, string? BarberName);
public readonly record struct ScheduleSegment(TimeSpan Start, TimeSpan End, TimeSpan? LunchStart, TimeSpan? LunchEnd);
