namespace WhatsAppBot.Worker.Models;

/// <summary>
/// Serviço configurável por tenant. Substitui o enum TipoServico hardcoded.
/// IDs 1-6 são reservados como defaults da barbearia (mapeados do TipoServico enum).
/// </summary>
public class ServicoItem
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Nome { get; set; } = "";
    public int DuracaoMinutos { get; set; } = 30;
    public decimal Preco { get; set; }
    public bool Ativo { get; set; } = true;
    public int Ordem { get; set; } = 0;

    /// <summary>
    /// Se o serviço ocupa uma vaga/horário fixo na agenda. Padrão: true.
    /// Serviços como polimento (longos, "sob avaliação") podem ser false — não consomem
    /// capacidade do horário nem bloqueiam horários futuros (ex.: lava-jato).
    /// </summary>
    public bool OcupaHorario { get; set; } = true;
}
