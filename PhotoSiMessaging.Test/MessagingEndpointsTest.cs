using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging;

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
}
