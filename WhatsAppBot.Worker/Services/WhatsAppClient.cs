/// <summary>
/// CLIENT HTTP: Integração com WhatsApp via Bridge Node.js.
/// Suporte a múltiplas instâncias de Bridge (uma por loja).
/// Polling para mensagens recebidas + envio.
/// Bridge Factory: WhatsAppBridge/bridge-factory.js
/// </summary>
using System.Net.Http.Json;
using Polly;
using Polly.Retry;
using WhatsAppBot.Worker.Data;
using WhatsAppBot.Worker.Models;
using Microsoft.Extensions.DependencyInjection;


namespace WhatsAppBot.Worker.Services;

/// <summary>
/// Cliente HTTP typed para API do WhatsAppBridge.
/// Suporte a múltiplas instâncias via BridgeUrl dinâmica.
/// GET /messages: Lista mensagens pendentes.
/// POST /send: Envia texto para phone.
/// </summary>
public class WhatsAppClient
{
    /// <summary>
    /// HttpClient compartilhado para todas as requisições
    /// </summary>
    private readonly HttpClient _http;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private readonly string _apiKey;

    /// <summary>
    /// Construtor DI: HttpClient + retry policy + API key.
    /// IMPORTANTE: não injeta ITenantService / AppDbContext aqui para evitar resolução de scoped service via root provider.
    /// O tenant e o DbContext serão resolvidos sob demanda dentro dos métodos.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    public WhatsAppClient(HttpClient http, IConfiguration config, IServiceScopeFactory scopeFactory)
    {
        _http = http;
        _apiKey = config["ApiKey"] ?? config["API_KEY"] ?? throw new InvalidOperationException("ApiKey não configurada");
        _scopeFactory = scopeFactory;





        _retryPolicy = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                            || r.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                            || r.StatusCode == System.Net.HttpStatusCode.GatewayTimeout)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
    }

    private async Task<string> GetCurrentBridgeUrlAsync()
    {
        try
        {
            // Resolve tenant e BridgeUrl do banco para falar com a instância correta.
            using var scope = _scopeFactory.CreateScope();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var tenantId = tenantService.GetTenantId();
            if (tenantId == 0)
                return "http://127.0.0.1:3001"; // superadmin: fallback para a primeira bridge

            var store = await db.Stores.FindAsync(tenantId);
            var bridgeUrl = store?.BridgeUrl;
            if (!string.IsNullOrWhiteSpace(bridgeUrl))
                return bridgeUrl;

            // Fallback pela convenção da bridge-factory: porta = 3000 + storeId (Store 2 → 3002).
            // Evita cair em :3000 (inexistente) quando o BridgeUrl não está gravado na loja.
            return $"http://127.0.0.1:{3000 + tenantId}";
        }
        catch
        {
            return "http://127.0.0.1:3001";
        }
    }

    /// <summary>
    /// Executa a requisição com retry criando uma NOVA HttpRequestMessage a cada tentativa.
    /// Um HttpRequestMessage só pode ser enviado uma vez — reusá-lo no retry lança
    /// "The request message was already sent", impedindo o bot de responder.
    /// </summary>
    private Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        return _retryPolicy.ExecuteAsync(() => _http.SendAsync(requestFactory(), cancellationToken));
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, object? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-API-KEY", _apiKey);
        if (jsonBody != null) request.Content = JsonContent.Create(jsonBody);
        return request;
    }





    /// <summary>
    /// Busca mensagens de um Bridge específico
    /// </summary>
    /// <param name="bridgeUrl">URL do Bridge (ex: http://localhost:3001)</param>
    /// <returns>Lista mensagens ou empty se null</returns>
    public async Task<List<IncomingMessage>> GetMessagesAsync(string bridgeUrl, CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, $"{bridgeUrl}/messages"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<IncomingMessage>>(cancellationToken: cancellationToken);
        return result ?? new();
    }

    public async Task<List<IncomingMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        return await GetMessagesAsync(url, cancellationToken);
    }

    public async Task AckMessagesAsync(string bridgeUrl, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (idList.Count == 0) return;

        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Post, $"{bridgeUrl}/messages/ack", new { ids = idList }), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task AckMessagesAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        await AckMessagesAsync(url, ids, cancellationToken);
    }

    /// <summary>
    /// Envia mensagem de texto para phone via Bridge específico.
    /// Called by ConversationStateManager e ReminderService.
    /// POST Json {phone, text}.
    /// </summary>
    public async Task SendAsync(string bridgeUrl, string phone, string text, CancellationToken cancellationToken = default)
    {
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Post, $"{bridgeUrl}/send", new { phone, text }), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendAsync(string phone, string text, CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        await SendAsync(url, phone, text, cancellationToken);
    }

    /// <summary>
    /// Envia enquete para phone via Bridge específico.
    /// Cada opcao tem um label exibido e um value usado internamente pelo bot.
    /// </summary>
    public async Task<PollResponse?> SendPollAsync(string bridgeUrl, string phone, string question, IEnumerable<PollOption> options, CancellationToken cancellationToken = default)
    {
        var optionList = options.ToList();
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Post, $"{bridgeUrl}/send-poll", new
        {
            phone,
            question,
            options = optionList,
            allowMultipleAnswers = false
        }), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var fallback = question + "\n\n" + string.Join("\n", optionList.Select(o => $"{o.Value} - {o.Label}"));
            fallback += "\n\n_Responda digitando o numero da opcao._";
            await SendAsync(bridgeUrl, phone, fallback, cancellationToken);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<PollResponse>(cancellationToken: cancellationToken);
    }

    public async Task<PollResponse?> SendPollAsync(string phone, string question, IEnumerable<PollOption> options, CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        return await SendPollAsync(url, phone, question, options, cancellationToken);
    }

    public async Task<bool> IsBridgeReachableAsync(string bridgeUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{bridgeUrl}/status");
            request.Headers.Add("X-API-KEY", _apiKey);

            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsBridgeReachableAsync(CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        return await IsBridgeReachableAsync(url, cancellationToken);
    }

    /// <summary>
    /// Obtém status detalhado do WhatsApp Bridge específico.
    /// Versão com retry completo — use apenas quando necessário (ex.: diagnóstico, primeiro load).
    /// </summary>
    public async Task<dynamic?> GetStatusAsync(string bridgeUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, $"{bridgeUrl}/status"), cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<dynamic?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        return await GetStatusAsync(url, cancellationToken);
    }

    /// <summary>
    /// Status rápido da bridge — sem retry, timeout fixo de 4 s.
    /// Usado pelo polling periódico da dashboard (a cada ~12 s).
    /// Se a bridge não responder em 4 s, retorna null imediatamente em vez de
    /// bloquear o endpoint até 14 s extras com o backoff exponencial do retry.
    /// </summary>
    public async Task<dynamic?> GetStatusFastAsync(string bridgeUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var request = BuildRequest(HttpMethod.Get, $"{bridgeUrl}/status");
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Obtém QR code do WhatsApp Bridge específico
    /// </summary>
    public async Task<dynamic?> GetQrAsync(string bridgeUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, $"{bridgeUrl}/qr"), cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<dynamic?> GetQrAsync(CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        return await GetQrAsync(url, cancellationToken);
    }

    /// <summary>
    /// Ativa/desativa o bot no Bridge específico
    /// </summary>
    public async Task<dynamic?> ToggleBotAsync(string bridgeUrl, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Post, $"{bridgeUrl}/bot/toggle", new { enabled }), cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<dynamic?> ToggleBotAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var url = await GetCurrentBridgeUrlAsync();
        return await ToggleBotAsync(url, enabled, cancellationToken);
    }
}

/// <summary>
/// Record para mensagem recebida do Bridge.
/// Phone sem formatação (ex: "5511999999999").
/// Text lowercased in HandleAsync.
/// Timestamp unix ms.
/// </summary>
public record IncomingMessage(string Phone, string Text, long Timestamp, string? PollId = null, int? StoreId = null, string? Id = null, string? Source = null);

/// <summary>
/// Representa a resposta da criação de uma enquete no Bridge
/// </summary>
public record PollResponse(string Id);

/// <summary>
/// Opcao de enquete enviada ao Bridge.
/// Label aparece no WhatsApp; Value volta para o fluxo como se o usuario tivesse digitado.
/// </summary>
public record PollOption(string Label, string Value);
