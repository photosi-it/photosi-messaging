namespace PhotoSiMessaging.Exceptions;

// Guard extensions available to every service via `using PhotoSiMessaging.Exceptions;` (as in the legacy
// sls library). A null/empty source raises OBJECT_NOT_FOUND; a present-when-it-should-not source raises
// OPERATION_NOT_ALLOWED. Both are 550 faults. Optional parameters fold into the message; logLevel overrides.
public static class ObjectExtensions
{
    public static void ShouldNotBeNull<T>(this T source, string paramKey, object paramValue)
        => source.ShouldNotBeNull(new Dictionary<string, object> { { paramKey, paramValue } });

    public static void ShouldNotBeNull<T>(this T source, IDictionary<string, object>? parameters = null, Level? logLevel = null)
    {
        if (source is null)
        {
            throw Fault(new ObjectNotFoundException(Describe(typeof(T), parameters, "not found")), logLevel);
        }
    }

    public static void ShouldNotBeEmpty<T>(this IEnumerable<T> source, string paramKey, object paramValue)
        => source.ShouldNotBeEmpty(new Dictionary<string, object> { { paramKey, paramValue } });

    public static void ShouldNotBeEmpty<T>(this IEnumerable<T>? source, IDictionary<string, object>? parameters = null, Level? logLevel = null)
    {
        if (source is null || !source.Any())
        {
            throw Fault(new ObjectNotFoundException(Describe(typeof(T), parameters, "not found")), logLevel);
        }
    }

    public static void ShouldBeNull<T>(this T source, string paramKey, object paramValue)
        => source.ShouldBeNull(new Dictionary<string, object> { { paramKey, paramValue } });

    public static void ShouldBeNull<T>(this T source, IDictionary<string, object>? parameters = null, Level? logLevel = null)
    {
        if (source is not null)
        {
            throw Fault(new OperationNotAllowedException(Describe(typeof(T), parameters, "already exists")), logLevel);
        }
    }

    private static TException Fault<TException>(TException exception, Level? logLevel) where TException : BaseException
    {
        if (logLevel.HasValue)
        {
            exception.Level = logLevel.Value;
        }

        return exception;
    }

    private static string Describe(Type type, IDictionary<string, object>? parameters, string verb)
        => parameters is { Count: > 0 }
            ? $"{type.Name} {verb} for {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}"
            : $"{type.Name} {verb}";
}
