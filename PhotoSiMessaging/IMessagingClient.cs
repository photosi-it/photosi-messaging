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

    // suffix: publishes on "PhotosiMessage.{Directory}:Message.{Name}.{suffix}" — the sls fan-out convention
    // (e.g. ObsoleteUserConfiguration.Card), read by subscribers mapped on name "{Name}.{suffix}".
    // Default body so hand-written IMessagingClient fakes keep compiling: it forwards to the explicit overload.
    Task PublishAsync<TMessage>(TMessage message, string suffix, bool guaranteed = true)
        => PublishAsync(MessagingClient.GetDirectory(typeof(TMessage)), typeof(TMessage).Name, message, suffix, guaranteed);

    // explicit directory + name (+ optional suffix): for publishes whose message has no local type in that
    // directory, mirroring CallAsync(directory, name, ...). message may be null.
    // Default body only for source compatibility of existing fakes; MessagingClient implements it.
    Task PublishAsync(string directory, string name, object? message, string? suffix = null, bool guaranteed = true)
        => throw new NotSupportedException($"{GetType().Name} does not implement PublishAsync(directory, name, ...)");
}
