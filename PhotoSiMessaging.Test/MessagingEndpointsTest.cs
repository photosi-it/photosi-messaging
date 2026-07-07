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
}
