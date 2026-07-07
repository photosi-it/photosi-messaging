namespace PhotoSiMessaging.Exceptions;

// Shape del body del fault 550 emesso dal sidecar (http-proxy.js sendHttpException):
// { "ExceptionCode": ..., "ExceptionMessage": ..., "ExceptionDetail": ... }.
public record ResponseException(string? ExceptionCode, string? ExceptionMessage, string? ExceptionDetail);
