using Microsoft.AspNetCore.Builder;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PhotoSiMessaging.Test;

[TestClass]
public class JobsTest
{
    private static MessagingRouteBuilder NewBuilder()
    {
        var app = WebApplication.CreateBuilder().Build();
        return new MessagingRouteBuilder(app.MapGroup(""), "CART_SERVICE");
    }

    // A job is a plain pubSub subscriber (queue -> exactly one replica) plus a recorded definition.
    [TestMethod]
    public void MapJob_RegistersPubSubRouteAndDefinition()
    {
        var builder = NewBuilder();

        builder.MapJob("CartDirectory", "RecalculateTotals", "0 3 * * *", () => "ok");

        var message = builder.Messages.Single();
        Assert.AreEqual("pubSub", message.Type);
        Assert.AreEqual("/api/pubSub/CartDirectory/RecalculateTotals", message.Url);

        var job = builder.Jobs.Single();
        Assert.AreEqual("0 3 * * *", job.Cron);
        Assert.AreEqual(JobConcurrency.Forbid, job.Concurrency); // safe default
        Assert.IsNull(job.Payload);
    }

    // Guards the JSON contract consumed by the deploy pipeline to materialize the CronJobs.
    [TestMethod]
    public void SerializeJobs_EmitsPipelineContract()
    {
        var builder = NewBuilder();
        builder.MapJob("CartDirectory", "RecalculateTotals", "*/15 * * * *", () => "ok",
            JobConcurrency.Replace, payload: new { ReportTypes = new[] { "Confirm" } });

        var json = MessagingEndpoints.SerializeJobs(builder.Jobs);

        StringAssert.Contains(json, "\"directory\":\"CartDirectory\"");
        StringAssert.Contains(json, "\"name\":\"RecalculateTotals\"");
        StringAssert.Contains(json, "\"cron\":\"*/15 * * * *\"");
        StringAssert.Contains(json, "\"concurrency\":\"Replace\"");
        StringAssert.Contains(json, "\"topic\":\"PhotosiMessage.CartDirectory:Message.RecalculateTotals\"");
        StringAssert.Contains(json, "\"reportTypes\":[\"Confirm\"]"); // payload on the wire like every message: camelCase
    }

    [TestMethod]
    public void SerializeJobs_Empty_EmitsEmptyList()
    {
        var json = MessagingEndpoints.SerializeJobs([]);

        Assert.AreEqual("""{"jobs":[]}""", json);
    }

    // Il nome del job finisce nel CronJob k8s ("job-<kebab>", limite 52 char) e la pipeline non
    // valida: meglio morire all'avvio col nome esatto che bloccare l'intera application in ArgoCD
    // a pipeline verde.
    [TestMethod]
    public void MapJob_RejectsCronJobNameOver52Chars()
    {
        var builder = NewBuilder();

        var ex = Assert.ThrowsException<ArgumentException>(() =>
            builder.MapJob("CartDirectory", "RecalculateProfessionalPreorderConfigurationTotals", "0 3 * * *", () => "ok"));
        StringAssert.Contains(ex.Message, "52");
        StringAssert.Contains(ex.Message, "job-recalculate-professional-preorder-configuration-totals");
    }

    [TestMethod]
    public void ToKebabCase_MirrorsThePipelineSed()
    {
        Assert.AreEqual("process-ready-to-pickup-orders", MessagingEndpoints.ToKebabCase("ProcessReadyToPickupOrders"));
        // maiuscole consecutive: la sed della pipeline mette un trattino prima di OGNUNA
        Assert.AreEqual("recalculate-a-b-c", MessagingEndpoints.ToKebabCase("RecalculateABC"));
        Assert.AreEqual("x", MessagingEndpoints.ToKebabCase("X"));
    }
}
