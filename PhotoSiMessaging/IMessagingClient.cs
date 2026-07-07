namespace PhotoSiMessaging;

// Superficie mockabile del client: i consumer la iniettano e nei test la sostituiscono senza HttpClient.
public interface IMessagingClient
{
    const int DefaultRpcTimeoutMs = 10_000;

    // directory e name dedotti dal tipo di TRequest (3° segmento del namespace / nome del tipo)
    Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = DefaultRpcTimeoutMs);

    Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true);
}
