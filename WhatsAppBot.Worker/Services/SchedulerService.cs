using System.Collections.Concurrent;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// SCHEDULER SERVICE: CRUD Appointments + validação overlap.
/// Chamado pelo bot (save/list/cancel/reschedule) + Reminder.
/// Usa AppDbContext singleton.
/// Docs existentes expandidas.
/// </summary>
namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Serviço para CRUD de agendamentos (Appointments)
/// Verificação de disponibilidade, save/update/delete
/// </summary>
public class SchedulerService
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notificationService;
    private readonly AgendaService _agenda;
    private readonly ServiceCatalogService _catalog;
    private readonly ILogger<SchedulerService> _logger;
    // FIX 5: semáforos por-loja em vez de um semáforo global.
    // O semáforo global serializava agendamentos de TODAS as lojas numa única fila:
    // um SaveAsync lento de Loja 1 bloqueava o SaveAsync da Loja 2, sem nenhuma relação.
    // Com ConcurrentDictionary<storeId, SemaphoreSlim>:
    //   - Lojas diferentes correm em paralelo (sem serialização desnecessária entre si).
    //   - Dentro de cada loja, o anti double-booking continua serializado (1 por loja).
    // GetOrAdd é thread-safe: se duas threads tentarem criar a chave ao mesmo tempo,
    // apenas um SemaphoreSlim vence e o outro é descartado — sem race condition.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _semaphores = new();

    /// <summary>
    /// Construtor com injeção de dependências
    /// </summary>
    public SchedulerService(AppDbContext db, NotificationService notificationService, AgendaService agenda, ServiceCatalogService catalog, ILogger<SchedulerService> logger)
    {
        _db = db;
        _notificationService = notificationService;
        _agenda = agenda;
        _catalog = catalog;
        _logger = logger;
    }

    /// Verifica se slot está disponível considerando overlapping com outros agendamentos
    /// para um barbeiro específico ou capacidade total da loja.
    /// </summary>
    /// <summary>Verifica se o horário está disponível para o serviço (ID inteiro, não enum).</summary>
    public bool IsSlotAvailable(DateTime dateTime, int serviceId, int? barberId = null)
        => _agenda.IsSlotAvailable(dateTime, serviceId, barberId);

    /// <summary>
    /// Salva novo agendamento no banco com verificação atômica de disponibilidade
    /// </summary>
    // barberId é nullable: no fluxo CarWash (lava-jato) não há profissional/box selecionado,
    // então a disponibilidade é verificada por capacidade da loja (barberId == null).
    /// <summary>
    /// Salva novo agendamento com verificação atômica de disponibilidade.
    /// <paramref name="serviceId"/> é o ID inteiro do serviço — não limitado ao enum TipoServico.
    /// </summary>
    public async Task<Appointment?> SaveAsync(int storeId, string phone, string name, DateTime dateTime,
        int serviceId, int duracao, decimal preco, int? barberId, string? barberName = null, string? notes = null,
        string origin = "WhatsApp/Bot", string? vehicleInfo = null, bool isWalkIn = false,
        CancellationToken cancellationToken = default)
    {
        // Resultado preparado dentro da seção crítica e notificado FORA dela.
        Appointment? createdAppt = null;
        string createdServiceName = "";

        var sem = _semaphores.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);
        try
        {
            var existingDuplicate = await _db.Appointments
                .AsNoTracking()
                .FirstOrDefaultAsync(a =>
                    a.StoreId == storeId &&
                    a.PhoneNumber == phone &&
                    a.DateTime == dateTime &&
                    a.BarberId == barberId &&
                    a.ServiceId == serviceId &&
                    a.Status == "ativo",
                    cancellationToken);

            if (existingDuplicate != null)
            {
                _logger.LogInformation(
                    "Agendamento duplicado tratado de forma idempotente: {Phone} em {DateTime} (Store: {StoreId}, Barber: {BarberId})",
                    phone, dateTime, storeId, barberId);
                return existingDuplicate;
            }

            // Walk-in: cliente está fisicamente presente — não verifica disponibilidade.
            // Lembretes de D-1 e 1h serão suprimidos pelo ReminderService (IsWalkIn=true).
            if (!isWalkIn && !IsSlotAvailable(dateTime, serviceId, barberId))
                return null;

            var catalogInfo = _catalog.Get(serviceId);
            if (catalogInfo == null)
            {
                _logger.LogWarning("Tentativa de agendamento para servico inativo: ServiceId={ServiceId}", serviceId);
                return null;
            }

            duracao = catalogInfo.DurationMinutes;
            preco   = catalogInfo.Price;

            var appt = new Appointment
            {
                StoreId           = storeId,
                PhoneNumber       = phone,
                ContactName       = name,
                Notes             = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                DateTime          = dateTime,
                ServiceId         = serviceId,
                DuracaoMinutos    = duracao,
                Preco             = preco,
                BarberId          = barberId,
                BarberName        = barberName,
                CreatedAt         = DateTime.Now,
                PresencaConfirmada = false,
                ReminderSent      = false,
                ReminderDayBefore = false,
                ReminderOneHour   = false,
                ThanksSent        = false,
                VehicleInfo       = string.IsNullOrWhiteSpace(vehicleInfo) ? null : vehicleInfo.Trim(),
                IsWalkIn          = isWalkIn
            };

            _db.Appointments.Add(appt);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Novo agendamento salvo: {Name} para {DateTime} (Store: {StoreId})", name, dateTime, storeId);

            createdAppt = appt;
            createdServiceName = catalogInfo.Name;
        }
        finally
        {
            // Libera o lock da loja ANTES de qualquer I/O de notificação.
            // O semáforo serializa apenas a checagem de disponibilidade + insert (anti double-booking);
            // mantê-lo durante SignalR/e-mail congelaria TODAS as confirmações daquela loja.
            sem.Release();
        }

        // Notificação fora da seção crítica e com timeout defensivo:
        // um dashboard SignalR lento ou o SendGrid travado NUNCA pode congelar a confirmação do bot.
        if (createdAppt != null)
            await NotifyNewAppointmentSafeAsync(createdAppt, createdServiceName, origin, cancellationToken);

        return createdAppt;
    }

    /// <summary>
    /// Dispara a notificação de novo agendamento de forma segura: fora do lock global e
    /// limitada por timeout. Falha/lentidão de notificação não invalida o agendamento já criado
    /// nem bloqueia a resposta de confirmação ao cliente.
    /// </summary>
    private async Task NotifyNewAppointmentSafeAsync(Appointment appt, string serviceName, string origin, CancellationToken ct)
    {
        try
        {
            var notifyTask = _notificationService.NotifyNewAppointment(
                appt.ContactName,
                appt.DateTime,
                serviceName,
                appt.PhoneNumber,
                appt.BarberName ?? "",
                origin,
                appt.Id);

            // NotifyNewAppointment já trata exceções internamente; aqui limitamos apenas o TEMPO.
            var completed = await Task.WhenAny(notifyTask, Task.Delay(TimeSpan.FromSeconds(8), ct));
            if (completed != notifyTask)
            {
                _logger.LogWarning(
                    "Notificacao do agendamento #{ApptId} excedeu o tempo limite; seguindo sem bloquear a confirmacao.",
                    appt.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao notificar novo agendamento #{ApptId}; agendamento permanece valido.", appt.Id);
        }
    }

    /// <summary>Overload de compatibilidade com barberId não-nullable.</summary>
    public Task<Appointment?> SaveAsync(int storeId, string phone, string name, DateTime dateTime,
        int serviceId, int duracao, decimal preco, int barberId, string? barberName, CancellationToken cancellationToken)
        => SaveAsync(storeId, phone, name, dateTime, serviceId, duracao, preco, (int?)barberId, barberName, null, "WhatsApp/Bot", null, false, cancellationToken);

    /// <summary>
    /// Lista agendamentos do cliente, ordenados pelo mais recente.
    /// activeOnly=true → apenas Status=="ativo" (para exibição ao cliente no bot).
    /// activeOnly=false → todos (para resolução de nome por histórico).
    /// </summary>
    public List<Appointment> GetByPhone(string phone, bool activeOnly = false)
    {
        var q = _db.Appointments
            .AsNoTracking()
            .Where(a => a.PhoneNumber == phone);
        if (activeOnly)
            q = q.Where(a => a.Status == "ativo");
        return q.OrderByDescending(a => a.DateTime).ToList();
    }

    /// <summary>
    /// Próximo agendamento do cliente
    /// </summary>
    public Appointment? GetProximoByPhone(string phone)
    {
        var now = AgendaService.GetBrazilNow();
        return _db.Appointments
            .AsNoTracking()
            .Where(a => a.PhoneNumber == phone && a.DateTime >= now && a.Status == "ativo")
            .OrderBy(a => a.DateTime)
            .FirstOrDefault();
    }

    /// <summary>
    /// Busca agendamento por ID
    /// </summary>
    public async Task<Appointment?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _db.Appointments.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <summary>
    /// Atualiza agendamento existente
    /// </summary>
    public async Task UpdateAsync(Appointment appt, CancellationToken cancellationToken = default)
    {
        _db.Appointments.Update(appt);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reagenda um agendamento de forma ATÔMICA, sob o mesmo semáforo global de SaveAsync.
    /// Sem isto, o reagendamento (dashboard) checava disponibilidade FORA da seção crítica e
    /// podia gravar concorrentemente com um SaveAsync do bot → double-booking no mesmo horário.
    /// A re-checagem de conflito acontece DENTRO do lock, garantindo serialização contra novos
    /// agendamentos. Exclui o próprio registro do cálculo (permite mover dentro da agenda).
    /// </summary>
    /// <returns>(true, null) em sucesso; (false, motivo) em conflito ou dia indisponível.</returns>
    public async Task<(bool Ok, string? Error)> RescheduleAsync(int appointmentId, DateTime newDateTime, CancellationToken cancellationToken = default)
    {
        // RescheduleAsync: usa _db.TenantId que já foi definido pelo caller (Worker/Endpoint)
        // antes de invocar este método. Fallback para 1 caso não tenha sido definido (não deve ocorrer).
        var reschedSem = _semaphores.GetOrAdd(_db.TenantId > 0 ? _db.TenantId : 1, _ => new SemaphoreSlim(1, 1));
        await reschedSem.WaitAsync(cancellationToken);
        try
        {
            var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
            if (appt == null) return (false, "Agendamento nao encontrado");
            if (newDateTime == appt.DateTime) return (true, null); // nada a fazer

            // Dia bloqueado para a loja ou para o barbeiro?
            if (_agenda.GetUnavailableDay(newDateTime, appt.BarberId) != null)
                return (false, "Dia indisponivel");

            // Conflito: existe OUTRO agendamento (Id diferente) sobrepondo o novo horário,
            // para o mesmo barbeiro, no mesmo dia. Leitura fresca AsNoTracking dentro do lock.
            var newEnd = newDateTime.AddMinutes(appt.DuracaoMinutos);
            var sameDay = await _db.Appointments.AsNoTracking()
                .Where(a => a.Id != appointmentId
                         && a.StoreId == appt.StoreId
                         && a.BarberId == appt.BarberId
                         && a.DateTime.Date == newDateTime.Date
                         && a.Status == "ativo")
                .Select(a => new { a.DateTime, a.DuracaoMinutos })
                .ToListAsync(cancellationToken);

            bool conflito = sameDay.Any(a =>
                newDateTime < a.DateTime.AddMinutes(a.DuracaoMinutos) && newEnd > a.DateTime);
            if (conflito) return (false, "Horario indisponivel");

            appt.DateTime = newDateTime;
            appt.ReminderDayBefore = false;
            appt.ReminderOneHour = false;
            appt.PresencaConfirmada = false;
            await _db.SaveChangesAsync(cancellationToken);
            return (true, null);
        }
        finally
        {
            reschedSem.Release();
        }
    }

    /// <summary>
    /// Cancela agendamento via soft-delete: marca Status = "cancelado" (mantém histórico).
    /// </summary>
    /// <param name="cancelledBy">"bot" | "dashboard" | nome do operador</param>
    public async Task CancelAsync(int id, string cancelledBy, CancellationToken cancellationToken = default)
    {
        var appt = await _db.Appointments.FindAsync(new object[] { id }, cancellationToken);
        if (appt != null)
        {
            var name = appt.ContactName;
            var dt   = appt.DateTime;
            var svc  = _catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}";

            appt.Status      = "cancelado";
            appt.CancelledAt = DateTime.Now;
            appt.CancelledBy = cancelledBy;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Agendamento #{Id} cancelado por {CancelledBy} ({Name} {Dt:dd/MM/yyyy HH:mm})",
                id, cancelledBy, name, dt);
            await _notificationService.NotifyAppointmentCancelled(name, dt, svc, appt.Id);
        }
    }

    /// <summary>
    /// Remove fisicamente o agendamento (mantido apenas para compatibilidade interna).
    /// Prefer CancelAsync para cancelamentos de clientes/dashboard.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var appt = await _db.Appointments.FindAsync(new object[] { id }, cancellationToken);
        if (appt != null)
        {
            var name = appt.ContactName;
            var dt   = appt.DateTime;
            var svc  = _catalog.Get(appt.ServiceId, includeInactive: true)?.Name ?? $"Serviço #{appt.ServiceId}";

            _db.Appointments.Remove(appt);
            await _db.SaveChangesAsync(cancellationToken);

            await _notificationService.NotifyAppointmentCancelled(name, dt, svc);
        }
    }
}
