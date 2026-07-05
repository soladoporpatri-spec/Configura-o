using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Startup;

public static class DefaultAutomationSeeder
{
    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["Msg_Welcome"] = "Ola, {nome}! Bem-vindo a {loja}.\n\nEscolha uma opcao:\n1 - Agendar horario\n2 - Meus agendamentos\n3 - Cancelar agendamento",
        ["Msg_Confirmation"] = "Deseja confirmar seu agendamento?\n\nData: {data}\nServico: {servico}\nProfissional: {barbeiro}\nValor: {preco}",
        ["Msg_Reminder24h"] = "Ola, {nome}! Passando para lembrar do seu horario amanha as {horario} para {servico}.",
        ["Msg_Reminder1h"] = "Falta 1 hora! {nome}, te esperamos as {horario} para {servico}.",
        ["Msg_Thanks"] = "Obrigado pela preferencia, {nome}! Esperamos que tenha gostado do seu {servico}. Ate a proxima!",
        ["Active_Reminder24h"] = "true",
        ["Active_Reminder1h"] = "true",
        ["Active_Thanks"] = "false",
        ["Active_Retention"] = "true",
        ["Retention_Days"] = "15",
        ["Msg_Retention"] = "Ola, {nome}! Sentimos sua falta em {loja}. Que tal renovar o visual esta semana? Digite *oi* para agendar.",
        // Clube de fidelidade: quando "true", o cliente pode escolher qualquer profissional OU
        // um barbeiro específico ao assinar. Quando "false", a opção "sem preferência" não
        // aparece no bot e o cliente DEVE vincular a assinatura a um barbeiro específico.
        ["Active_SubscriptionAnyBarber"] = "true",
        // Walk-in: quando "true", adiciona a opção "⚡ Quero atendimento agora" no menu
        // do bot (exclusivo para lava-jato e similares). Cria agendamento com DateTime=agora
        // sem necessidade de selecionar data/hora. Desabilitado por padrão — o dono habilita
        // pela dashboard em Automações quando o lava-jato aceitar clientes sem hora marcada.
        ["Active_WalkIn"] = "false"
    };

    /// <summary>
    /// Planos padrão criados automaticamente para qualquer loja (Barbershop) que ainda não possui planos.
    /// A loja pode editar, criar novos ou desativar esses planos pela dashboard.
    /// </summary>
    private static readonly IReadOnlyList<(string Nome, string Descricao, decimal Preco, int Creditos, int DuracaoDias, string ServicosPermitidos)> DefaultPlans =
    [
        (
            "Plano Corte Fidelidade",
            "4 cortes por mês com desconto",
            100.00m,
            4,
            30,
            "Corte"   // cobre apenas corte simples
        ),
        (
            "Plano Corte & Barba Fidelidade",
            "4 usos combinados de corte e barba por mês",
            200.00m,
            4,
            30,
            "Corte,Barba,CorteBarba,CorteSobrancelha,CorteBarbasobrancelha"  // cobre todos que incluem corte ou barba
        ),
    ];

    public static async Task EnsureSeededAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await EnsureSystemConfigsAsync(db, cancellationToken);
        await EnsureDefaultPlansAsync(db, cancellationToken);
    }

    // ── SystemConfigs ─────────────────────────────────────────────────────────

    private static async Task EnsureSystemConfigsAsync(AppDbContext db, CancellationToken ct)
    {
        var existingConfigs = await db.Database
            .SqlQueryRaw<string>("SELECT Key AS Value FROM SystemConfigs")
            .ToListAsync(ct);

        foreach (var item in Defaults)
        {
            if (!existingConfigs.Contains(item.Key))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO SystemConfigs (Key, Value) VALUES ({0}, {1})",
                    item.Key,
                    item.Value);
            }
        }
    }

    // ── Planos padrão ─────────────────────────────────────────────────────────

    /// <summary>
    /// Para cada loja do tipo Barbershop que não possui nenhum plano cadastrado,
    /// cria os dois planos padrão. Idempotente: não duplica se já existir algum plano.
    /// </summary>
    private static async Task EnsureDefaultPlansAsync(AppDbContext db, CancellationToken ct)
    {
        // Ignora filtro de tenant para leitura (TenantId=0 = superadmin)
        db.TenantId = 0;

        var barbershopStoreIds = await db.Stores
            .Where(s => s.BusinessType == BusinessType.Barbershop)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (barbershopStoreIds.Count == 0) return;

        var storesWithPlans = await db.SubscriptionPlans
            .Select(p => p.StoreId)
            .Distinct()
            .ToListAsync(ct);

        var storesSemPlanos = barbershopStoreIds
            .Except(storesWithPlans)
            .ToList();

        foreach (var storeId in storesSemPlanos)
        {
            foreach (var (nome, descricao, preco, creditos, duracao, servicos) in DefaultPlans)
            {
                db.SubscriptionPlans.Add(new SubscriptionPlan
                {
                    StoreId = storeId,
                    Nome = nome,
                    Descricao = descricao,
                    Preco = preco,
                    Creditos = creditos,
                    DuracaoDias = duracao,
                    ServicosPermitidos = servicos,
                    Ativo = true,
                    CreatedAt = DateTime.Now
                });
            }
        }

        if (storesSemPlanos.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
