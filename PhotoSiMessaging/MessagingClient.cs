using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PhotoSiMessaging.Exceptions;
using Polly;

namespace PhotoSiMessaging;

public class MessagingClient(HttpClient httpClient) : IMessagingClient
{
    protected internal const string RpcBasePath = "/publish/rpc/";
    private const string PubSubBasePath = "/publish/pubsub/";

    public async Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = IMessagingClient.DefaultRpcTimeoutMs)
    {
        var requestType = typeof(TRequest);
        var directory = GetDirectory(requestType);
        var name = requestType.Name;

        var response = await httpClient.PostAsJsonAsync(
            $"{RpcBasePath}?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}&timeout={timeoutMs}",
            request);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TResponse>()
                ?? throw new SomethingWentWrongException($"Empty RPC reply from {directory}:{name}");
        }

        // camelCase come il body di PostAsJsonAsync: quello allegato all'eccezione resta replayabile
        throw await ToExceptionAsync(response, directory, name, JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
    }

    public async Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true)
    {
        var messageType = typeof(TMessage);
        var directory = GetDirectory(messageType);
        var name = messageType.Name;

        var url = $"{PubSubBasePath}?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}";
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


    private static async Task<BaseException> ToExceptionAsync(HttpResponseMessage response, string directory, string name, string? requestMessage = null)
    {
        var status = (int)response.StatusCode;
        BaseException exception;

        if (status == 550)
        {
            var body = await response.Content.ReadAsStringAsync();
            ResponseException? fault = null;
            try
            {
                fault = JsonSerializer.Deserialize<ResponseException>(body, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                // fault malformato: cade nel ramo SomethingWentWrong via FromFault(null, ...)
            }

            exception = BaseException.FromFault(
                fault?.ExceptionCode,
                fault?.ExceptionMessage ?? "Malformed 550 fault from sidecar",
                fault?.ExceptionDetail);

            if (fault is null && !string.IsNullOrEmpty(body))
            {
                exception.Data["Response Body"] = body;
            }
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
        if (requestMessage is not null)
        {
            exception.Data["Request Message"] = requestMessage;
        }

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
    private const int RpcRetryCount = 3;

    public static IHttpClientBuilder AddMessagingClient(this IServiceCollection services)
    {
        // Retry SOLO sui fallimenti di trasporto (HttpRequestException), mai in base alla
        // risposta: un 550/429 NON viene ritentato. 3 tentativi esponenziali: 0,200,400 ms.
        var rpcRetry = Policy
            .Handle<HttpRequestException>()
            .OrResult<HttpResponseMessage>(_ => false)
            .WaitAndRetryAsync(RpcRetryCount, RpcBackoff);

        // pub/sub non è idempotente: ritentare significherebbe pubblicare un duplicato -> no-op
        var pubSubNoOp = Policy.NoOpAsync<HttpResponseMessage>();

        return services
            .AddHttpClient<IMessagingClient, MessagingClient>(c => c.BaseAddress = new Uri(Configuration.SidecarUri))
            .AddPolicyHandler(request =>
                request.RequestUri?.AbsolutePath.StartsWith(MessagingClient.RpcBasePath) == true
                    ? rpcRetry
                    : pubSubNoOp);
    }

    private static TimeSpan RpcBackoff(int retry) =>
        TimeSpan.FromMilliseconds(retry > 1
            ? 100 * Math.Pow(2, retry - 1)
            : 0);
}