namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Horário semanal de um profissional: um registro por dia da semana (0=Dom … 6=Sáb).
/// Quando existe ao menos uma entrada para um barbeiro, estes registros têm prioridade
/// sobre WorkStart/WorkEnd/WorkingDays do próprio Barbeiro (que servem de fallback para
/// profissionais que ainda não têm grade configurada).
///
/// Prioridade no AgendaService:
///   1. CustomHoursJson com chave de data específica ("yyyy-MM-dd") — dias excepcionais
///   2. BarbeiroHorario para o DiaSemana — grade semanal estruturada  ← ESTE modelo
///   3. CustomHoursJson com chave de dia-da-semana ("1"…"6") — legado JSON
///   4. Barbeiro.WorkStart / WorkEnd (HasProfessionalScheduleOverride) — legado global
///   5. BusinessHours da loja — padrão da loja
/// </summary>
public class BarbeiroHorario
{
    public int Id { get; set; }

    /// <summary>FK para Barbeiro.Id</summary>
    public int BarbeiroId { get; set; }

    /// <summary>Necessário para o query filter de multi-tenant.</summary>
    public int StoreId { get; set; }

    /// <summary>
    /// Dia da semana seguindo DayOfWeek: 0=Domingo, 1=Segunda, …, 6=Sábado.
    /// Unique(BarbeiroId, DiaSemana) garante um registro por dia.
    /// </summary>
    public int DiaSemana { get; set; }

    /// <summary>true = folga neste dia (não aparece disponível no bot nem na agenda).</summary>
    public bool Folga { get; set; } = false;

    public TimeSpan Entrada { get; set; } = new TimeSpan(8, 0, 0);
    public TimeSpan Saida { get; set; } = new TimeSpan(18, 0, 0);

    public TimeSpan? InicioAlmoco { get; set; }
    public TimeSpan? FimAlmoco { get; set; }
}
