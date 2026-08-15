using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class AdminSessionServiceTests
{
    [Fact]
    public void DisplayHandle_is_userid_not_email_local_part()
    {
        var session = new AdminSessionService(js: null);
        session.SetUserId("budcribar");
        Assert.Equal("@budcribar", session.DisplayHandle);

        // An email-shaped leftover session id is shown as-is — never stripped to a handle.
        session.SetUserId("budcribar@example.com");
        Assert.Equal("@budcribar@example.com", session.DisplayHandle);
    }
}
