namespace PhotoSiMessaging;

// Mockable client surface: consumers inject it and swap it in tests without touching HttpClient.
public interface IMessagingClient
{
    const int DefaultRpcTimeoutMs = 10_000;

    // directory and name deduced from the TRequest type (3rd namespace segment / type name)
    Task<TResponse> CallAsync<TRequest, TResponse>(TRequest request, int timeoutMs = DefaultRpcTimeoutMs);

    // explicit directory + name: for cross-directory calls whose target name isn't a valid C# type
    // name (e.g. "CrossSellingPages.List") or whose directory is only known at runtime. request may be null.
    Task<TResponse> CallAsync<TResponse>(string directory, string name, object? request, int timeoutMs = DefaultRpcTimeoutMs);

    Task PublishAsync<TMessage>(TMessage message, bool guaranteed = true);
}
