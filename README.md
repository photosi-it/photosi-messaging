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
business: non conteggiato tra le function failure e mai parcheggiato nella DMQ (`DmqOptions`).
Un'eccezione **non**-PMS risponde invece `500` con `<Tipo>: <messaggio>` nel body (sempre come
la runtime FaaS): il sidecar lo copia nell'`errorDescription` della busta bad-message/DMQ — un
500 ASP.NET nudo avrebbe il body vuoto e i messaggi parcheggiati sarebbero indiagnosticabili.
Sentry riceve comunque l'eccezione completa (loggata a Error).

`consumerTag` e `port` hanno default sensati (`SERVICE_NAME` in CONSTANT_CASE, 8081) ma sono
sovrascrivibili. Occhio: il consumerTag finisce nei nomi delle code Solace — cambiarlo significa
code nuove.

### DMQ e retry automatico (`DmqOptions`)

```csharp
// parcheggio + 3 tentativi ogni 2 ore (i default)
messaging.MapPubSub("CartServiceDirectory", "TestPubSub", TestPubSubSubscriber.Handle,
    dmq: new DmqOptions());

// solo parcheggio, nessun retry automatico
messaging.MapPubSub("CartServiceDirectory", "TestPubSub", TestPubSubSubscriber.Handle,
    dmq: new DmqOptions(MaxRetries: 0));
```

`MapPubSub` accetta `dmq: new DmqOptions(...)` (solo pub/sub; **sostituisce il vecchio bool
`createDmq`** — chi lo usava migra a `dmq: new DmqOptions()`): il sidecar provisiona una coda
`<nomeCoda>.DMQ` — 50 MB di spool, TTL messaggi 24h — e ci parcheggia ogni messaggio fallito
definitivamente (errori non-550), body integro e contesto (`x-original-topic`, `x-retry-count`,
`x-parked-at`, `x-error`) nelle user property.

Con `MaxRetries > 0` la libreria registra anche un job interno `<Nome>DmqDrain`: ogni
`RetryEvery` il CronJob (materializzato dalla pipeline via `--dump-jobs`, come ogni `MapJob`)
chiede al sidecar di rimettere i parcheggiati nella coda madre — move affidabile lato broker:
un crash nel mezzo produce al più un duplicato, mai una perdita. Un messaggio che esaurisce i
tentativi resta parcheggiato fino alla scadenza del TTL, marcato `x-exhausted`: si ripesca a
mano, dopo aver sistemato la causa, con `POST /dmq/drain?directory=<Dir>&name=<Nome>&includeExhausted=1`
sul sidecar del pod, oppure con "Move Messages" dal Manager Solace.

Vincoli, validati all'avvio (una dichiarazione invalida uccide il boot, non il broker):
`RetryEvery` a minuti interi sotto l'ora oppure ore intere — deve tradursi in una cron esatta —
e `MaxRetries × RetryEvery ≤ 24h`, perché oltre la finestra del TTL i retry sarebbero promesse
vuote. Con `MaxRetries: 0`, `RetryEvery` non è ammesso.

Richiede sidecarmq **≥ 0.1.5** per il retry (con una sidecar più vecchia il tick del drain
fallisce e si vede su Datadog, i messaggi restano parcheggiati); il solo parcheggio funziona
da 0.0.87. Un consumer centrale può sottoscrivere `DMQ/>` per osservare i fallimenti di tutta
la flotta; il flusso globale `BadPubSubMessage` resta invariato.

## Deployment

Richiede il container `sidecarmq` nello stesso pod. Esponi **solo la porta pubblica** sul
Service Kubernetes: gli endpoint di `MapMessaging` rispondono sulla porta di messaggistica
(default 8081), che il sidecar chiama via `localhost` — restano irraggiungibili dall'esterno.

Env richieste: `SIDECAR_URI` (bridge del sidecar, per il client) e `SERVICE_NAME` (consumerTag
di default, per i subscriber).
