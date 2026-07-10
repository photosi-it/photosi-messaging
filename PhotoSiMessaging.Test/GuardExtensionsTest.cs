using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging.Exceptions;

namespace PhotoSiMessaging.Test;

[TestClass]
public class GuardExtensionsTest
{
    [TestMethod]
    public void ShouldNotBeNull_Null_ThrowsObjectNotFoundWarning()
    {
        string? value = null;

        var exception = Assert.ThrowsException<ObjectNotFoundException>(() => value.ShouldNotBeNull());

        Assert.AreEqual(Code.ObjectNotFound, exception.Code);
        Assert.AreEqual(Level.Warning, exception.Level);
    }

    [TestMethod]
    public void ShouldNotBeNull_NotNull_DoesNotThrow()
    {
        "ok".ShouldNotBeNull();
    }

    [TestMethod]
    public void ShouldNotBeEmpty_Empty_ThrowsObjectNotFound()
    {
        Assert.ThrowsException<ObjectNotFoundException>(() => new List<int>().ShouldNotBeEmpty());
    }

    [TestMethod]
    public void ShouldBeNull_NotNull_ThrowsOperationNotAllowed()
    {
        var exception = Assert.ThrowsException<OperationNotAllowedException>(() => "exists".ShouldBeNull());

        Assert.AreEqual(Code.OperationNotAllowed, exception.Code);
    }

    [TestMethod]
    public void ShouldBeNull_Null_DoesNotThrow()
    {
        string? value = null;
        value.ShouldBeNull();
    }
}
