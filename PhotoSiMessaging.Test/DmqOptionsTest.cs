using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoSiMessaging.Test;

[TestClass]
public class DmqOptionsTest
{
    private static MessagingRouteBuilder NewBuilder()
    {
        var app = WebApplication.CreateBuilder().Build();
        return new MessagingRouteBuilder(app.MapGroup(""), "CART_SERVICE");
    }

    // La dichiarazione con DmqOptions produce DUE cose: il flag createDmq nel /_init (contratto
    // sidecar invariato) e il job interno di drain con la cadenza chiesta.
    [TestMethod]
    public void MapPubSub_WithDmq_EmitsCreateDmqAndDrainJob()
    {
        var builder = NewBuilder();

        builder.MapPubSub("CartDirectory", "OrderUpdated", () => "ok",
            dmq: new DmqOptions(MaxRetries: 3, RetryEvery: TimeSpan.FromHours(2)));

        var subscription = builder.Messages.Single(m => m.Name == "OrderUpdated");
        Assert.IsTrue(subscription is { Type: "pubSub", CreateDmq: true });

        // il tick del drain e' una subscription pubSub interna in piu' (senza DMQ: niente ricorsione)
        var tick = builder.Messages.Single(m => m.Name == "OrderUpdatedDmqDrain");
        Assert.IsTrue(tick is { Type: "pubSub", CreateDmq: null });

        var job = builder.Jobs.Single();
        Assert.AreEqual("OrderUpdatedDmqDrain", job.Name);
        Assert.AreEqual("0 */2 * * *", job.Cron);
        Assert.AreEqual(JobConcurrency.Forbid, job.Concurrency); // un drain sovrapposto e' inutile
    }

    // MaxRetries = 0: solo parcheggio — la DMQ esiste, il job no.
    [TestMethod]
    public void MapPubSub_ParkOnly_NoJob()
    {
        var builder = NewBuilder();

        builder.MapPubSub("CartDirectory", "OrderUpdated", () => "ok", dmq: new DmqOptions(MaxRetries: 0));

        Assert.IsTrue(builder.Messages.Single() is { Name: "OrderUpdated", CreateDmq: true });
        Assert.AreEqual(0, builder.Jobs.Count);
    }

    // Senza DmqOptions il /_init resta byte-identico a prima (CreateDmq assente, non false).
    [TestMethod]
    public void MapPubSub_WithoutDmq_NoCreateDmqNoJob()
    {
        var builder = NewBuilder();

        builder.MapPubSub("CartDirectory", "OrderUpdated", () => "ok");

        Assert.IsTrue(builder.Messages.Single() is { CreateDmq: null });
        Assert.AreEqual(0, builder.Jobs.Count);
    }

    // AddValidator si aggancia all'ULTIMA route: il job interno registrato da MapPubSub non deve
    // rubargliela, o il validator finirebbe sul tick del drain invece che sul messaggio di business.
    [TestMethod]
    public void MapPubSub_WithDmq_AddValidatorStillTargetsTheSubscriber()
    {
        var builder = NewBuilder();

        // se _lastRoute puntasse alla route del drain, AddValidator la filtrerebbe: qui basta che
        // la chain non lanci e che il drain resti un job singolo ben formato — il target reale del
        // filtro e' verificato dal comportamento (il tick non ha body da validare e fallirebbe)
        builder.MapPubSub("CartDirectory", "OrderUpdated", (JsonBody body) => "ok",
                dmq: new DmqOptions())
            .AddValidator<JsonBodyValidator>();

        Assert.AreEqual(1, builder.Jobs.Count);
        Assert.AreEqual(2, builder.Messages.Count);
    }

    public record JsonBody(string Value);

    public class JsonBodyValidator : FluentValidation.AbstractValidator<JsonBody>
    {
        public JsonBodyValidator()
        {
            RuleFor(x => x.Value).NotEmpty();
        }
    }

    // --- validazione: una dichiarazione invalida deve uccidere l'avvio, non il broker ---

    [TestMethod]
    public void Validate_Defaults_AreLegal()
    {
        // 3 x 2h = 6h <= 24h
        Assert.AreEqual(TimeSpan.FromHours(2), new DmqOptions().Validate("D", "N"));
    }

    [TestMethod]
    public void Validate_Rejects_NegativeMaxRetries()
    {
        Assert.ThrowsException<ArgumentException>(() => new DmqOptions(MaxRetries: -1).Validate("D", "N"));
    }

