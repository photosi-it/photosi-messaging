using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoSiMessaging.Exceptions;

namespace PhotoSiMessaging;

// Broker surface consumed by sidecarmq (photosi-it/sidecarmq): at startup the sidecar calls
// GET /_init, provisions the Solace queues/subscriptions from that contract, and delivers
// messages by POSTing to the handler urls.

// PrefetchCount applies only to pubSub queues (the sidecar ignores it for rpc): null = omitted from the JSON.
// CreateDmq (pubSub only): true = the sidecar provisions a "<queueName>.DMQ" queue and points the
// subscription queue's deadMsgQueue at it; null (rpc, or pubSub without the flag) so existing /_init
// payloads stay byte-identical.
internal record InitMessage(string ConsumerIdentifier, string Directory, string Name, int? PrefetchCount, string Type, string Url, bool? CreateDmq = null);

internal record InitResponse(List<InitMessage> Messages);

// Dead-lettering + retry automatico per una subscription pubSub (sidecarmq >= 0.1.5).
// La sidecar crea "<coda>.DMQ" e ci parcheggia i fallimenti definitivi non-550 (TTL 24h); con
// MaxRetries > 0 la libreria registra anche un job interno che ogni RetryEvery rimette i
// parcheggiati nella coda madre (move affidabile lato sidecar, POST /dmq/drain). Un messaggio
// che esaurisce i tentativi resta parcheggiato fino alla scadenza del TTL, ripescabile a mano
// (drain con includeExhausted=1, o "Move Messages" dal Manager).
//
// MaxRetries = 0: solo parcheggio, nessun job (e RetryEvery non e' ammesso).
// RetryEvery: minuti interi sotto l'ora, oppure ore intere — deve tradursi in una cron esatta.
//   Default: 2 ore.
// Vincolo: MaxRetries * RetryEvery <= 24h — oltre la finestra del TTL i retry sarebbero
// promesse vuote (il messaggio scade prima di esaurirli).
public sealed record DmqOptions(int MaxRetries = 3, TimeSpan? RetryEvery = null)
{
    internal static readonly TimeSpan DefaultRetryEvery = TimeSpan.FromHours(2);
    internal static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // fail fast all'avvio, con la subscription nel messaggio: una dichiarazione invalida non deve
    // arrivare viva al broker
    internal TimeSpan Validate(string directory, string name)
    {
        var context = $"DmqOptions on {directory}/{name}";
        if (MaxRetries < 0)
        {
            throw new ArgumentException($"{context}: MaxRetries must be >= 0.");
        }

        if (MaxRetries == 0)
        {
            if (RetryEvery is not null)
            {
                throw new ArgumentException($"{context}: RetryEvery has no effect with MaxRetries = 0 (park-only).");
            }

            return DefaultRetryEvery; // inutilizzato: nessun job viene registrato
        }

        var every = RetryEvery ?? DefaultRetryEvery;
        if (every < TimeSpan.FromMinutes(1) || every.Seconds != 0 || every.Milliseconds != 0 || every.Microseconds != 0)
        {
            throw new ArgumentException($"{context}: RetryEvery must be whole minutes (>= 1 minute).");
        }

        if (every >= TimeSpan.FromHours(1) && (every.Minutes != 0 || every > Ttl))
        {
            throw new ArgumentException($"{context}: RetryEvery of an hour or more must be whole hours, up to 24.");
        }

        if (MaxRetries * every > Ttl)
        {
            throw new ArgumentException($"{context}: MaxRetries * RetryEvery must fit the 24h DMQ TTL ({MaxRetries} * {every} = {MaxRetries * every}).");
        }

        return every;
    }

    // Validate() garantisce che ci si arrivi solo con valori rappresentabili esattamente
    internal static string ToCron(TimeSpan every)
    {
        if (every == Ttl)
        {
            return "0 0 * * *";
        }

        return every >= TimeSpan.FromHours(1)
            ? $"0 */{(int)every.TotalHours} * * *"
            : $"*/{(int)every.TotalMinutes} * * * *";
    }
}

