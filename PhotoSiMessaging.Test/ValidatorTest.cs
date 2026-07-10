using FluentValidation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoSiMessaging.Exceptions;
using MessagingValidationException = PhotoSiMessaging.Exceptions.ValidationException;

namespace PhotoSiMessaging.Test;

[TestClass]
public class ValidatorTest
{
    private sealed class Msg
    {
        public string Name { get; set; } = "";
    }

    private sealed class MsgValidator : AbstractValidator<Msg>
    {
        public MsgValidator()
        {
            RuleFor(m => m.Name).NotEmpty();
        }
    }

    [TestMethod]
    public async Task ValidateOrThrow_Valid_DoesNotThrow()
    {
        await MessagingEndpoints.ValidateOrThrowAsync(new MsgValidator(), new Msg { Name = "ok" });
    }

    // An invalid message must surface as the INVALID_MESSAGE (550) fault, with the failing property in Detail.
    [TestMethod]
    public async Task ValidateOrThrow_Invalid_ThrowsInvalidMessageFaultWithDetail()
    {
        var exception = await Assert.ThrowsExceptionAsync<MessagingValidationException>(
            () => MessagingEndpoints.ValidateOrThrowAsync(new MsgValidator(), new Msg { Name = "" }));

        Assert.AreEqual(Code.InvalidMessage, exception.Code);
        StringAssert.Contains(exception.Detail?.ToString() ?? "", "Name");
    }
}
