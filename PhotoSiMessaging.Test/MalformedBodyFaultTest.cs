using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoSiMessaging.Test;

// A malformed message body (fails [FromBody] binding) must surface as a 550 INVALID_MESSAGE fault carrying the
// deserializer's message — for EVERY messaging endpoint (rpc + pubSub), like the legacy PhotosiMessageClient —
// not Minimal API's opaque 400. Valid bodies must still work (ThrowOnBadRequest is global). Real Kestrel: the
// group's LocalPort guard only lets the real messaging port through, so an in-memory TestServer wouldn't do.
[TestClass]
public class MalformedBodyFaultTest
{
    public record EchoRequest(ushort Value);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WebApplication> StartAppAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMessagingClient(); // sets ThrowOnBadRequest
        var app = builder.Build();
        app.MapMessaging(m =>
        {
            m.MapRpc("TestDir", "Echo", (EchoRequest r) => new { r.Value });
            m.MapPubSub("TestDir", "Notify", (EchoRequest _) => { });
        }, consumerTag: "TEST", port: port); // app.Urls := [:port] => Kestrel binds only this port
        await app.StartAsync();
        return app;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [TestMethod]
    public async Task MalformedRpcBody_Returns550InvalidMessageFaultWithDeserializerDetail()
    {
        var port = FreePort();
        var app = await StartAppAsync(port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.PostAsync("/api/rpc/TestDir/Echo", Json("""{"value":-1}""")); // -1 can't bind to ushort

            Assert.AreEqual(550, (int)resp.StatusCode);
            var fault = await resp.Content.ReadAsStringAsync();
            StringAssert.Contains(fault, "\"ExceptionCode\":\"INVALID_MESSAGE\"");
            StringAssert.Contains(fault, "could not be converted"); // the JSON deserializer's own message
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MalformedPubSubBody_AlsoReturns550_Transversal()
    {
        var port = FreePort();
        var app = await StartAppAsync(port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.PostAsync("/api/pubSub/TestDir/Notify", Json("""{"value":-1}"""));

            Assert.AreEqual(550, (int)resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "\"ExceptionCode\":\"INVALID_MESSAGE\"");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ValidRpcBody_StillSucceeds()
    {
        var port = FreePort();
        var app = await StartAppAsync(port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.PostAsync("/api/rpc/TestDir/Echo", Json("""{"value":5}"""));

            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "\"value\":5");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
