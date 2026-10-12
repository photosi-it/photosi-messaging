using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PhotoSiMessaging.Exceptions;
using Polly;

namespace PhotoSiMessaging;

internal sealed class MessagingClient(HttpClient httpClient) : IMessagingClient
{
    internal const string RpcBasePath = "/publish/rpc/";

    // Wire format of RPC/pubSub bodies, both directions: camelCase web defaults + enums as STRINGS — what the
    // sls SlsMessaging client sent and read, and what every FaaS / connexion caller expects. On read the
    // converter still accepts integer enums, so it is strictly more tolerant than the plain web defaults.
    internal static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private const string PubSubBasePath = "/publish/pubsub/";

    // The sidecar answers 550 TIMEOUT by itself once the RPC timeout elapses; this margin only bounds a hung or
    // saturated sidecar (HttpClient's 100 s default applied whatever timeoutMs said).
    internal const int DeadlineMarginMs = 2_000;
    internal const int PublishDeadlineMs = 10_000;

    public Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = IMessagingClient.DefaultRpcTimeoutMs)
        => CallAsync<TRequest, TResponse>(request, CancellationToken.None, timeoutMs);

    public Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken, int timeoutMs = IMessagingClient.DefaultRpcTimeoutMs)
    {
        var requestType = typeof(TRequest);
        return SendRpcAsync<TResponse>(GetDirectory(requestType), requestType.Name, request, timeoutMs, cancellationToken);
    }

    public Task<TResponse> CallAsync<TResponse>(string directory, string name, object? request, int timeoutMs = IMessagingClient.DefaultRpcTimeoutMs)
        => SendRpcAsync<TResponse>(directory, name, request, timeoutMs, CancellationToken.None);

    public Task<TResponse> CallAsync<TResponse>(string directory, string name, object? request, CancellationToken cancellationToken, int timeoutMs = IMessagingClient.DefaultRpcTimeoutMs)
        => SendRpcAsync<TResponse>(directory, name, request, timeoutMs, cancellationToken);

    private async Task<TResponse> SendRpcAsync<TResponse>(string directory, string name, object? request, int timeoutMs, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs + DeadlineMarginMs);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"{RpcBasePath}?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}&timeout={timeoutMs}",
                request, WireOptions, deadline.Token);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TResponse>(WireOptions, deadline.Token) ?? throw new SomethingWentWrongException($"Empty RPC reply from {directory}:{name}");
            }

            // camelCase, same shape PostAsJsonAsync sent: the body attached to the exception stays replayable
            throw await ToExceptionAsync(response, directory, name, JsonSerializer.Serialize(request, WireOptions), deadline.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // the deadline fired, not the caller: a typed TIMEOUT fault instead of a raw TaskCanceledException
            throw new Exceptions.TimeoutException($"No reply from the sidecar for {directory}:{name} within {timeoutMs + DeadlineMarginMs} ms", ex);
        }
    }

    public Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true)
        => PublishAsync(message, CancellationToken.None, guaranteed);

    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken, bool guaranteed = true)
    {
        var messageType = typeof(TMessage);
        return SendPubSubAsync(GetDirectory(messageType), messageType.Name, suffix: null, message, guaranteed, cancellationToken);
    }

    public Task PublishAsync(string directory, string name, object? message, string? suffix = null, bool guaranteed = true)
        => SendPubSubAsync(directory, name, suffix, message, guaranteed, CancellationToken.None);

    public Task PublishAsync(string directory, string name, object? message, CancellationToken cancellationToken, string? suffix = null, bool guaranteed = true)
        => SendPubSubAsync(directory, name, suffix, message, guaranteed, cancellationToken);

    private async Task SendPubSubAsync<TMessage>(string directory, string name, string? suffix, TMessage message, bool guaranteed, CancellationToken cancellationToken)
    {
        var url = $"{PubSubBasePath}?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}";
        if (!string.IsNullOrEmpty(suffix))
        {
            // the sidecar appends it to the topic: PhotosiMessage.{directory}:Message.{name}.{suffix}
            url += $"&suffix={Uri.EscapeDataString(suffix)}";
        }

        if (!guaranteed)
        {
            url += "&guaranteed=0";
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PublishDeadlineMs);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(url, message, WireOptions, deadline.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw await ToExceptionAsync(response, directory, string.IsNullOrEmpty(suffix) ? name : $"{name}.{suffix}", cancellationToken: deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Exceptions.TimeoutException($"The sidecar did not accept the publish of {directory}:{name} within {PublishDeadlineMs} ms", ex);
        }
    }


    private static async Task<BaseException> ToExceptionAsync(HttpResponseMessage response, string directory, string name, string? requestMessage = null, CancellationToken cancellationToken = default)
    {
        var status = (int)response.StatusCode;
        BaseException exception;

        if (status == 550)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            ResponseException? fault = null;
            try
            {
                fault = JsonSerializer.Deserialize<ResponseException>(body, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                // malformed fault: falls through to SomethingWentWrong via FromFault(null, ...)
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
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
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

    internal static string GetDirectory(Type messageType)
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
        // Retry ONLY on transport failures (HttpRequestException), never on the response:
        // a 550/429 is NOT retried. 3 exponential attempts: 0, 200, 400 ms.
        var rpcRetry = Policy
            .Handle<HttpRequestException>()
            .OrResult<HttpResponseMessage>(_ => false)
            .WaitAndRetryAsync(RpcRetryCount, RpcBackoff);

        // pub/sub is not idempotent: retrying would publish a duplicate -> no-op
        var pubSubNoOp = Policy.NoOpAsync<HttpResponseMessage>();

        // Server side (a service that maps handlers also calls this): make a malformed RPC request body throw a
        // BadHttpRequestException instead of Minimal API's silent 400, so MapMessaging can turn it into a 550
        // ValidationException fault carrying the deserializer's message — the FaaS main-runtime behaviour the
        // connexion callers expect (an opaque 400 tells them nothing about what was wrong).
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        return services
            .AddHttpClient<IMessagingClient, MessagingClient>(c =>
            {
                c.BaseAddress = new Uri(Configuration.SidecarUri);
                c.Timeout = Timeout.InfiniteTimeSpan; // the per-call deadline in MessagingClient is the only timeout
            })
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