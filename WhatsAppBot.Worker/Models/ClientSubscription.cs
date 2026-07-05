namespace WhatsAppBot.Worker.Models;

public enum SubscriptionStatus
{
    /// <summary>Aguardando confirmação de pagamento.</summary>
    Pending = 0,
    /// <summary>Ativa — cliente pode usar créditos.</summary>
    Active = 1,
    /// <summary>Expirada por data ou créditos esgotados.</summary>
    Expired = 2,
    /// <summary>Cancelada pelo admin ou pelo cliente.</summary>
    Cancelled = 3
}

/// <summary>
/// Assinatura de um cliente a um plano. Cada assinatura tem créditos que são
/// consumidos a cada agendamento confirmado pelo WhatsApp ou dashboard.
/// </summary>
public class ClientSubscription
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    /// <summary>Telefone do assinante (chave de lookup no bot).</summary>
    public string ClientPhone { get; set; } = "";
    public string ClientName { get; set; } = "";
    public int PlanId { get; set; }
    /// <summary>Nome do plano no momento da adesão (snapshot para histórico).</summary>
    public string PlanNome { get; set; } = "";
    public decimal PlanPreco { get; set; }
    /// <summary>Snapshot dos serviços permitidos no momento da adesão (espelha SubscriptionPlan.ServicosPermitidos).</summary>
    public string ServicosPermitidos { get; set; } = "*";
    /// <summary>
    /// Barbeiro ao qual a assinatura está atrelada (snapshot no momento da adesão).
    /// Null / 0 = qualquer barbeiro pode atender — créditos não têm restrição de profissional.
    /// &gt; 0      = somente agendamentos com este barbeiro consomem créditos.
    /// </summary>
    public int? BarbeiroId { get; set; }
    /// <summary>Nome do barbeiro — snapshot para exibição histórica mesmo que o cadastro mude.</summary>
    public string? BarbeiroNome { get; set; }
    public int CreditosTotal { get; set; }
    public int CreditosUsados { get; set; } = 0;
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Pending;
    /// <summary>Preenchida pelo admin ao ativar a assinatura.</summary>
    public DateTime? StartDate { get; set; }
    /// <summary>Data de expiração — calculada em StartDate + plano.DuracaoDias.</summary>
    public DateTime? EndDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary>Anotação interna do admin (ex.: "Pagamento PIX confirmado").</summary>
    public string? Notes { get; set; }

    // ── Helpers (não mapeados em banco) ──────────────────────────────────────
    public int CreditosRestantes => CreditosTotal - CreditosUsados;
    public bool HasCredits => CreditosRestantes > 0;
    public bool IsExpired => EndDate.HasValue && EndDate.Value < DateTime.Now;
    public bool IsEffectivelyActive => Status == SubscriptionStatus.Active && !IsExpired && HasCredits;
}
