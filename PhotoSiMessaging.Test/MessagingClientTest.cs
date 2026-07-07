using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging.Exceptions;
using TimeoutException = PhotoSiMessaging.Exceptions.TimeoutException;

// fake contracts that follow the X.Y.{Directory}.Request/Response/Message convention
namespace PhotoSiMessaging.Test.CartDirectory.Request
{
    public record Echo(string? Text);
}

namespace PhotoSiMessaging.Test.CartDirectory.Response
{
    public record Echo(string Reply);
}

namespace PhotoSiMessaging.Test.CartDirectory.Message
{
    public record Ping(string? Text);
}

namespace ShallowNs
{
    public record Lonely;
}

namespace PhotoSiMessaging.Test
{
    [TestClass]
    public class MessagingClientTest
    {
        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastBody { get; private set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return respond(request);
            }
        }

        private static (MessagingClient Client, StubHandler Stub) NewClient(HttpStatusCode status, string? body = null)
        {
            var stub = new StubHandler(_ => new HttpResponseMessage(status)
            {
                Content = body is null ? null : new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
            return (new MessagingClient(new HttpClient(stub) { BaseAddress = new Uri("http://sidecar:8005") }), stub);
        }

        [TestMethod]
        public async Task CallAsync_Success_BuildsRpcUrlAndSendsCamelCase()
        {
            var (client, stub) = NewClient(HttpStatusCode.OK, """{"reply":"HI"}""");

            var response = await client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi"));

            Assert.AreEqual("HI", response.Reply);
            Assert.AreEqual("/publish/rpc/?directory=CartDirectory&name=Echo&timeout=10000", stub.LastRequest!.RequestUri!.PathAndQuery);
            Assert.AreEqual("""{"text":"hi"}""", stub.LastBody); // camelCase on the wire, like sls
        }

        [TestMethod]
        public async Task CallAsyncExplicit_DottedName_BuildsUrlAndSendsBody()
        {
            var (client, stub) = NewClient(HttpStatusCode.OK, """{"reply":"HI"}""");

            var response = await client.CallAsync<CartDirectory.Response.Echo>(
                "AppCmsDirectory", "CrossSellingPages.List", new CartDirectory.Request.Echo("hi"));

            Assert.AreEqual("HI", response.Reply);
            Assert.AreEqual("/publish/rpc/?directory=AppCmsDirectory&name=CrossSellingPages.List&timeout=10000", stub.LastRequest!.RequestUri!.PathAndQuery);
            Assert.AreEqual("""{"text":"hi"}""", stub.LastBody);
        }

        [TestMethod]
        public async Task CallAsyncExplicit_NullRequest_SendsNullBody()
        {
            var (client, stub) = NewClient(HttpStatusCode.OK, """{"reply":"HI"}""");

            var response = await client.CallAsync<CartDirectory.Response.Echo>(
                "AppCmsDirectory", "CrossSellingPages.List", null);

            Assert.AreEqual("HI", response.Reply);
            Assert.AreEqual("null", stub.LastBody);
        }

        [TestMethod]
        public async Task CallAsync_NullReply_ThrowsSomethingWentWrong()
        {
            var (client, _) = NewClient(HttpStatusCode.OK, "null");

            var ex = await Assert.ThrowsExceptionAsync<SomethingWentWrongException>(() =>
                client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi")));

            StringAssert.Contains(ex.Message, "CartDirectory:Echo");
        }

        [TestMethod]
        public async Task CallAsync_550PascalCase_ThrowsTypedWithRequestData()
        {
            var (client, _) = NewClient((HttpStatusCode)550,
                """{"ExceptionCode":"OBJECT_NOT_FOUND","ExceptionMessage":"cart 42 not found","ExceptionDetail":"d"}""");

            var ex = await Assert.ThrowsExceptionAsync<ObjectNotFoundException>(() =>
                client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi")));

            Assert.AreEqual("cart 42 not found", ex.Message);
            Assert.AreEqual("d", ex.Detail);
            Assert.AreEqual("CartDirectory:Echo", ex.Data["Request Type"]);
            Assert.AreEqual("""{"text":"hi"}""", ex.Data["Request Message"]); // the body actually sent
        }

        [TestMethod]
        public async Task CallAsync_550CamelCase_StillTyped()
        {
            var (client, _) = NewClient((HttpStatusCode)550, """{"exceptionCode":"TIMEOUT","exceptionMessage":"slow"}""");

            var ex = await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi")));

            Assert.AreEqual("slow", ex.Message);
        }

        [TestMethod]
        public async Task CallAsync_550Malformed_SomethingWentWrongWithRawBody()
        {
            var (client, _) = NewClient((HttpStatusCode)550, "not-json");

            var ex = await Assert.ThrowsExceptionAsync<SomethingWentWrongException>(() =>
                client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi")));

            StringAssert.Contains(ex.Message, "Malformed");
            Assert.AreEqual("not-json", ex.Data["Response Body"]);
        }

        [TestMethod]
        public async Task CallAsync_429_ThrowsTooManyRequests()
        {
            var (client, _) = NewClient(HttpStatusCode.TooManyRequests);

            await Assert.ThrowsExceptionAsync<TooManyRequestsException>(() =>
                client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi")));
        }

        [TestMethod]
        public async Task CallAsync_500_SomethingWentWrongWithResponseBody()
        {
            var (client, _) = NewClient(HttpStatusCode.InternalServerError, "oops");

            var ex = await Assert.ThrowsExceptionAsync<SomethingWentWrongException>(() =>
                client.CallAsync<CartDirectory.Request.Echo, CartDirectory.Response.Echo>(new CartDirectory.Request.Echo("hi")));

            Assert.AreEqual("oops", ex.Data["Response Body"]);
        }

        [TestMethod]
        public async Task CallAsync_ShallowNamespace_ThrowsArgumentException()
        {
            var (client, _) = NewClient(HttpStatusCode.OK, """{"reply":"HI"}""");

            await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
                client.CallAsync<ShallowNs.Lonely, CartDirectory.Response.Echo>(new ShallowNs.Lonely()));
        }

        [TestMethod]
        public async Task PublishAsync_Guaranteed_BuildsPubSubUrl()
        {
            var (client, stub) = NewClient(HttpStatusCode.NoContent);

            await client.PublishAsync(new CartDirectory.Message.Ping("hi"));

            Assert.AreEqual("/publish/pubsub/?directory=CartDirectory&name=Ping", stub.LastRequest!.RequestUri!.PathAndQuery);
        }

        [TestMethod]
        public async Task PublishAsync_NotGuaranteed_AppendsGuaranteed0()
        {
            var (client, stub) = NewClient(HttpStatusCode.NoContent);

            await client.PublishAsync(new CartDirectory.Message.Ping("hi"), guaranteed: false);

            Assert.AreEqual("/publish/pubsub/?directory=CartDirectory&name=Ping&guaranteed=0", stub.LastRequest!.RequestUri!.PathAndQuery);
        }

        [TestMethod]
        public async Task PublishAsync_Failure_HasRequestTypeButNoRequestMessage()
        {
            var (client, _) = NewClient(HttpStatusCode.InternalServerError, "oops");

            var ex = await Assert.ThrowsExceptionAsync<SomethingWentWrongException>(() =>
                client.PublishAsync(new CartDirectory.Message.Ping("hi")));

            Assert.AreEqual("CartDirectory:Ping", ex.Data["Request Type"]);
            Assert.IsFalse(ex.Data.Contains("Request Message")); // only CallAsync attaches the body
        }

        // MessagingClient is internal + built via ActivatorUtilities: guards that DI can still
        // resolve the typed client through the interface
        [TestMethod]
        public void AddMessagingClient_ResolvesIMessagingClientFromContainer()
        {
            var services = new ServiceCollection();
            services.AddMessagingClient();
            using var provider = services.BuildServiceProvider();

            Assert.IsNotNull(provider.GetRequiredService<IMessagingClient>());
        }
    }
}
