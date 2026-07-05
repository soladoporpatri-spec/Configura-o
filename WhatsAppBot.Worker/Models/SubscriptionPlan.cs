namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Plano de assinatura configurável por loja (ex.: Plano Mensal 4 cortes R$100).
/// </summary>
public class SubscriptionPlan
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Nome { get; set; } = "";
    public string Descricao { get; set; } = "";
    /// <summary>Preço do plano em reais.</summary>
    public decimal Preco { get; set; }
    /// <summary>Quantidade de créditos (usos) incluídos no plano.</summary>
    public int Creditos { get; set; } = 4;
    /// <summary>Duração em dias a partir da ativação.</summary>
    public int DuracaoDias { get; set; } = 30;
    /// <summary>
    /// Serviços cobertos pelo plano.
    /// "*" = todos os serviços.
    /// Caso contrário, lista separada por vírgula dos nomes do enum TipoServico
    /// (ex.: "Corte" ou "Corte,Barba,CorteBarba,CorteSobrancelha,CorteBarbasobrancelha").
    /// </summary>
    public string ServicosPermitidos { get; set; } = "*";
    public bool Ativo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
