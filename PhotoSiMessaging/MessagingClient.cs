using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoSiMessaging;

// Pubblicazione/RPC in uscita attraverso il bridge HTTP del sidecarmq (http-proxy.js), NON
// il gateway REST di Solace: POST {MESSAGE_BRIDGE_URL}/publish/rpc|pubsub/?directory=X&name=Y.

public record MessagingError(string? ExceptionCode, string? ExceptionMessage, string? ExceptionDetail);

public class MessagingCallException(MessagingError error)
    : Exception($"{error.ExceptionCode}: {error.ExceptionMessage}")
{
    public MessagingError Error { get; } = error;
}

public class MessagingClient(HttpClient httpClient)
{
    private const int DefaultRpcTimeoutMs = 10_000; // sidecar DEFAULT_RPC_TIMEOUT

    // topic PhotosiMessage.{directory}:Request.{requestName}; lancia MessagingCallException
    // su reply 550 (timeout o eccezione tipizzata dall'handler remoto)
    public async Task<TResponse> CallAsync<TResponse>(string directory, string requestName, object request, int timeoutMs = DefaultRpcTimeoutMs)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"/publish/rpc/?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(requestName)}&timeout={timeoutMs}",
            request);

        if ((int)response.StatusCode == 550)
        {
            var error = await response.Content.ReadFromJsonAsync<MessagingError>();
            throw new MessagingCallException(error ?? new MessagingError("UNKNOWN", "Malformed 550 response from bridge", null));
        }

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>())!;
    }

    // topic PhotosiMessage.{directory}:Message.{messageName}; 204 al successo
    public async Task PublishAsync(string directory, string messageName, object message, bool guaranteed = true)
    {
        var url = $"/publish/pubsub/?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(messageName)}";
        if (!guaranteed)
        {
            url += "&guaranteed=0";
        }

        var response = await httpClient.PostAsJsonAsync(url, message);
        response.EnsureSuccessStatusCode();
    }
}

public static class MessagingClientExtensions
{
    // base URL = env MESSAGE_BRIDGE_URL (default il sidecar in localhost:8005)
    public static IServiceCollection AddMessagingClient(this IServiceCollection services)
    {
        services.AddSingleton(_ => new MessagingClient(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(2) })
            {
                BaseAddress = new Uri(Environment.GetEnvironmentVariable("MESSAGE_BRIDGE_URL") ?? "http://localhost:8005")
            }));

        return services;
    }
}
