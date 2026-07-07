using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging.Exceptions;
using TimeoutException = PhotoSiMessaging.Exceptions.TimeoutException;

namespace PhotoSiMessaging.Test;

[TestClass]
public class ExceptionsTest
{
    [DataTestMethod]
    [DataRow(Code.ObjectNotFound, typeof(ObjectNotFoundException))]
    [DataRow(Code.InvalidAuthorization, typeof(SecurityException))]
    [DataRow(Code.InvalidMessage, typeof(ValidationException))]
    [DataRow(Code.Timeout, typeof(TimeoutException))]
    [DataRow(Code.MaxRetriesExceeded, typeof(MaxRetriesExceededException))]
    [DataRow(Code.OperationNotAllowed, typeof(OperationNotAllowedException))]
    [DataRow(Code.TooManyRequests, typeof(TooManyRequestsException))]
    [DataRow("SOMETHING_UNKNOWN", typeof(SomethingWentWrongException))]
    [DataRow(null, typeof(SomethingWentWrongException))]
    public void FromFault_MapsCodeToTypedException(string? code, Type expected)
    {
        var ex = BaseException.FromFault(code, "boom", "detail-x");

        Assert.IsInstanceOfType(ex, expected);
        Assert.AreEqual("boom", ex.Message);
        Assert.AreEqual("detail-x", ex.Detail);
        if (code is not null && code != "SOMETHING_UNKNOWN")
        {
            Assert.AreEqual(code, ex.Code);
        }
    }

    [TestMethod]
    public void ObjectNotFound_DefaultsToWarningLevel()
    {
        Assert.AreEqual(Level.Warning, new ObjectNotFoundException("x").Level);
        Assert.AreEqual(Level.Error, new SecurityException("x").Level);
    }
}
