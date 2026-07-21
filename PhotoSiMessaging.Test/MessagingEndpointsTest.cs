using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging;
using PhotoSiMessaging.Exceptions;

namespace PhotoSiMessaging.Test;

[TestClass]
public class MessagingEndpointsTest
{
    [DataTestMethod]
    [DataRow("cart-service", "CART_SERVICE")]
    [DataRow("CartService", "CART_SERVICE")]
    [DataRow("cart service", "CART_SERVICE")]
    [DataRow("image-manipulator-service", "IMAGE_MANIPULATOR_SERVICE")]
    public void ToConstantCase_ConvertsToConsumerTagFormat(string source, string expected)
    {
        Assert.AreEqual(expected, MessagingEndpoints.ToConstantCase(source));
    }

    // The 550 fault MUST come out in PascalCase: sls-messaging (C#) and sls-messaging-python
    // deserialize case-sensitively on ExceptionCode/ExceptionMessage/ExceptionDetail.
    [TestMethod]
    public void SerializeFault_EmitsPascalCaseFields()
    {
        var body = MessagingEndpoints.SerializeFault(new ObjectNotFoundException("cart 42 not found"));

        StringAssert.Contains(body, "\"ExceptionCode\":\"OBJECT_NOT_FOUND\"");
        StringAssert.Contains(body, "\"ExceptionMessage\":\"cart 42 not found\"");
        Assert.IsFalse(body.Contains("\"exceptionCode\""), "fault serialized camelCase: sls-messaging can't decode it");
    }

    [TestMethod]
    public void SerializeFault_ComplexDetail_SerializedAsJsonNotTypeName()
    {
        var ex = new ValidationException("bad cart") { Detail = new { Id = 42 } };

        var body = MessagingEndpoints.SerializeFault(ex);

        // round-trip: the decoded detail is the object's JSON, not the type name
        var fault = System.Text.Json.JsonSerializer.Deserialize<Exceptions.ResponseException>(body);
        Assert.AreEqual("""{"Id":42}""", fault!.ExceptionDetail);
    }

    // guards the /_init contract consumed by sidecarmq: field values, the url template, and the
    // exact "pubSub"/"rpc" type strings its solace-consumer.js switches on
    [TestMethod]
    public void BuildInitMessage_PubSub_MatchesSidecarContract()
    {
        var m = MessagingRouteBuilder.BuildInitMessage("CART_SERVICE", "pubSub", "CartServiceDirectory", "TestPubSub", 10);

        Assert.AreEqual("/api/pubSub/CartServiceDirectory/TestPubSub", m.Url);
        Assert.AreEqual("CART_SERVICE/CartServiceDirectory/TestPubSub", m.ConsumerIdentifier);
        // CreateDmq null (not false) without the flag: existing /_init payloads must stay byte-identical
        Assert.IsTrue(m is { Type: "pubSub", PrefetchCount: 10, Directory: "CartServiceDirectory", Name: "TestPubSub", CreateDmq: null });
    }

    [TestMethod]
    public void BuildInitMessage_PubSubWithDmq_EmitsCreateDmq()
    {
        var m = MessagingRouteBuilder.BuildInitMessage("CART_SERVICE", "pubSub", "CartServiceDirectory", "TestPubSub", 10, createDmq: true);

        Assert.IsTrue(m is { Type: "pubSub", CreateDmq: true });
    }

    [TestMethod]
    public void BuildInitMessage_Rpc_OmitsPrefetchCount()
    {
        var m = MessagingRouteBuilder.BuildInitMessage("CART_SERVICE", "rpc", "CartServiceDirectory", "TestRpc", null);

        Assert.AreEqual("/api/rpc/CartServiceDirectory/TestRpc", m.Url);
        Assert.IsTrue(m is { Type: "rpc", Directory: "CartServiceDirectory", Name: "TestRpc", PrefetchCount: null, CreateDmq: null });
    }

    [DataTestMethod]
    [DataRow(Level.Debug, LogLevel.Debug)]
    [DataRow(Level.Info, LogLevel.Information)]
    [DataRow(Level.Warning, LogLevel.Warning)]
    [DataRow(Level.Error, LogLevel.Error)]
    [DataRow(Level.Fatal, LogLevel.Critical)]
    public void ToLogLevel_MapsFaultLevel(Level level, LogLevel expected)
    {
        Assert.AreEqual(expected, MessagingEndpoints.ToLogLevel(level));
    }
}
