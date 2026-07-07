namespace PhotoSiMessaging.Exceptions;

public enum Level
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Fatal = 4,
}

// Codici sul filo (campo ExceptionCode del fault 550), stessi valori del PhotosiMessageClient
// ufficiale. Rimossi DATABASE_ROW_LOCKED / DATABASE_CONCURRENCY; aggiunto TOO_MANY_REQUESTS.
public static class Code
{
    public const string ObjectNotFound = "OBJECT_NOT_FOUND";
    public const string SomethingWentWrong = "SOMETHING_WENT_WRONG";
    public const string InvalidAuthorization = "INVALID_AUTHORIZATION";
    public const string InvalidMessage = "INVALID_MESSAGE";
    public const string Timeout = "TIMEOUT";
    public const string MaxRetriesExceeded = "MAX_RETRIES_EXCEEDED";
    public const string OperationNotAllowed = "OPERATION_NOT_ALLOWED";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
}

public abstract class BaseException(string message, Level level, Exception? innerException = null)
    : Exception(message, innerException)
{
    public abstract string Code { get; }

    public Level Level { get; set; } = level;

    public object? Detail { get; set; }

    // Ricostruisce l'eccezione tipizzata dal fault 550 del sidecar
    // ({ExceptionCode, ExceptionMessage, ExceptionDetail}). Codice sconosciuto -> SomethingWentWrong.
    internal static BaseException FromFault(string? code, string? exceptionMessage, string? exceptionDetail)
    {
        var message = string.IsNullOrEmpty(exceptionMessage) ? "(no message)" : exceptionMessage;

        BaseException exception = code switch
        {
            Exceptions.Code.ObjectNotFound => new ObjectNotFoundException(message),
            Exceptions.Code.InvalidAuthorization => new SecurityException(message),
            Exceptions.Code.InvalidMessage => new ValidationException(message),
            Exceptions.Code.Timeout => new TimeoutException(message),
            Exceptions.Code.MaxRetriesExceeded => new MaxRetriesExceededException(message),
            Exceptions.Code.OperationNotAllowed => new OperationNotAllowedException(message),
            Exceptions.Code.TooManyRequests => new TooManyRequestsException(message),
            _ => new SomethingWentWrongException(message),
        };

        if (!string.IsNullOrEmpty(exceptionDetail))
        {
            exception.Detail = exceptionDetail;
            exception.Data["Exception Detail"] = exceptionDetail;
        }

        return exception;
    }
}
