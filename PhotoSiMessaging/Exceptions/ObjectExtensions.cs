namespace PhotoSiMessaging.Exceptions;

// Guard extensions available to every service via `using PhotoSiMessaging.Exceptions;` (as in the legacy
// sls library). A null / empty source raises the OBJECT_NOT_FOUND fault (550, Warning-level). Optional
// parameters are folded into the message, and logLevel overrides the default level.
public static class ObjectExtensions
{
    public static void ShouldNotBeNull<T>(this T source, string paramKey, object paramValue)
        => source.ShouldNotBeNull(new Dictionary<string, object> { { paramKey, paramValue } });

    public static void ShouldNotBeNull<T>(this T source, IDictionary<string, object>? parameters = null, Level? logLevel = null)
    {
        if (source is not null)
        {
            return;
        }

        throw NotFound(typeof(T), parameters, logLevel);
    }

    public static void ShouldNotBeEmpty<T>(this IEnumerable<T> source, string paramKey, object paramValue)
        => source.ShouldNotBeEmpty(new Dictionary<string, object> { { paramKey, paramValue } });

    public static void ShouldNotBeEmpty<T>(this IEnumerable<T>? source, IDictionary<string, object>? parameters = null, Level? logLevel = null)
    {
        if (source is not null && source.Any())
        {
            return;
        }

        throw NotFound(typeof(T), parameters, logLevel);
    }

    private static ObjectNotFoundException NotFound(Type type, IDictionary<string, object>? parameters, Level? logLevel)
    {
        var message = parameters is { Count: > 0 }
            ? $"{type.Name} not found for {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}"
            : $"{type.Name} not found";

        var exception = new ObjectNotFoundException(message);
        if (logLevel.HasValue)
        {
            exception.Level = logLevel.Value;
        }

        return exception;
    }
}
