using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging.Exceptions;

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
            m.MapRpc("TestDir", "EchoBoom", (EchoRequest _) => { throw new ObjectNotFoundException("echo 42 not found"); });
            m.MapPubSub("TestDir", "NotifyBoom", (EchoRequest _) => { throw new ObjectNotFoundException("notify 42 not found"); });
            m.MapPubSub("TestDir", "NotifyCrash", (EchoRequest _) => { throw new InvalidOperationException("Sequence contains more than one element"); });
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

    // A handler throwing a BaseException must answer the typed 550 fault on BOTH kinds of route —
    // pubSub included, mirroring the FaaS main-runtime (PmsResponse -> 550 whatever the trigger) —
    // never degrade into a generic 500.
    [TestMethod]
    public async Task RpcHandlerBaseException_Returns550TypedFault()
    {
        var port = FreePort();
        var app = await StartAppAsync(port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.PostAsync("/api/rpc/TestDir/EchoBoom", Json("""{"value":5}"""));

            Assert.AreEqual(550, (int)resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "\"ExceptionCode\":\"OBJECT_NOT_FOUND\"");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task PubSubHandlerBaseException_AlsoReturns550TypedFault()
    {
        var port = FreePort();
        var app = await StartAppAsync(port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.PostAsync("/api/pubSub/TestDir/NotifyBoom", Json("""{"value":5}"""));

            Assert.AreEqual(550, (int)resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "\"ExceptionCode\":\"OBJECT_NOT_FOUND\"");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // A NON-PMS unhandled exception answers 500 with "<Type>: <message>" as the body (the FaaS
    // main-runtime contract): in production a bare ASP.NET 500 has an empty body, and the sidecar
    // copies the wire into the bad-message/DMQ envelope's errorDescription.
    [TestMethod]
    public async Task PubSubHandlerUnhandledException_Returns500WithTypeAndMessage()
    {
        var port = FreePort();
        var app = await StartAppAsync(port);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var resp = await client.PostAsync("/api/pubSub/TestDir/NotifyCrash", Json("""{"value":5}"""));

            Assert.AreEqual(500, (int)resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "InvalidOperationException: Sequence contains more than one element");
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
