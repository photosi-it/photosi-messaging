using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoSiMessaging;

// Pubblicazione/RPC in uscita attraverso il bridge HTTP del sidecarmq (http-proxy.js), NON
// il gateway REST di Solace: POST {SIDECAR_URI}/publish/rpc|pubsub/?directory=X&name=Y.

public record MessagingError(string? ExceptionCode, string? ExceptionMessage, string? ExceptionDetail);

public class MessagingCallException(MessagingError error)
    : Exception($"{error.ExceptionCode}: {error.ExceptionMessage}")
{
    public MessagingError Error { get; } = error;
}

public class MessagingClient(HttpClient httpClient)
{
    private const int DefaultRpcTimeoutMs = 10_000; // sidecar DEFAULT_RPC_TIMEOUT

    // Directory e name si deducono dal namespace del tipo TRequest, come in SlsMessaging:
    // "CartService.Directory.CartServiceDirectory.Request.TestRpc" -> directory = terzo
    // segmento del namespace ("CartServiceDirectory"), name = nome della classe ("TestRpc").
    // Topic PhotosiMessage.{directory}:Request.{name}; lancia MessagingCallException su
    // reply 550 (timeout o eccezione tipizzata dall'handler remoto).
    public async Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = DefaultRpcTimeoutMs)
    {
        var requestType = typeof(TRequest);
        var directory = GetDirectory(requestType);
        var name = requestType.Name;

        var response = await httpClient.PostAsJsonAsync(
            $"/publish/rpc/?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}&timeout={timeoutMs}",
            request);

        if ((int)response.StatusCode == 550)
        {
            var error = await response.Content.ReadFromJsonAsync<MessagingError>();
            throw new MessagingCallException(error ?? new MessagingError("UNKNOWN", "Malformed 550 response from bridge", null));
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    // topic PhotosiMessage.{directory}:Message.{name}; 204 al successo. Stessa deduzione di CallAsync.
    public async Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true)
    {
        var messageType = typeof(TMessage);
        var directory = GetDirectory(messageType);
        var name = messageType.Name;

        var url = $"/publish/pubsub/?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}";
        if (!guaranteed)
        {
            url += "&guaranteed=0";
        }

        var response = await httpClient.PostAsJsonAsync(url, message);
        response.EnsureSuccessStatusCode();
    }


    private static string GetDirectory(Type messageType)
    {
        var ns = messageType.Namespace
            ?? throw new ArgumentException($"{messageType.Name} must declare a namespace shaped as X.Y.{{Directory}}.Request/Response/Message");
        var segments = ns.Split('.');

        if (segments.Length < 3)
        {
            throw new ArgumentException($"{messageType.Name}'s namespace \"{ns}\" is too shallow: expected X.Y.{{Directory}}.Request/Response/Message");
        }

        return segments[2];
    }
}

public static class MessagingClientExtensions
{
    public static IServiceCollection AddMessagingClient(this IServiceCollection services)
    {
        services.AddSingleton(_ => new MessagingClient(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(2) })
            {
                BaseAddress = new Uri(Configuration.SidecarUri)
            }));

        return services;
    }
}
