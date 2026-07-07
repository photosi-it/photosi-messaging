namespace PhotoSiMessaging.Exceptions;

public record ResponseException(string? ExceptionCode, string? ExceptionMessage, string? ExceptionDetail);
