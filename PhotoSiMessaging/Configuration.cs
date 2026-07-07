namespace PhotoSiMessaging;

public static class Configuration
{
    // il sidecar espone il suo bridge HTTP su questo indirizzo (porta 8005 nel container sidecarmq)
    public static string SidecarUri => Environment.GetEnvironmentVariable("SIDECAR_URI") ?? "http://localhost:8005";
}