    [TestMethod]
    public void Validate_Rejects_RetryEveryWithParkOnly()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(MaxRetries: 0, RetryEvery: TimeSpan.FromHours(1)).Validate("D", "N"));
    }

    [TestMethod]
    public void Validate_Rejects_SubMinuteAndFractionalMinutes()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(RetryEvery: TimeSpan.FromSeconds(45)).Validate("D", "N"));
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(RetryEvery: TimeSpan.FromMinutes(2.5)).Validate("D", "N"));
    }

    // 90 minuti non hanno una cron esatta ("*/90" mente): sopra l'ora solo ore intere.
    [TestMethod]
    public void Validate_Rejects_NinetyMinutes()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(RetryEvery: TimeSpan.FromMinutes(90)).Validate("D", "N"));
    }

    // Solo DIVISORI esatti di ora/giorno: "*/59" scatterebbe a :00 E :59 (doppio drain
    // back-to-back a ogni cambio d'ora) e "0 */23" a 00:00 e 23:00 — la cadenza reale
    // tradirebbe quella nominale su cui ragiona il budget delle 24h.
    [TestMethod]
    public void Validate_Rejects_NonDivisors()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(RetryEvery: TimeSpan.FromMinutes(59)).Validate("D", "N"));
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(RetryEvery: TimeSpan.FromMinutes(45)).Validate("D", "N"));
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(MaxRetries: 1, RetryEvery: TimeSpan.FromHours(23)).Validate("D", "N"));
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(MaxRetries: 2, RetryEvery: TimeSpan.FromHours(5)).Validate("D", "N"));
        // i divisori passano
        Assert.AreEqual(TimeSpan.FromMinutes(30), new DmqOptions(RetryEvery: TimeSpan.FromMinutes(30)).Validate("D", "N"));
        Assert.AreEqual(TimeSpan.FromHours(8), new DmqOptions(MaxRetries: 3, RetryEvery: TimeSpan.FromHours(8)).Validate("D", "N"));
    }

    // Il nome del job finisce nel CronJob k8s ("job-<kebab>", limite 52 char) e la pipeline non
    // valida: il suffisso DmqDrain puo' sfondare il limite con nomi madre leciti — meglio morire
    // all'avvio col nome esatto che bloccare l'intera application in ArgoCD a pipeline verde.
    [TestMethod]
    public void MapPubSub_WithDmq_RejectsCronJobNameOver52Chars()
    {
        var builder = NewBuilder();

        // kebab("ProfessionalPreorderConfigurationUpdatedDmqDrain") + "job-" = 58 char
        var ex = Assert.ThrowsException<ArgumentException>(() =>
            builder.MapPubSub("CartDirectory", "ProfessionalPreorderConfigurationUpdated", () => "ok",
                dmq: new DmqOptions()));
        StringAssert.Contains(ex.Message, "job-professional-preorder-configuration-updated-dmq-drain");
        StringAssert.Contains(ex.Message, "52");
    }

    [TestMethod]
    public void ToKebabCase_MirrorsThePipelineSed()
    {
        Assert.AreEqual("order-updated-dmq-drain", MessagingEndpoints.ToKebabCase("OrderUpdatedDmqDrain"));
        // maiuscole consecutive: la sed della pipeline mette un trattino prima di OGNUNA
        Assert.AreEqual("recalculate-a-b-c", MessagingEndpoints.ToKebabCase("RecalculateABC"));
        Assert.AreEqual("x", MessagingEndpoints.ToKebabCase("X"));
    }

    // Il drain risolve IHttpClientFactory a runtime: senza AddMessagingClient il servizio NON
    // deve partire (prima falliva ogni tick in silenzio, con un 400 a body vuoto).
    [TestMethod]
    public void MapMessaging_WithDmqDrain_FailsFastWithoutMessagingClient()
    {
        var app = WebApplication.CreateBuilder().Build(); // nessun AddMessagingClient/AddHttpClient

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            app.MapMessaging(m => m.MapPubSub("CartDirectory", "OrderUpdated", () => "ok",
                dmq: new DmqOptions())));
        StringAssert.Contains(ex.Message, "AddMessagingClient");
    }

    [TestMethod]
    public void MapMessaging_ParkOnly_DoesNotRequireMessagingClient()
    {
        var app = WebApplication.CreateBuilder().Build();

        // MaxRetries 0: nessun job, nessuna factory richiesta
        app.MapMessaging(m => m.MapPubSub("CartDirectory", "OrderUpdated", () => "ok",
            dmq: new DmqOptions(MaxRetries: 0)));
    }

    [TestMethod]
    public void Validate_Rejects_BudgetBeyondTtl()
    {
        // 13 x 2h = 26h > 24h: il messaggio scadrebbe prima di esaurire i tentativi promessi
        Assert.ThrowsException<ArgumentException>(() =>
            new DmqOptions(MaxRetries: 13, RetryEvery: TimeSpan.FromHours(2)).Validate("D", "N"));
    }

    [TestMethod]
    public void ToCron_MapsExactly()
    {
        Assert.AreEqual("*/30 * * * *", DmqOptions.ToCron(TimeSpan.FromMinutes(30)));
        Assert.AreEqual("0 */2 * * *", DmqOptions.ToCron(TimeSpan.FromHours(2)));
        Assert.AreEqual("0 0 * * *", DmqOptions.ToCron(TimeSpan.FromHours(24)));
        Assert.AreEqual("*/1 * * * *", DmqOptions.ToCron(TimeSpan.FromMinutes(1)));
    }

    // --- il tick del drain: contratto HTTP verso la sidecar ---

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string clientName)
        {
            Assert.AreEqual("PhotoSiMessaging.DmqDrain", clientName); // il client col timeout lungo
            return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8005") };
        }
    }

    // L'handler risolve tutto da HttpContext.RequestServices (mai parameter binding: su un
    // servizio senza la factory, Minimal API la inferirebbe come body parameter).
    private static (Func<HttpContext, Task<IResult>> Handler, HttpContext Context) HandlerWith(HttpMessageHandler stub)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new StubClientFactory(stub))
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var handler = (Func<HttpContext, Task<IResult>>)
            MessagingRouteBuilder.BuildDmqDrainHandler("CartDirectory", "OrderUpdated", 3);
        return (handler, context);
    }

    [TestMethod]
    public async Task DrainHandler_PostsTheSidecarContract()
    {
        var stub = new StubHandler(HttpStatusCode.OK, """{"requeued":2}""");
        var (handler, context) = HandlerWith(stub);

        await handler(context);

        Assert.IsNotNull(stub.LastRequest);
        Assert.AreEqual(HttpMethod.Post, stub.LastRequest.Method);
        Assert.AreEqual("/dmq/drain?directory=CartDirectory&name=OrderUpdated&maxRetries=3",
            stub.LastRequest.RequestUri!.PathAndQuery);
    }

    // Un drain fallito deve LANCIARE: cosi' il tick passa dal giro standard dei fallimenti pubSub
    // (function_failures_total + BadPubSubMessage) ed e' visibile su Datadog.
    [TestMethod]
    public async Task DrainHandler_ThrowsOnSidecarError()
    {
        var (handler, context) = HandlerWith(new StubHandler(HttpStatusCode.ServiceUnavailable, "solace session not up"));

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => handler(context));
        StringAssert.Contains(ex.Message, "503");
        StringAssert.Contains(ex.Message, "solace session not up");
    }

    // Il 404 e' il caso rollout (sidecar vecchia nel manifest): il messaggio deve dirlo.
    [TestMethod]
    public async Task DrainHandler_404_PointsAtTheOldSidecar()
    {
        var (handler, context) = HandlerWith(new StubHandler(HttpStatusCode.NotFound, "Why did you try to navigate..."));

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => handler(context));
        StringAssert.Contains(ex.Message, "sidecarmq >= 0.1.5");
    }

    // Il timeout del client (TaskCanceledException) DEVE diventare un errore normale: e' una
    // OperationCanceledException e scavalcherebbe il filtro dei 500 di MapMessaging -> 500 a
    // body vuoto, indiagnosticabile. L'abort del chiamante invece deve continuare a propagarsi.
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("simulated client timeout");
    }

    [TestMethod]
    public async Task DrainHandler_ClientTimeout_BecomesDiagnosableError()
    {
        var (handler, context) = HandlerWith(new TimeoutHandler());

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => handler(context));
        StringAssert.Contains(ex.Message, "did not complete within the client timeout");
    }
}
