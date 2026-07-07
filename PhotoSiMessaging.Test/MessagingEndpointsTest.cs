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

    // Il fault 550 DEVE uscire in PascalCase: sls-messaging (C#) e sls-messaging-python
    // deserializzano case-sensitive su ExceptionCode/ExceptionMessage/ExceptionDetail.
    [TestMethod]
    public void SerializeFault_EmitsPascalCaseFields()
    {
        var body = MessagingEndpoints.SerializeFault(new ObjectNotFoundException("cart 42 not found"));

        StringAssert.Contains(body, "\"ExceptionCode\":\"OBJECT_NOT_FOUND\"");
        StringAssert.Contains(body, "\"ExceptionMessage\":\"cart 42 not found\"");
        Assert.IsFalse(body.Contains("\"exceptionCode\""), "fault serialized camelCase: sls-messaging non lo decodifica");
    }

    [TestMethod]
    public void SerializeFault_ComplexDetail_SerializedAsJsonNotTypeName()
    {
        var ex = new ValidationException("bad cart") { Detail = new { Id = 42 } };

        var body = MessagingEndpoints.SerializeFault(ex);

        // round-trip: il detail decodificato è il JSON dell'oggetto, non il nome del tipo
        var fault = System.Text.Json.JsonSerializer.Deserialize<Exceptions.ResponseException>(body);
        Assert.AreEqual("""{"Id":42}""", fault!.ExceptionDetail);
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
