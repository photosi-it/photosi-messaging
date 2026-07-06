# PhotoSiMessaging

Libreria ASP.NET per parlare col **sidecarmq** PhotoSi. Due mattoni indipendenti:

- **`MapMessaging`** — superficie in ingresso: dichiari subscriber pub/sub e RPC con una riga
  l'uno; route e contratto `/_init` (letto dal sidecar) derivano dalla stessa chiamata, quindi
  non possono divergere. Serviti su una porta dedicata, esclusi da OpenAPI.
- **`MessagingClient`** — uscita: pubblichi eventi e fai RPC verso altri servizi passando dal
  bridge HTTP del sidecar, non dal gateway REST di Solace.

Target: `net8.0` e `net10.0`.

## Install

```
dotnet add package PhotoSiMessaging
```

## Subscriber (ingresso)

```csharp
app.MapMessaging(messaging =>
{
    messaging.MapPubSub("CartServiceDirectory", "CartConfirmed", CartConfirmed.Handle);
    messaging.MapRpc("CartServiceDirectory", "GetCartSummary", GetCartSummary.Handle);
});
```

Ogni handler è un normale delegate minimal-API. Per l'RPC, **il body della risposta HTTP è la
reply** (rispondi con 550 + `{ExceptionCode, ExceptionMessage, ExceptionDetail}` per un errore
tipizzato). `consumerTag` e `port` hanno default sensati (`SERVICE_NAME` in CONSTANT_CASE, 8081)
ma sono sovrascrivibili.

## Client (uscita)

```csharp
builder.Services.AddMessagingClient(); // legge MESSAGE_BRIDGE_URL (default http://localhost:8005)

// in un handler / servizio:
await messaging.PublishAsync("CartServiceDirectory", "CartConfirmed", new { cartId });
var summary = await messaging.CallAsync<CartSummary>("OtherDirectory", "GetSomething", new { id });
```

## Deployment

Richiede il container `sidecarmq` nello stesso pod. Esponi **solo la porta pubblica** sul
Service Kubernetes: gli endpoint di `MapMessaging` rispondono sulla porta di messaggistica
(default 8081), che il sidecar chiama via `localhost` — restano irraggiungibili dall'esterno.
