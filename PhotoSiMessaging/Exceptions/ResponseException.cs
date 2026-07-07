namespace PhotoSiMessaging.Exceptions;

internal record ResponseException(string? ExceptionCode, string? ExceptionMessage, string? ExceptionDetail);