// Maps 1:1 onto the k8s CronJob concurrencyPolicy.
public enum JobConcurrency
{
    Allow,
    Forbid,
    Replace,
}

// A MapJob declaration: exported by --dump-jobs, materialized as a k8s CronJob by the deploy pipeline.
internal sealed record JobDefinition(string Directory, string Name, string Cron, JobConcurrency Concurrency, string? Payload);

public class MessagingRouteBuilder
{
    private readonly RouteGroupBuilder _group;
    private readonly string _consumerTag;
    private readonly List<InitMessage> _messages = [];
    private readonly List<JobDefinition> _jobs = [];
    private RouteHandlerBuilder? _lastRoute;

    internal MessagingRouteBuilder(RouteGroupBuilder group, string consumerTag)
    {
        _group = group;
        _consumerTag = consumerTag;
    }

    internal IReadOnlyList<InitMessage> Messages => _messages;

    internal IReadOnlyList<JobDefinition> Jobs => _jobs;

    // dmq: the sidecar provisions "<queueName>.DMQ" as the subscription queue's deadMsgQueue and,
    // with MaxRetries > 0, an internal job re-queues parked messages on the DmqOptions schedule
    // (pubSub only: rpc is never dead-lettered). Replaces the old bool createDmq: callers migrate
    // to `dmq: new DmqOptions()`.
    public MessagingRouteBuilder MapPubSub(string directory, string name, Delegate handler, int prefetchCount = 10, DmqOptions? dmq = null)
    {
        var retryEvery = dmq?.Validate(directory, name); // fail fast, PRIMA di registrare qualunque route
        Map("pubSub", directory, name, handler, prefetchCount, createDmq: dmq is not null);

        if (dmq is not null && dmq.MaxRetries > 0)
        {
            // Il job di drain e' una subscription interna in piu': il CronJob (materializzato dalla
            // pipeline via --dump-jobs) pubblica il tick, la coda broker lo consegna a UNA replica,
            // e l'handler chiede alla sidecar il move dei parcheggiati. AddValidator si aggancia a
            // _lastRoute: va ripristinato alla route dell'UTENTE, o il validator finirebbe sul tick.
            var subscriberRoute = _lastRoute;
            MapJob(directory, $"{name}DmqDrain", DmqOptions.ToCron(retryEvery!.Value),
                BuildDmqDrainHandler(directory, name, dmq.MaxRetries));
            _lastRoute = subscriberRoute;
        }

        return this;
    }

    // Chiama la sidecar del pod: e' lei che risolve i nomi delle code dal proprio registro e fa il
    // move pop-and-push. Un tick fallito lancia: passa dal giro standard dei fallimenti pubSub
    // (function_failures_total + record su BadPubSubMessage, visibile su Datadog) e non viene
    // ritentato — il prossimo tentativo e' il tick successivo.
    internal static Delegate BuildDmqDrainHandler(string directory, string name, int maxRetries)
    {
        return async (IHttpClientFactory clientFactory, ILoggerFactory loggerFactory) =>
        {
            var client = clientFactory.CreateClient(MessagingEndpoints.DmqDrainClientName);
            using var response = await client.PostAsync(
                $"/dmq/drain?directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}&maxRetries={maxRetries}",
                content: null);
            var summary = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"DMQ drain {directory}/{name} failed: HTTP {(int)response.StatusCode} {summary}");
            }

