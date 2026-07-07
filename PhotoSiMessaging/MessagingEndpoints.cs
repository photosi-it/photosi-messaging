using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PhotoSiMessaging.Exceptions;

namespace PhotoSiMessaging;

// Superficie broker consumata dal sidecarmq (photosi-it/sidecarmq): il sidecar interroga
// GET /_init all'avvio, crea code/subscription su Solace da quel contratto e consegna i
// messaggi con una POST agli url degli handler.

// PrefetchCount vale solo per le code pubSub (il sidecar lo ignora per le rpc): null = omesso dal JSON
public record InitMessage(string ConsumerIdentifier, string Directory, string Name, int? PrefetchCount, string Type, string Url);

public record InitResponse(List<InitMessage> Messages);

public class MessagingRouteBuilder
{
    private readonly RouteGroupBuilder _group;
    private readonly string _consumerTag;
    private readonly List<InitMessage> _messages = [];

    internal MessagingRouteBuilder(RouteGroupBuilder group, string consumerTag)
    {
        _group = group;
        _consumerTag = consumerTag;
    }

    internal IReadOnlyList<InitMessage> Messages => _messages;

    // topic PhotosiMessage.{directory}:Message.{name}
    public MessagingRouteBuilder MapPubSub(string directory, string name, Delegate handler, int prefetchCount = 10)
    {
        return Map("pubSub", directory, name, handler, prefetchCount);
    }

    // topic PhotosiMessage.{directory}:Request.{name}; il body della risposta HTTP è la reply RPC
    public MessagingRouteBuilder MapRpc(string directory, string name, Delegate handler)
    {
        return Map("rpc", directory, name, handler, prefetchCount: null);
    }

    private MessagingRouteBuilder Map(string type, string directory, string name, Delegate handler, int? prefetchCount)
    {
        // route ed entry di /_init derivano dalla stessa chiamata: non possono divergere
        var message = BuildInitMessage(_consumerTag, type, directory, name, prefetchCount);
        _group.MapPost(message.Url, handler);
        _messages.Add(message);
        return this;
    }

    public static InitMessage BuildInitMessage(string consumerTag, string type, string directory, string name, int? prefetchCount)
    {
        return new InitMessage(
            ConsumerIdentifier: $"{consumerTag}/{directory}/{name}",
            Directory: directory,
            Name: name,
            PrefetchCount: prefetchCount,
            Type: type, // stringhe su cui il sidecar fa switch: "pubSub" / "rpc"
            Url: $"/api/{type}/{directory}/{name}"); // convenzione di path stile legacy-flow
    }
}

public static class MessagingEndpoints
{
    private const int DefaultPort = 8081;

    // Mappa i subscriber + GET /_init e registra il listener dedicato.
    // Tutto è escluso da OpenAPI e risponde solo sulla porta di messaggistica: esponi solo
    // la porta pubblica sul Service k8s e queste route restano raggiungibili solo dal
    // sidecar via localhost.
    //
    // consumerTag: default = SERVICE_NAME (env, presente in ogni deployment) in CONSTANT_CASE,
    // es. "cart-service" -> "CART_SERVICE". Finisce nei nomi delle code Solace: rinominarlo
    // significa code nuove, quindi passalo esplicito se SERVICE_NAME può cambiare.
    public static void MapMessaging(this WebApplication app, Action<MessagingRouteBuilder> subscribers, string? consumerTag = null, int port = DefaultPort)
    {
        consumerTag ??= ToConstantCase(Environment.GetEnvironmentVariable("SERVICE_NAME") ?? app.Environment.ApplicationName);

        app.Urls.Add($"http://0.0.0.0:{port}");

        var group = app.MapGroup("").ExcludeFromDescription();

        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Connection.LocalPort != port)
            {
                return Results.NotFound();
            }

            return await next(context);
        });

        // Complemento server del giro tipizzato: un handler RPC che lancia una BaseException
        // deve rispondere 550 + {ExceptionCode,...} (contratto letto da MessagingClient.FromFault
        // e da sls-messaging), non lasciar degenerare in 500 -> SOMETHING_WENT_WRONG.
        // Solo per le RPC: nel pub/sub non c'è un chiamante in attesa, quindi lasciamo risalire
        // (500 -> il sidecar non ackerà -> redelivery).
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (BaseException ex) when (context.HttpContext.Request.Path.StartsWithSegments("/api/rpc"))
            {
                return Results.Text(SerializeFault(ex), "application/json", statusCode: 550);
            }
        });

        var builder = new MessagingRouteBuilder(group, consumerTag);
        subscribers(builder);

        var init = new InitResponse(builder.Messages.ToList());
        group.MapGet("/_init", () => Results.Ok(init));
    }

    // Fault 550 serializzato in PascalCase (ExceptionCode/...) con le opzioni DI DEFAULT di
    // System.Text.Json, NON i Web defaults camelCase di ASP.NET: sls-messaging (C#) e
    // sls-messaging-python deserializzano il fault case-sensitive su PascalCase e altrimenti
    // si rompono (il core Rust del client python fa .unwrap() -> panic). I nostri client
    // (PhotoSiMessaging, sls-messaging-rust) sono case-insensitive, quindi PascalCase va bene a tutti.
    internal static string SerializeFault(BaseException ex) =>
        JsonSerializer.Serialize(new ResponseException(ex.Code, ex.Message, ex.Detail?.ToString()));

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
