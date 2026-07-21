# PhotoSiMessaging

Libreria ASP.NET (net10.0) per parlare col **sidecarmq** PhotoSi. Due mattoni indipendenti:

- **`MapMessaging`** — superficie in ingresso: dichiari subscriber pub/sub e RPC con una riga
  l'uno; route e contratto `/_init` (letto dal sidecar) derivano dalla stessa chiamata, quindi
  non possono divergere. Serviti su una porta dedicata, esclusi da OpenAPI.
- **`IMessagingClient`** — uscita: pubblichi eventi e fai RPC verso altri servizi passando dal
  bridge HTTP del sidecar (`SIDECAR_URI`, default `http://localhost:8005`).

## Install

```
dotnet add package PhotoSiMessaging
```

## Convenzione dei contratti

Directory e nome del messaggio sono **dedotti dal tipo**, come in sls-messaging: il terzo
segmento del namespace è la directory, il nome del tipo è il nome del messaggio.

```csharp
namespace CartService.Directory.CartServiceDirectory.Request  { public record TestRpc(string? Text); }
namespace CartService.Directory.CartServiceDirectory.Response { public record TestRpc(string Echo, DateTimeOffset ReceivedAt); }
namespace CartService.Directory.CartServiceDirectory.Message  { public record TestPubSub(string? Text); }
//                              ^^^^^^^^^^^^^^^^^^^^ directory        ^^^^^^^ name
```

## Client (uscita)

```csharp
builder.Services.AddMessagingClient(); // legge SIDECAR_URI (default http://localhost:8005)
```

```csharp
public class MyService(IMessagingClient messaging)
{
    public async Task DoWork()
    {
        // RPC: PhotosiMessage.CartServiceDirectory:Request.TestRpc
        var reply = await messaging.CallAsync<Request.TestRpc, Response.TestRpc>(new Request.TestRpc("hi"));

        // pub/sub: PhotosiMessage.CartServiceDirectory:Message.TestPubSub
        await messaging.PublishAsync(new Message.TestPubSub("hi"));           // guaranteed
        await messaging.PublishAsync(new Message.TestPubSub("hi"), guaranteed: false); // best effort
    }
}
```

`IMessagingClient` è un'interfaccia: nei test dei tuoi handler la mocki senza toccare HttpClient.

### Errori tipizzati

Un servizio remoto che fallisce risponde `550 {ExceptionCode, ExceptionMessage, ExceptionDetail}`:
`CallAsync`/`PublishAsync` lo ricostruiscono nella `BaseException` corrispondente
(`ObjectNotFoundException`, `ValidationException`, `TimeoutException`, ... in
`PhotoSiMessaging.Exceptions`). Un 429 del broker diventa `TooManyRequestsException`; ogni altro
status `SomethingWentWrongException`. Su un errore RPC l'eccezione porta in `Data`:
`Request Type` (`directory:name`), `Request Message` (il body inviato, camelCase come sul filo)
e, per errori non-550, `Response Body`.

### Retry

Solo per le RPC e solo su fallimenti di **trasporto** (`HttpRequestException`): 3 tentativi
esponenziali (0/200/400 ms). Un 550 o 429 non viene mai ritentato; il pub/sub non viene mai
ritentato (ritentare = rischiare un duplicato).

## Subscriber (ingresso)

```csharp
app.MapMessaging(messaging =>
{
    messaging.MapPubSub("CartServiceDirectory", "TestPubSub", TestPubSubSubscriber.Handle);
    messaging.MapRpc("CartServiceDirectory", "TestRpc", TestRpcSubscriber.Handle);
});
```

Ogni handler è un normale delegate minimal-API. Per l'RPC **il body della risposta HTTP è la
reply** (serializzato camelCase, il default ASP.NET). Un handler — rpc **o pub/sub** — che
lancia una `BaseException` risponde automaticamente `550 + {ExceptionCode, ...}` (PascalCase,
il casing che tutti i client dell'ecosistema decodificano) e viene loggato secondo il suo
`Level`, come faceva la runtime FaaS. Sull'RPC il chiamante riceve l'eccezione **tipizzata**,
non un generico `SOMETHING_WENT_WRONG`; sul pub/sub il 550 dice al sidecar che è un esito di
business: non conteggiato tra le function failure e mai parcheggiato nella DMQ (`createDmq`).

`consumerTag` e `port` hanno default sensati (`SERVICE_NAME` in CONSTANT_CASE, 8081) ma sono
sovrascrivibili. Occhio: il consumerTag finisce nei nomi delle code Solace — cambiarlo significa
code nuove.

`MapPubSub` accetta anche `createDmq: true` (solo pub/sub): il sidecar provisiona una coda
`<nomeCoda>.DMQ` — 50 MB di spool, TTL messaggi 24h — e ci parcheggia una copia persistente
di ogni messaggio fallito definitivamente (dopo i retry), pubblicandola sul topic
`DMQ/<nomeCoda>`. Un consumer centrale (es. un logger verso Datadog) può sottoscrivere
`DMQ/>` per osservare i fallimenti di tutta la flotta; il flusso globale `BadPubSubMessage`
resta invariato.

## Deployment

Richiede il container `sidecarmq` nello stesso pod. Esponi **solo la porta pubblica** sul
Service Kubernetes: gli endpoint di `MapMessaging` rispondono sulla porta di messaggistica
(default 8081), che il sidecar chiama via `localhost` — restano irraggiungibili dall'esterno.

Env richieste: `SIDECAR_URI` (bridge del sidecar, per il client) e `SERVICE_NAME` (consumerTag
di default, per i subscriber).
