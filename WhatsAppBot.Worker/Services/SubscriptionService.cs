using Microsoft.EntityFrameworkCore;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;

namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Gerencia planos e assinaturas de clientes.
/// Regras de negócio: lookup por telefone, consumo de crédito, ativação manual,
/// validação de serviço permitido pelo plano.
/// </summary>
public class SubscriptionService
{
    private readonly AppDbContext _db;

    public SubscriptionService(AppDbContext db) => _db = db;

    // ── Planos ────────────────────────────────────────────────────────────────

    public List<SubscriptionPlan> GetPlans(bool includeInactive = false)
        => _db.Set<SubscriptionPlan>()
              .Where(p => includeInactive || p.Ativo)
              .AsEnumerable()                  // SQLite não suporta ORDER BY em decimal via EF
              .OrderBy(p => p.Preco)
              .ToList();

    public SubscriptionPlan? GetPlan(int id)
        => _db.Set<SubscriptionPlan>().FirstOrDefault(p => p.Id == id);

    public async Task<SubscriptionPlan> CreatePlanAsync(int storeId, string nome, string descricao,
        decimal preco, int creditos, int duracaoDias, string servicosPermitidos = "*",
        CancellationToken ct = default)
    {
        var plan = new SubscriptionPlan
        {
            StoreId = storeId,
            Nome = nome.Trim(),
            Descricao = descricao.Trim(),
            Preco = preco,
            Creditos = creditos,
            DuracaoDias = duracaoDias,
            ServicosPermitidos = NormalizeServicosPermitidos(servicosPermitidos),
            Ativo = true,
            CreatedAt = DateTime.Now
        };
        _db.Set<SubscriptionPlan>().Add(plan);
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    public async Task<SubscriptionPlan?> UpdatePlanAsync(int id, string? nome, string? descricao,
        decimal? preco, int? creditos, int? duracaoDias, bool? ativo,
        string? servicosPermitidos = null, CancellationToken ct = default)
    {
        var plan = GetPlan(id);
        if (plan == null) return null;
        if (nome != null) plan.Nome = nome.Trim();
        if (descricao != null) plan.Descricao = descricao.Trim();
        if (preco.HasValue) plan.Preco = preco.Value;
        if (creditos.HasValue) plan.Creditos = creditos.Value;
        if (duracaoDias.HasValue) plan.DuracaoDias = duracaoDias.Value;
        if (ativo.HasValue) plan.Ativo = ativo.Value;
        if (servicosPermitidos != null) plan.ServicosPermitidos = NormalizeServicosPermitidos(servicosPermitidos);
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    // ── Validação de serviço ──────────────────────────────────────────────────

    /// <summary>
    /// Verifica se o serviço do agendamento está coberto pelo plano da assinatura.
    /// Usa o snapshot ServicosPermitidos da ClientSubscription para garantir
    /// que a regra em vigor é a do momento da adesão, não a atual do plano.
    /// Aceita int (serviceId) — não depende mais do enum TipoServico.
    /// </summary>
    public static bool IsServicoPermitido(ClientSubscription sub, int serviceId)
        => IsServicoPermitidoByInt(sub.ServicosPermitidos, serviceId);

    /// <summary>Verifica cobertura diretamente por um SubscriptionPlan (uso em exibição ao cliente).</summary>
    public static bool IsServicoPermitido(SubscriptionPlan plan, int serviceId)
        => IsServicoPermitidoByInt(plan.ServicosPermitidos, serviceId);

    /// <summary>Retorna descrição legível dos serviços cobertos (para exibir no bot/dashboard).</summary>
    public static string DescricaoServicosPermitidos(string servicosPermitidos)
    {
        var raw = servicosPermitidos?.Trim() ?? "*";
        if (string.IsNullOrEmpty(raw) || raw == "*")
            return "todos os serviços";

        var labels = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s switch
            {
                "Corte"                  => "Corte",
                "Barba"                  => "Barba",
                "Sobrancelha"            => "Sobrancelha",
                "CorteBarba"             => "Corte + Barba",
                "CorteSobrancelha"       => "Corte + Sobrancelha",
                "CorteBarbasobrancelha"  => "Corte + Barba + Sobrancelha",
                _                        => s
            })
            .ToList();

        return labels.Count == 1 ? labels[0] : string.Join(", ", labels);
    }

    // ── Assinaturas ───────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna a assinatura ativa do telefone para a loja atual (tenant já aplicado no db).
    /// <para>
    /// Quando <paramref name="servico"/> é informado, ignora assinaturas cujo plano não cobre
    /// aquele tipo de serviço — crédito de "Plano Corte" não é descontado em barba.
    /// </para>
    /// <para>
    /// Quando <paramref name="barbeiroId"/> é informado, ignora assinaturas atreladas a outro
    /// barbeiro — crédito do "Plano com Itamar" não é descontado em agendamento com outro profissional.
    /// Assinaturas sem restrição de barbeiro (BarbeiroId null ou 0) são sempre consideradas.
    /// </para>
    /// </summary>
    /// <summary>
    /// Retorna a assinatura ativa do telefone para a loja atual.
    /// <paramref name="serviceId"/> filtra pelo serviço (ID inteiro); null = qualquer serviço.
    /// </summary>
    public ClientSubscription? GetActiveByPhone(string phone, int? serviceId = null, int? barbeiroId = null)
    {
        var query = _db.Set<ClientSubscription>()
            .Where(s => s.ClientPhone == phone
                     && s.Status == SubscriptionStatus.Active
                     && (s.EndDate == null || s.EndDate > DateTime.Now)
                     && s.CreditosUsados < s.CreditosTotal)
            .OrderByDescending(s => s.EndDate)
            .AsEnumerable();

        if (serviceId.HasValue)
            query = query.Where(s => IsServicoPermitido(s, serviceId.Value));

        if (barbeiroId.HasValue && barbeiroId.Value > 0)
            query = query.Where(s => s.BarbeiroId == null || s.BarbeiroId == 0 || s.BarbeiroId == barbeiroId.Value);

        return query.FirstOrDefault();
    }

    public List<ClientSubscription> GetAll(bool onlyActive = false)
        => _db.Set<ClientSubscription>()
              .Where(s => !onlyActive || s.Status == SubscriptionStatus.Active)
              .OrderByDescending(s => s.CreatedAt)
              .ToList();

    public ClientSubscription? GetById(int id)
        => _db.Set<ClientSubscription>().FirstOrDefault(s => s.Id == id);

    public ClientSubscription? GetLatestByPhone(string phone, bool includeCancelled = false)
        => _db.Set<ClientSubscription>()
              .Where(s => s.ClientPhone == phone
                       && (includeCancelled || s.Status != SubscriptionStatus.Cancelled))
              .OrderByDescending(s => s.CreatedAt)
              .FirstOrDefault();

    /// <summary>
    /// Retorna a assinatura Pending mais recente do telefone (se houver).
    /// Usado para evitar que o cliente crie múltiplas adesões pendentes.
    /// </summary>
    public ClientSubscription? GetPendingByPhone(string phone)
        => _db.Set<ClientSubscription>()
              .Where(s => s.ClientPhone == phone && s.Status == SubscriptionStatus.Pending)
              .OrderByDescending(s => s.CreatedAt)
              .FirstOrDefault();

    /// <summary>
    /// Cria assinatura pendente quando cliente solicita pelo WhatsApp.
    /// Admin precisa ativar na dashboard após confirmar o pagamento.
    /// Os snapshots de ServicosPermitidos e BarbeiroId/Nome são copiados do plano/sessão
    /// para preservar as regras vigentes no momento da adesão.
    /// </summary>
    /// <param name="barbeiroId">
    /// 0 ou null = sem restrição de barbeiro.
    /// &gt; 0 = somente agendamentos com este barbeiro consomem créditos.
    /// </param>
    public async Task<ClientSubscription> CreatePendingAsync(int storeId, string phone, string name,
        int planId, int? barbeiroId = null, string? barbeiroNome = null, CancellationToken ct = default)
    {
        var plan = GetPlan(planId);
        if (plan == null) throw new InvalidOperationException($"Plano {planId} não encontrado.");

        // Normaliza: 0 e null são equivalentes (sem restrição)
        var barId  = barbeiroId.HasValue && barbeiroId.Value > 0 ? barbeiroId : null;
        var barNome = barId.HasValue ? barbeiroNome?.Trim() : null;

        var sub = new ClientSubscription
        {
            StoreId = storeId,
            ClientPhone = phone,
            ClientName = name,
            PlanId = planId,
            PlanNome = plan.Nome,
            PlanPreco = plan.Preco,
            ServicosPermitidos = plan.ServicosPermitidos,   // snapshot no momento da adesão
            BarbeiroId = barId,
            BarbeiroNome = barNome,
            CreditosTotal = plan.Creditos,
            CreditosUsados = 0,
            Status = SubscriptionStatus.Pending,
            CreatedAt = DateTime.Now
        };
        _db.Set<ClientSubscription>().Add(sub);
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    /// <summary>
    /// Retorna descricao legivel do vinculo de profissional para exibicao no bot/dashboard.
    /// Ex.: "com Itamar" ou "qualquer profissional".
    /// </summary>
    public static string DescricaoBarbeiro(ClientSubscription sub)
        => sub.BarbeiroId is > 0 && !string.IsNullOrWhiteSpace(sub.BarbeiroNome)
            ? $"com *{sub.BarbeiroNome}*"
            : "qualquer profissional";

    /// <summary>Ativa uma assinatura pendente (admin confirma pagamento).</summary>
    public async Task<ClientSubscription?> ActivateAsync(int id, CancellationToken ct = default)
    {
        var sub = GetById(id);
        if (sub == null) return null;

        var plan = GetPlan(sub.PlanId);
        var dias = plan?.DuracaoDias ?? 30;

        sub.Status = SubscriptionStatus.Active;
        sub.StartDate = DateTime.Now;
        sub.EndDate = DateTime.Now.AddDays(dias);
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    /// <summary>Cancela assinatura (admin ou expiração).</summary>
    public async Task<ClientSubscription?> CancelAsync(int id, string? notes, CancellationToken ct = default)
    {
        var sub = GetById(id);
        if (sub == null) return null;
        sub.Status = SubscriptionStatus.Cancelled;
        if (notes != null) sub.Notes = notes;
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<ClientSubscription?> DeleteCancelledAsync(int id, CancellationToken ct = default)
    {
        var sub = await _db.Set<ClientSubscription>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sub == null) return null;
        if (sub.Status != SubscriptionStatus.Cancelled)
            throw new InvalidOperationException("Somente assinaturas canceladas podem ser apagadas.");

        _db.Set<ClientSubscription>().Remove(sub);
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    public async Task<int> DeleteAllCancelledAsync(CancellationToken ct = default)
    {
        var cancelled = await _db.Set<ClientSubscription>()
            .Where(s => s.Status == SubscriptionStatus.Cancelled)
            .ToListAsync(ct);

        if (cancelled.Count == 0) return 0;

        _db.Set<ClientSubscription>().RemoveRange(cancelled);
        await _db.SaveChangesAsync(ct);
        return cancelled.Count;
    }

    /// <summary>
    /// Consome 1 crédito em uma assinatura já carregada (evita re-query e conflito de tracking no EF).
    /// Preferir este overload quando a entidade já estiver disponível no contexto.
    /// </summary>
    public async Task<bool> UseCredentialAsync(ClientSubscription sub, CancellationToken ct = default)
    {
        sub.CreditosUsados++;

        // Expirar automaticamente se créditos esgotados
        if (sub.CreditosUsados >= sub.CreditosTotal)
            sub.Status = SubscriptionStatus.Expired;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Consome 1 crédito buscando a assinatura pelo telefone.
    /// Use somente quando a entidade não está disponível; do contrário use o overload com entidade.
    /// </summary>
    public async Task<bool> UseCredentialAsync(string phone, CancellationToken ct = default)
    {
        var sub = GetActiveByPhone(phone);
        if (sub == null) return false;
        return await UseCredentialAsync(sub, ct);
    }

    // ── Métricas para dashboard ───────────────────────────────────────────────

    public int CountActive() => _db.Set<ClientSubscription>()
        .Count(s => s.Status == SubscriptionStatus.Active
                 && (s.EndDate == null || s.EndDate > DateTime.Now)
                 && s.CreditosUsados < s.CreditosTotal);

    public int CountPending() => _db.Set<ClientSubscription>()
        .Count(s => s.Status == SubscriptionStatus.Pending);

    public decimal RevenueThisMonth()
    {
        var start = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        // SQLite não suporta Sum() em decimal via EF — traz para memória antes
        return _db.Set<ClientSubscription>()
            .Where(s => s.Status != SubscriptionStatus.Pending
                     && s.Status != SubscriptionStatus.Cancelled
                     && s.StartDate >= start)
            .AsEnumerable()
            .Sum(s => s.PlanPreco);
    }

    /// <summary>
    /// Marca como Expired assinaturas Active cuja EndDate já passou.
    /// Chamado ao listar assinaturas no dashboard para manter o Status coerente
    /// com a realidade sem precisar de job background.
    /// </summary>
    public async Task<int> ExpireOverdueAsync(CancellationToken ct = default)
    {
        var overdue = _db.Set<ClientSubscription>()
            .Where(s => s.Status == SubscriptionStatus.Active
                     && s.EndDate != null
                     && s.EndDate < DateTime.Now)
            .ToList();

        if (overdue.Count == 0) return 0;

        foreach (var sub in overdue)
            sub.Status = SubscriptionStatus.Expired;

        await _db.SaveChangesAsync(ct);
        return overdue.Count;
    }

    // ── Helpers privados ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifica cobertura por ID de serviço inteiro.
    /// Para IDs 1–6 (barbearia legado), converte para o nome do enum TipoServico e compara com a lista.
    /// Para IDs > 6 (serviços personalizados de outros tipos de negócio), retorna true (sem restrição) —
    /// o sistema de assinaturas é exclusivo de barbearia onde IDs são 1–6.
    /// </summary>
    private static bool IsServicoPermitidoByInt(string? servicosPermitidos, int serviceId)
    {
        var raw = servicosPermitidos?.Trim() ?? "*";
        if (string.IsNullOrEmpty(raw) || raw == "*") return true;

        // Serviços com ID > 6 não pertencem ao universo de enum de barbearia —
        // o plano de assinatura não tem como restringi-los por nome de enum.
        if (!Enum.IsDefined(typeof(TipoServico), serviceId)) return true;

        var servicoNome = ((TipoServico)serviceId).ToString().ToLowerInvariant();
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => s.ToLowerInvariant() == servicoNome);
    }

    /// <summary>
    /// Normaliza a string de serviços: remove espaços extras, valida tokens contra o enum,
    /// ignora tokens desconhecidos. Retorna "*" se vazio ou se todos forem inválidos.
    /// </summary>
    private static string NormalizeServicosPermitidos(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "*") return "*";

        var validos = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => Enum.TryParse<TipoServico>(s, ignoreCase: true, out _))
            .Select(s =>
            {
                Enum.TryParse<TipoServico>(s, ignoreCase: true, out var parsed);
                return parsed.ToString();  // normaliza casing para o nome canônico do enum
            })
            .Distinct()
            .ToList();

        return validos.Count > 0 ? string.Join(",", validos) : "*";
    }
}
