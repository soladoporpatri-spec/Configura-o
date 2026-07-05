namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Dados de contexto para substituição de placeholders em templates de mensagem.
/// Todos os campos são opcionais — valores nulos resultam em string vazia no placeholder.
/// </summary>
public sealed record MessageTemplateContext(
    string? Nome      = null,
    string? Servico   = null,
    string? Profissional = null,
    string? Loja      = null,
    string? Data      = null,
    string? Hora      = null
);

/// <summary>
/// Helper estático para renderizar templates de mensagem com substituição de placeholders.
/// Usado por automações (lembretes, boas-vindas, agradecimento, confirmação) para garantir
/// que TODOS os pontos do sistema apliquem os mesmos placeholders de forma consistente.
///
/// Placeholders suportados:
///   {nome}         → Nome do cliente
///   {servico}      → Nome do serviço
///   {profissional} → Nome do profissional / "nossa equipe"
///   {barbeiro}     → Alias de {profissional} (compatibilidade)
///   {loja}         → Nome da loja
///   {barbearia}    → Alias de {loja} (compatibilidade)
///   {data}         → Data formatada (dd/MM)
///   {hora}         → Hora formatada (HH:mm)
///   {horario}      → Alias de {hora} (compatibilidade)
/// </summary>
public static class MessageTemplateService
{
    /// <summary>
    /// Aplica substituição de placeholders no <paramref name="template"/>.
    /// Placeholders ausentes no contexto resultam em string vazia — nunca NullReferenceException.
    /// </summary>
    public static string Apply(string template, MessageTemplateContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var nome         = ctx.Nome          ?? "";
        var servico      = ctx.Servico       ?? "";
        var profissional = ctx.Profissional  ?? "nossa equipe";
        var loja         = ctx.Loja          ?? "";
        var data         = ctx.Data          ?? "";
        var hora         = ctx.Hora          ?? "";

        return template
            .Replace("{nome}",          nome,         StringComparison.OrdinalIgnoreCase)
            .Replace("{Nome}",          nome)
            .Replace("{servico}",       servico,      StringComparison.OrdinalIgnoreCase)
            .Replace("{Servico}",       servico)
            .Replace("{profissional}",  profissional, StringComparison.OrdinalIgnoreCase)
            .Replace("{Profissional}",  profissional)
            .Replace("{barbeiro}",      profissional, StringComparison.OrdinalIgnoreCase) // alias
            .Replace("{Barbeiro}",      profissional)
            .Replace("{loja}",          loja,         StringComparison.OrdinalIgnoreCase)
            .Replace("{Loja}",          loja)
            .Replace("{barbearia}",     loja,         StringComparison.OrdinalIgnoreCase) // alias
            .Replace("{Barbearia}",     loja)
            .Replace("{data}",          data,         StringComparison.OrdinalIgnoreCase)
            .Replace("{Data}",          data)
            .Replace("{hora}",          hora,         StringComparison.OrdinalIgnoreCase)
            .Replace("{Hora}",          hora)
            .Replace("{horario}",       hora,         StringComparison.OrdinalIgnoreCase) // alias
            .Replace("{Horario}",       hora);
    }

    /// <summary>
    /// Aplica template a partir de um Appointment — atalho para o caso de uso mais comum.
    /// </summary>
    public static string Apply(string template, MessageTemplateContext ctx, string? lojaOverride)
    {
        var ctxComLoja = ctx with { Loja = lojaOverride ?? ctx.Loja };
        return Apply(template, ctxComLoja);
    }
}
