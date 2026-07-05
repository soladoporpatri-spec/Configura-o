/// <summary>
/// CONFIGURAÇÃO DA AGENDA: POCO carregado do appsettings.json (Agenda section).
/// Define expediente, dias, step de slots e limite por horário.
/// Injetado via IOptions em AgendaService.
/// Exemplo config:
/// "Agenda": {
///   "HorarioAbertura": "HH:mm:ss",
///   "HorarioFechamento": "HH:mm:ss",
///   "DuracaoAtendimentoMinutos": 30,
///   "DiasAtendimento": [1,2,3,4,5], // seg-sex
///   "LimiteAgendamentosPorHorario": 1
/// }
/// </summary>
namespace WhatsAppBot.Worker.Models;

public class AgendaConfig
{
    public TimeSpan HorarioAbertura { get; set; }
    public TimeSpan HorarioFechamento { get; set; }
    public int DuracaoAtendimentoMinutos { get; set; }
    public List<int> DiasAtendimento { get; set; } = new();
    public int LimiteAgendamentosPorHorario { get; set; } = 1;
}
