namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Labels de texto especificos por tipo de negocio, usados no bot e na UI.
/// </summary>
public record BusinessLabels(
    string ProfissionalSingular,
    string ProfissionalPlural,
    string ProfissionalEmoji,
    string StepEscolhaProfissional,
    string PerguntaProfissional,
    string SemProfissionaisMsg,
    string ServicoQuestion,
    string AgendarLabel,
    string DetailStepLabel = "Detalhes",
    string DetailPrompt = "",
    string DetailInvalidHint = "Informe os detalhes do agendamento.",
    string DetailNotesPrefix = "Detalhes",
    string DetailLineLabel = "Detalhes",
    string ServiceEmoji = ""
);

public static class BusinessLabelsFactory
{
    public static BusinessLabels For(BusinessType type) => type switch
    {
        BusinessType.CarWash => new BusinessLabels(
            ProfissionalSingular:    "Box",
            ProfissionalPlural:      "Boxes",
            ProfissionalEmoji:       "Auto",
            StepEscolhaProfissional: "Escolha o box",
            PerguntaProfissional:    "Em qual box deseja realizar o servico?",
            SemProfissionaisMsg:     "No momento nao ha boxes disponiveis. Entre em contato com a unidade.",
            ServicoQuestion:         "Qual servico deseja para o seu veiculo?",
            AgendarLabel:            "Agendar lavagem",
            DetailStepLabel:         "Veiculo",
            DetailPrompt:            "Qual o veiculo? Informe modelo e cor.\nEx: Onix prata, Civic preto, Honda CG vermelha.",
            DetailInvalidHint:       "Informe o veiculo com modelo e cor (entre 2 e 60 caracteres). Ex: Onix prata.",
            DetailNotesPrefix:       "Veiculo",
            DetailLineLabel:         "Veiculo",
            ServiceEmoji:            "Auto"
        ),
        BusinessType.Pizzeria => new BusinessLabels(
            ProfissionalSingular:    "Equipe",
            ProfissionalPlural:      "Equipe",
            ProfissionalEmoji:       "Pizza",
            StepEscolhaProfissional: "Detalhes do pedido",
            PerguntaProfissional:    "Informe endereco de entrega, retirada ou mesa.",
            SemProfissionaisMsg:     "No momento nao ha equipe disponivel. Entre em contato com a unidade.",
            ServicoQuestion:         "Qual pizza ou combo deseja pedir?",
            AgendarLabel:            "Fazer pedido",
            DetailStepLabel:         "Entrega/mesa",
            DetailPrompt:            "Informe endereco de entrega, retirada no balcao ou mesa.\nEx: Rua 10, n 25, ap 302; retirada; mesa 4.",
            DetailInvalidHint:       "Informe endereco, retirada ou mesa (entre 2 e 120 caracteres).",
            DetailNotesPrefix:       "Entrega/mesa",
            DetailLineLabel:         "Entrega/mesa",
            ServiceEmoji:            "Pizza"
        ),
        BusinessType.ComputerOptimization => new BusinessLabels(
            ProfissionalSingular:    "Operador",
            ProfissionalPlural:      "Operadores",
            ProfissionalEmoji:       "PC",
            StepEscolhaProfissional: "Triagem do computador",
            PerguntaProfissional:    "Informe o computador e o objetivo da otimizacao.",
            SemProfissionaisMsg:     "No momento nao ha equipe disponivel. Entre em contato com a unidade.",
            ServicoQuestion:         "Qual pacote de otimizacao combina melhor com seu caso?",
            AgendarLabel:            "Iniciar atendimento",
            DetailStepLabel:         "Computador",
            DetailPrompt:            "Me descreva o computador, o problema e a modalidade.\nEx: PC gamer com queda de FPS, remoto; notebook Windows 11 lento, levar ate a loja.",
            DetailInvalidHint:       "Informe computador, problema e modalidade da otimizacao (entre 2 e 120 caracteres).",
            DetailNotesPrefix:       "Computador",
            DetailLineLabel:         "Computador",
            ServiceEmoji:            "PC"
        ),
        _ => new BusinessLabels(
            ProfissionalSingular:    "Profissional",
            ProfissionalPlural:      "Profissionais",
            ProfissionalEmoji:       "Profissional",
            StepEscolhaProfissional: "Escolha o profissional",
            PerguntaProfissional:    "Com qual profissional deseja agendar?",
            SemProfissionaisMsg:     "No momento nao ha profissionais ativos para agendamento. Entre em contato com a unidade.",
            ServicoQuestion:         "Qual servico deseja?",
            AgendarLabel:            "Marcar horario",
            ServiceEmoji:            "Servico"
        ),
    };
}
