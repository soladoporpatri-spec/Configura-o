using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

/// <summary>
/// MODELOS DE SERVIÇOS: Enum dos tipos de serviço da barbearia + utilitário com preços/durações.
/// Usado em Appointment.Servico e para gerar menu/formatação no bot.
/// Carregado estaticamente - dados hard-coded (fácil manutenção).
/// </summary>

namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Tipos de serviços oferecidos pela barbearia.
/// Valores numéricos para input do usuário (1-6 no menu).
/// </summary>
public enum TipoServico
{
    Corte = 1,
    Barba = 2,
    Sobrancelha = 3,
    CorteBarba = 4,
    CorteSobrancelha = 5,
    CorteBarbasobrancelha = 6
}

/// <summary>
/// Utilitário estático com catálogo de serviços: nome, duração e preço.
/// Usado para:
/// - Gerar menu formatado no bot (ConversationStateManager)
/// - Validar duração na AgendaService
/// - Calcular preços em Appointment
/// - Formatação em relatórios/Excel (ExportService)
/// </summary>
public static class ServicoInfo
{
/// <summary>
/// Dicionário readonly com todos os serviços (chave = TipoServico).
/// Dados de negócio - editar aqui para alterar preços/durações.
/// Usado em GetServico(), AgendaService.GetHorariosDisponiveis(), etc.
/// </summary>
public static readonly Dictionary<TipoServico, (string Nome, int DuracaoMinutos, decimal Preco)> Servicos = new()
    {
        [TipoServico.Corte]                  = ("Corte",                      30, 30.00m),
        [TipoServico.Barba]                  = ("Barba",                      20, 30.00m),
        [TipoServico.Sobrancelha]            = ("Sobrancelha",                15, 15.00m),
        [TipoServico.CorteBarba]             = ("Corte + Barba",              50, 60.00m),
        [TipoServico.CorteSobrancelha]       = ("Corte + Sobrancelha",        45, 45.00m),
        [TipoServico.CorteBarbasobrancelha]  = ("Corte + Barba + Sobrancelha",65, 75.00m),
    };

/// <summary>
/// String formatada do MENU DE SERVIÇOS para WhatsApp.
/// Enviado após nome do cliente na conversa (BotState.AwaitingServico).
/// Layout otimizado para mobile/WhatsApp.
/// </summary>
public static string MenuServicos =>
        "✂️ Qual serviço você deseja?\n\n" +
        "1️⃣ - Corte (30min) — R$30\n" +
        "2️⃣ - Barba (20min) — R$30\n" +
        "3️⃣ - Sobrancelha (15min) — R$15\n" +
        "4️⃣ - Corte + Barba (50min) — R$60\n" +
        "5️⃣ - Corte + Sobrancelha (45min) — R$45\n" +
        "6️⃣ - Corte + Barba + Sobrancelha (65min) — R$75\n\n" +
        "Digite o número do serviço:";

/// <summary>
/// Busca info do serviço pelo enum.
/// Fallback para ("Desconhecido", 30, 0) se inválido.
/// Usado em: save Appointment (SchedulerService), formatação mensagens (ConversationStateManager), AgendaService.
/// </summary>
public static (string Nome, int DuracaoMinutos, decimal Preco) GetServico(TipoServico s) => 
        Servicos.TryGetValue(s, out var info) ? info : ("Desconhecido", 30, 0m);
}

