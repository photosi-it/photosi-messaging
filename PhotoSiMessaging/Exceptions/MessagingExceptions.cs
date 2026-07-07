namespace PhotoSiMessaging.Exceptions;

public sealed class SomethingWentWrongException(string message, Exception? innerException = null)
    : BaseException(message, Level.Error, innerException)
{
    public override string Code => Exceptions.Code.SomethingWentWrong;
}

public sealed class ObjectNotFoundException(string message, Exception? innerException = null)
    : BaseException(message, Level.Warning, innerException)
{
    public override string Code => Exceptions.Code.ObjectNotFound;
}

public sealed class SecurityException(string message, Exception? innerException = null)
    : BaseException(message, Level.Error, innerException)
{
    public override string Code => Exceptions.Code.InvalidAuthorization;
}

public sealed class ValidationException(string message, Exception? innerException = null)
    : BaseException(message, Level.Error, innerException)
{
    public override string Code => Exceptions.Code.InvalidMessage;
}

public sealed class TimeoutException(string message, Exception? innerException = null)
    : BaseException(message, Level.Error, innerException)
{
    public override string Code => Exceptions.Code.Timeout;
}

public sealed class MaxRetriesExceededException(string message, Exception? innerException = null)
    : BaseException(message, Level.Error, innerException)
{
    public override string Code => Exceptions.Code.MaxRetriesExceeded;
}

public sealed class OperationNotAllowedException(string message, Exception? innerException = null)
    : BaseException(message, Level.Error, innerException)
{
    public override string Code => Exceptions.Code.OperationNotAllowed;
}

public sealed class TooManyRequestsException(string message, Exception? innerException = null)
    : BaseException(message, Level.Warning, innerException)
{
    public override string Code => Exceptions.Code.TooManyRequests;
}
