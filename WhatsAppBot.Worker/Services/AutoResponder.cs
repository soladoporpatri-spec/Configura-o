using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

public static class AutoResponder
{
    // ── Mensagens em uso ─────────────────────────────────────────────────────

    public static string AskName => "Qual e o seu nome completo?";

    public static string AskVehicle => "🚗 Qual o veiculo? Informe modelo e cor.\nEx: Onix prata, Civic preto, Honda CG vermelha.";

    public static string AskTime => "⏰ Escolha o horario desejado na lista abaixo:";

    public static string NoAgendamentos =>
        "Nenhum agendamento futuro encontrado.\n\nDigite *oi* para marcar um horario.";

    public static string ReminderDayBefore(DateTime dt, string servico) =>
        $"Lembrete para amanha!\n\n{servico}\nHorario: {dt:HH:mm}\n\n" +
        "Confirme presenca respondendo *sim* ou *nao*.";

    public static string ReminderOneHour(DateTime dt, string servico) =>
        $"Falta 1 hora!\n\n{servico}\nHorario: {dt:HH:mm}\nChegue 5 minutos antes.\n\nTe esperamos!";

    public static string ConfirmacaoPresenca(DateTime dt, string servico) =>
        $"Voce confirma presenca amanha?\n\n{servico}\nHorario: {dt:HH:mm}\n\nResponda *sim* ou *nao*.";
}
