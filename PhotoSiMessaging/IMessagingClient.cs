namespace PhotoSiMessaging;

// Mockable client surface: consumers inject it and swap it in tests without touching HttpClient.
public interface IMessagingClient
{
    const int DefaultRpcTimeoutMs = 10_000;

    // directory and name deduced from the TRequest type (3rd namespace segment / type name)
    Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = DefaultRpcTimeoutMs);

    Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true);
}
