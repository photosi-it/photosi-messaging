namespace PhotoSiMessaging;

// internal: consumers configure it via the SIDECAR_URI env var, not in code
internal static class Configuration
{
    internal static string SidecarUri => Environment.GetEnvironmentVariable("SIDECAR_URI") ?? "http://localhost:8005";
}
