namespace PhotoSiMessaging;

// interna: i consumer configurano via env SIDECAR_URI, non via codice
internal static class Configuration
{
    public static string SidecarUri => Environment.GetEnvironmentVariable("SIDECAR_URI") ?? "http://localhost:8005";
}
