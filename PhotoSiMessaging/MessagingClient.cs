using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PhotoSiMessaging.Exceptions;

namespace PhotoSiMessaging;

// Pubblicazione/RPC in uscita attraverso il bridge HTTP del sidecarmq (http-proxy.js), NON
// il gateway REST di Solace: POST {SIDECAR_URI}/publish/rpc|pubsub/?directory=X&name=Y.
//
// Gestione errori (come nel PhotosiMessageClient ufficiale):
//   550 -> eccezione tipizzata dal fault ({ExceptionCode,...}) via BaseException.FromFault
//   429 -> TooManyRequestsException (bridge/broker in throttling)
//   altri non-2xx -> SomethingWentWrongException, con Request Type/Body in Exception.Data
public class MessagingClient(HttpClient httpClient)
{
    private const int DefaultRpcTimeoutMs = 10_000; // sidecar DEFAULT_RPC_TIMEOUT

    // Directory e name si deducono dal namespace del tipo TRequest, come in SlsMessaging:
    // "CartService.Directory.CartServiceDirectory.Request.TestRpc" -> directory = terzo
    // segmento ("CartServiceDirectory"), name = nome classe ("TestRpc").
    // Topic PhotosiMessage.{directory}:Request.{name}.
    public async Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = DefaultRpcTimeoutMs)
    {
        var requestType = typeof(TRequest);
        var directory = GetDirectory(requestType);
        var name = requestType.Name;

        var response = await httpClient.PostAsJsonAsync(
            $"/publish/rpc/?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}&timeout={timeoutMs}",
            request);

        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync<TResponse>())!;
        }

        throw await ToExceptionAsync(response, directory, name);
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

        if (!response.IsSuccessStatusCode)
        {
            throw await ToExceptionAsync(response, directory, name);
        }
    }


    private static async Task<BaseException> ToExceptionAsync(HttpResponseMessage response, string directory, string name)
    {
        var status = (int)response.StatusCode;
        BaseException exception;

        if (status == 550)
        {
            ResponseException? fault = null;
            try
            {
                fault = await response.Content.ReadFromJsonAsync<ResponseException>();
            }
            catch
            {
                // fault malformato: cade nel ramo SomethingWentWrong sotto
            }

            exception = BaseException.FromFault(
                fault?.ExceptionCode,
                fault?.ExceptionMessage ?? "Malformed 550 fault from sidecar",
                fault?.ExceptionDetail);
        }
        else if (status == 429)
        {
            exception = new TooManyRequestsException($"Broker throttled {directory}:{name} (HTTP 429)");
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync();
            exception = new SomethingWentWrongException($"Sidecar returned HTTP {status} for {directory}:{name}");
            if (!string.IsNullOrEmpty(body))
            {
                exception.Data["Response Body"] = body;
            }
        }

        exception.Data["Request Type"] = $"{directory}:{name}";
        return exception;
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
