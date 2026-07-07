namespace PhotoSiMessaging;

// Superficie mockabile del client: i consumer iniettano questa nei loro handler/servizi
// e nei loro unit test la sostituiscono senza dover simulare HttpClient.
public interface IMessagingClient
{
    const int DefaultRpcTimeoutMs = 10_000;

    // RPC verso PhotosiMessage.{directory}:Request.{name}; directory e name dedotti dal tipo
    // di TRequest (terzo segmento del namespace / nome del tipo). Lancia BaseException tipizzate.
    Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = DefaultRpcTimeoutMs);

    // pub/sub verso PhotosiMessage.{directory}:Message.{name}; guaranteed=false = best effort
    Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true);
}