            loggerFactory.CreateLogger(typeof(MessagingEndpoints).FullName!)
                .LogInformation("DMQ drain {Directory}/{Name}: {Summary}", directory, name, summary);
            return Results.Ok();
        };
    }

    // the HTTP response body is the RPC reply
    public MessagingRouteBuilder MapRpc(string directory, string name, Delegate handler)
    {
        return Map("rpc", directory, name, handler, prefetchCount: null);
    }

    // Scheduled job. The handler is a plain pubSub subscriber — the broker queue guarantees the
    // tick runs on exactly ONE replica. The schedule itself lives in a k8s CronJob that publishes
    // the trigger message: the deploy pipeline materializes/reconciles it from `--dump-jobs`
    // (cron expression, concurrency policy and optional payload sent as the message body).
    public MessagingRouteBuilder MapJob(string directory, string name, string cron, Delegate handler,
        JobConcurrency concurrency = JobConcurrency.Forbid, object? payload = null)
    {
        MapPubSub(directory, name, handler);
        _jobs.Add(new JobDefinition(directory, name, cron, concurrency,
            payload is null ? null : JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)));
        return this;
    }

    // Runs a FluentValidation validator (resolved from DI) on the inbound message BEFORE the handler.
    // Chain it right after MapPubSub/MapRpc/MapJob — it applies to that last-mapped route. On failure it
    // throws ValidationException (INVALID_MESSAGE): the MapMessaging filter turns it into a 550 fault,
    // rpc and pubSub alike. The service registers TValidator.
    public MessagingRouteBuilder AddValidator<TValidator>()
        where TValidator : class, IValidator
    {
        if (_lastRoute is null)
        {
            throw new InvalidOperationException("AddValidator must be chained after MapPubSub/MapRpc/MapJob.");
        }

        _lastRoute.AddEndpointFilter(async (context, next) =>
        {
            var validator = context.HttpContext.RequestServices.GetRequiredService<TValidator>();
            // the message type is inferred from the validator: pick the handler argument it can validate
            var message = context.Arguments.FirstOrDefault(argument => argument is not null && validator.CanValidateInstancesOfType(argument.GetType()));
            if (message is not null)
            {
                await MessagingEndpoints.ValidateOrThrowAsync(validator, message);
            }

            return await next(context);
        });

        return this;
    }

    private MessagingRouteBuilder Map(string type, string directory, string name, Delegate handler, int? prefetchCount, bool createDmq = false)
    {
        // route and /_init entry come from the same call: they cannot diverge
        var message = BuildInitMessage(_consumerTag, type, directory, name, prefetchCount, createDmq);
        _lastRoute = _group.MapPost(message.Url, handler);
        _messages.Add(message);
        return this;
    }

    internal static InitMessage BuildInitMessage(string consumerTag, string type, string directory, string name, int? prefetchCount, bool createDmq = false)
    {
        return new InitMessage(
            ConsumerIdentifier: $"{consumerTag}/{directory}/{name}",
            Directory: directory,
            Name: name,
            PrefetchCount: prefetchCount,
            Type: type, // strings the sidecar switches on: "pubSub" / "rpc"
            Url: $"/api/{type}/{directory}/{name}",
            CreateDmq: createDmq ? true : null);
    }
}

public static class MessagingEndpoints
{
    private const int DefaultPort = 8081;

    // HttpClient dedicato al drain (registrato in AddMessagingClient): il drain di una DMQ piena
    // e' un batch che puo' durare minuti, ben oltre i 100s di default di HttpClient
    internal const string DmqDrainClientName = "PhotoSiMessaging.DmqDrain";

