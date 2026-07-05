using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services.Modules;

public record ModuleDefinition(
    string Key,
    string Name,
    string Description,
    string MinimumPlan,
    bool EnabledByDefault = true,
    IReadOnlySet<BusinessType>? SupportedBusinessTypes = null)
{
    public bool Supports(BusinessType businessType)
        => SupportedBusinessTypes == null || SupportedBusinessTypes.Contains(businessType);
}

public class ModuleCatalog
{
    public IReadOnlyList<ModuleDefinition> Modules { get; } =
    [
        new("agenda", "Agenda", "Agendamentos, cancelamentos e disponibilidade operacional.", "Starter"),
        new("services", "Servicos", "Cadastro de servicos, precos, duracao e regras de horario.", "Starter"),
        new("customers", "Clientes", "Historico basico, contatos e recorrencia dos clientes.", "Starter"),
        new("whatsapp_bot", "Chatbot WhatsApp", "Fluxos de atendimento, QR Code, bridge e respostas automaticas.", "Starter"),
        new("dashboard_analytics", "Dashboard Analytics", "Indicadores principais da loja e acompanhamento diario.", "Starter"),
        new("reports", "Relatorios", "Exportacao Excel e relatorios operacionais.", "Professional"),
        new("automations", "Automacoes", "Mensagens automaticas, lembretes e retencao.", "Professional"),
        new("notifications", "Notificacoes", "Alertas em tempo real para admin e profissionais.", "Professional"),
        new("google_sheets", "Google Sheets", "Sincronizacao com planilhas externas.", "Professional"),
        new("finance", "Financeiro", "Assinatura da loja, recebimentos manuais, PIX e fechamento simples.", "Professional"),
        new("multi_professionals", "Equipe e profissionais", "Gestao de profissionais, horarios, permissoes e agenda por responsavel.", "Professional"),
        new(
            "barbershop_loyalty",
            "Fidelidade para barbearia",
            "Clube, creditos, planos mensais e reativacao de clientes.",
            "Professional",
            SupportedBusinessTypes: new HashSet<BusinessType> { BusinessType.Barbershop }),
        new(
            "carwash_operations",
            "Operacao de lavajato",
            "Veiculos, fila operacional, historico por placa e pacotes de lavagem.",
            "Professional",
            SupportedBusinessTypes: new HashSet<BusinessType> { BusinessType.CarWash }),
        new(
            "computer_optimization",
            "Otimizacao de computadores",
            "Fila, computadores, checklist, orcamento e relatorios para lojas de tecnologia.",
            "Professional",
            SupportedBusinessTypes: new HashSet<BusinessType> { BusinessType.ComputerOptimization }),
        new("crm", "CRM avancado", "Historico, tags, lembretes e relacionamento avancado com clientes.", "Premium", EnabledByDefault: false),
        new("managed_backup", "Backup gerenciado", "Rotina comercial preparada para backup assistido e restauracao.", "Premium", EnabledByDefault: false),
        new("white_label", "White label", "Identidade personalizada, dominio e entrega premium.", "Enterprise", EnabledByDefault: false),
        new("meta_whatsapp_api", "Meta WhatsApp API", "Canal oficial futuro para substituir ou complementar WhatsApp Web.", "Enterprise", EnabledByDefault: false)
    ];

    public bool PlanAllows(string plan, string minimumPlan)
        => PlanCatalog.Allows(plan, minimumPlan);

    public ModuleDefinition? Find(string moduleKey)
        => Modules.FirstOrDefault(m => string.Equals(m.Key, NormalizeKey(moduleKey), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeKey(string moduleKey)
        => moduleKey.Trim().ToLowerInvariant().Replace("-", "_");
}