    // Maps the subscribers + GET /_init and registers the dedicated listener.
    // Everything is excluded from OpenAPI and answers only on the messaging port: expose only
    // the public port on the k8s Service and these routes stay reachable solely by the sidecar
    // over localhost.
    //
    // consumerTag: default = SERVICE_NAME (env, present in every deployment) in CONSTANT_CASE,
    // e.g. "cart-service" -> "CART_SERVICE". It ends up in the Solace queue names: renaming it
    // means new queues, so pass it explicitly if SERVICE_NAME can change.
    public static void MapMessaging(this WebApplication app, Action<MessagingRouteBuilder> subscribers, string? consumerTag = null, int port = DefaultPort)
    {
        consumerTag ??= ToConstantCase(Environment.GetEnvironmentVariable("SERVICE_NAME") ?? app.Environment.ApplicationName);

        app.Urls.Add($"http://0.0.0.0:{port}");

        // A malformed body fails [FromBody] binding BEFORE the endpoint filters run, so the BaseException filter
        // below can't catch it. With ThrowOnBadRequest=true (set in AddMessagingClient) it surfaces here as a
        // BadHttpRequestException; turn it into an INVALID_MESSAGE ValidationException carrying the deserializer's
        // message (the FaaS main-runtime behaviour) — logged clearly for EVERY messaging endpoint, rpc and pubSub,
        // instead of Minimal API's opaque 400 that tells the caller nothing.
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            try
            {
                await next(context);
            }
            catch (BadHttpRequestException ex) when (
                context.Request.Path.StartsWithSegments("/api/rpc") ||
                context.Request.Path.StartsWithSegments("/api/pubSub"))
            {
                var fault = new Exceptions.ValidationException(ex.InnerException?.Message ?? ex.Message, ex);
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(MessagingEndpoints).FullName!)
                    .Log(ToLogLevel(fault.Level), fault, "Messaging {Path} rejected: malformed request body", context.Request.Path.Value);

                context.Response.Clear();
                // 550 INVALID_MESSAGE for EVERY messaging endpoint (rpc + pubSub), matching the legacy
                // PhotosiMessageClient middleware (which converted any 400 -> 550 transversally). Safe for pubSub:
                // sidecarmq's DEFAULT_RETRY_STRATEGY redelivers ONLY 502-504, so a 550 is not looped — it goes to
                // the BadPubSubMessage (dead-letter) queue and is acked, like the FaaS bad-message queue.
                context.Response.StatusCode = 550;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(SerializeFault(fault));
            }
        });

        var group = app.MapGroup("").ExcludeFromDescription();

        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Connection.LocalPort != port)
            {
                return Results.NotFound();
            }

            return await next(context);
        });

        // Server half of the typed round-trip: a handler that throws a BaseException answers
        // 550 + {ExceptionCode,...} (the contract read by MessagingClient.FromFault and by
        // sls-messaging), not degrade into a 500 -> SOMETHING_WENT_WRONG. Rpc AND pubSub alike,
        // mirroring the FaaS main-runtime (ServerLifecycle: any exception carrying a PmsResponse
        // -> 550, whatever the trigger). On pubSub the sidecar reads the 550 as a business
        // outcome: bad-messaged + acked, excluded from function_failures_total and never parked
        // in the createDmq DMQ.
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (BaseException ex)
            {
                // catching it here means ASP.NET no longer logs it as unhandled: without this
                // log the fault would reach the caller but the server would lose every trace of it
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(MessagingEndpoints).FullName!)
                    .Log(ToLogLevel(ex.Level), ex, "Messaging handler {Path} faulted with {Code}", context.HttpContext.Request.Path.Value, ex.Code);

                return Results.Text(SerializeFault(ex), "application/json", statusCode: 550);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unhandled (non-PMS) exception: mirror the FaaS main-runtime, which answered
                // 500 + the exception message as the body. In production a bare ASP.NET 500 has an
                // EMPTY body, and the sidecar copies the wire into the bad-message/DMQ envelope's
                // errorDescription — without this, parked messages read "HTTP 500: " and are
                // undiagnosable. Logged at Error, so Sentry still gets the full exception; these
                // endpoints answer only the sidecar on localhost, no detail leaks outside the pod.
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(MessagingEndpoints).FullName!)
                    .LogError(ex, "Messaging handler {Path} crashed with {ExceptionType}", context.HttpContext.Request.Path.Value, ex.GetType().Name);

                return Results.Text($"{ex.GetType().Name}: {ex.Message}", "text/plain", statusCode: 500);
            }
        });

        var builder = new MessagingRouteBuilder(group, consumerTag);
        subscribers(builder);

        // CLI contract for the deploy pipeline: `dotnet <Service>.dll --dump-jobs` prints the
        // declared jobs as JSON and exits, so the pipeline can materialize/reconcile the k8s
        // CronJobs that publish each job's trigger message.
        if (Environment.GetCommandLineArgs().Contains("--dump-jobs"))
        {
            Console.WriteLine(SerializeJobs(builder.Jobs));
            Environment.Exit(0);
        }

        var init = new InitResponse(builder.Messages.ToList());
        group.MapGet("/_init", () => Results.Ok(init));
    }

    // Stable JSON contract consumed by the deploy pipeline (--dump-jobs). topic is the exact
    // Solace destination the CronJob curl publishes to.
    internal static string SerializeJobs(IReadOnlyList<JobDefinition> jobs)
    {
        return JsonSerializer.Serialize(new
        {
            jobs = jobs.Select(job => new
            {
                directory = job.Directory,
                name = job.Name,
                cron = job.Cron,
                concurrency = job.Concurrency.ToString(),
                topic = $"PhotosiMessage.{job.Directory}:Message.{job.Name}",
                payload = job.Payload is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(job.Payload),
            }),
        });
    }

    // Validate a message with its FluentValidation validator; on failure raise the INVALID_MESSAGE fault
    // with each error under Data + a JSON Detail ("{ErrorCode} on {PropertyName}": "{message}"), so the
    // caller receives a structured 550 (the same shape as the sls AbstractValidator extension).
    internal static async Task ValidateOrThrowAsync(IValidator validator, object message)
    {
        var result = await validator.ValidateAsync(new ValidationContext<object>(message));
        if (result.IsValid)
        {
            return;
        }

        // Exceptions.ValidationException (our 550 fault), NOT FluentValidation.ValidationException.
        var exception = new Exceptions.ValidationException($"Invalid {message.GetType().Name}");
        var detail = new JsonObject();
        foreach (var error in result.Errors)
        {
            var key = $"{error.ErrorCode} on {error.PropertyName}";
            exception.Data[key] = error.ErrorMessage;
            detail[key] = error.ErrorMessage; // indexer overwrites, so duplicate (code, property) pairs are safe
        }

        exception.Detail = detail.ToString();
        throw exception;
    }

    // 550 fault serialized in PascalCase (ExceptionCode/...) with System.Text.Json's DEFAULT
    // options, NOT ASP.NET's camelCase web defaults: sls-messaging (C#) and sls-messaging-python
    // deserialize the fault case-sensitively on PascalCase and otherwise break (the python
    // client's Rust core .unwrap()s -> panic). Our clients (PhotoSiMessaging, sls-messaging-rust)
    // are case-insensitive, so PascalCase works for everyone.
    internal static string SerializeFault(BaseException ex)
    {
        // complex Detail -> JSON, not ToString() (which would give only the type name)
        var detail = ex.Detail as string ?? (ex.Detail is null ? null : JsonSerializer.Serialize(ex.Detail));
        return JsonSerializer.Serialize(new ResponseException(ex.Code, ex.Message, detail));
    }

    internal static LogLevel ToLogLevel(Level level) => level switch
    {
        Level.Debug => LogLevel.Debug,
        Level.Info => LogLevel.Information,
        Level.Warning => LogLevel.Warning,
        Level.Fatal => LogLevel.Critical,
        _ => LogLevel.Error,
    };

    // "cart-service" / "CartService" / "cart service" -> "CART_SERVICE"
    internal static string ToConstantCase(string source)
    {
        var result = new System.Text.StringBuilder(source.Length + source.Length / 2);

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];

            if (c is '-' or ' ' or '_')
            {
                if (result.Length > 0 && result[^1] != '_')
                {
                    result.Append('_');
                }

                continue;
            }

            if (char.IsUpper(c) && i > 0 && (char.IsLower(source[i - 1]) || char.IsDigit(source[i - 1])) && result.Length > 0 && result[^1] != '_')
            {
                result.Append('_');
            }

            result.Append(char.ToUpperInvariant(c));
        }

        return result.ToString();
    }
}
