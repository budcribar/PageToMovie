using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

using PageToMovie.Core.Utils;
namespace PageToMovie.UiTests;

[Collection("ui")]
public class AuthUiTests
{
    private readonly AppFixture _fx;
    public AuthUiTests(AppFixture fx) => _fx = fx;

    [Fact]
    public async Task Signup_and_email_confirmation_ui_flow()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var username = "confirm_ui_" + suffix;
            var email = username + "@example.com";
            var password = "Password123!";

            // 1. Navigate to /signup
            await page.GotoAsync(_fx.BaseUrl + "/signup");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // 2. Fill signup form
            await page.Locator("#username-input").FillAsync(username);
            await page.Locator("#email-input").FillAsync(email);
            await page.Locator("#password-input").FillAsync(password);
            await page.Locator("#confirm-password-input").FillAsync(password);

            // Submit signup
            await page.Locator("button[type='submit']").ClickAsync();

            // 3. Verify signup success alert
            await Assertions.Expect(page.Locator(".alert-success, [role='alert']").First).ToBeVisibleAsync();

            // 4. Fetch the confirmation token via API or admin helper
            using var http = new HttpClient { BaseAddress = new Uri(_fx.BaseUrl) };
            var logsResp = await http.GetAsync("/api/admin/logs?me=SECRET");
            var confirmToken = await FetchLatestTokenAsync(http, username, "email_confirm");
            Assert.False(string.IsNullOrWhiteSpace(confirmToken), "Email confirmation token should exist");

            // 5. Navigate to confirmation link
            await page.GotoAsync($"{_fx.BaseUrl}/login?confirmEmail={Uri.EscapeDataString(confirmToken)}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // 6. Verify URL clean replace and confirmed message
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("emailConfirmed=1", RegexOptions.None, CommonRegex.Timeout));
            await Assertions.Expect(page.GetByText("Email confirmed. You can sign in now.")).ToBeVisibleAsync();

            // 7. Login with confirmed credentials
            await page.Locator("#username-input").FillAsync(username);
            await page.Locator("#password-input").FillAsync(password);
            await page.Locator("button[type='submit']").ClickAsync();

            // 8. Verify redirection away from login
            await page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = 10000 });
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }

    [Fact]
    public async Task Password_reset_ui_flow()
    {
        var (ctx, page) = await _fx.NewPageAsync();
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var username = "reset_ui_" + suffix;
            var email = username + "@example.com";
            var initialPassword = "OldPassword123!";
            var newPassword = "NewPassword456!";

            using var http = new HttpClient { BaseAddress = new Uri(_fx.BaseUrl) };

            // 1. Create and confirm user account
            var signup = await http.PostAsJsonAsync("/api/auth/signup", new { username, password = initialPassword, email });
            Assert.True(signup.IsSuccessStatusCode);
            var confirmToken = await FetchLatestTokenAsync(http, username, "email_confirm");
            Assert.NotNull(confirmToken);
            await http.PostAsJsonAsync("/api/auth/confirm-email", new { token = confirmToken });

            // 2. Open /login, click "Forgot password?"
            await page.GotoAsync(_fx.BaseUrl + "/login");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await page.GetByText("Forgot password?").ClickAsync();
            await Assertions.Expect(page.Locator("#forgot-username")).ToBeVisibleAsync();

            // 3. Request password reset link
            await page.Locator("#forgot-username").FillAsync(username);
            await page.Locator("button[type='submit']").ClickAsync();

            await Assertions.Expect(page.GetByText("If that account exists and has a confirmed email")).ToBeVisibleAsync();

            // 4. Retrieve reset token
            var resetToken = await FetchLatestTokenAsync(http, username, "password_reset");
            Assert.False(string.IsNullOrWhiteSpace(resetToken), "Password reset token should exist");

            // 5. Open reset link /login?resetToken=...
            await page.GotoAsync($"{_fx.BaseUrl}/login?resetToken={Uri.EscapeDataString(resetToken)}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await Assertions.Expect(page.Locator("#reset-password-input")).ToBeVisibleAsync();

            // 6. Submit new password
            await page.Locator("#reset-password-input").FillAsync(newPassword);
            await page.Locator("#reset-confirm-input").FillAsync(newPassword);
            await page.Locator("button[type='submit']").ClickAsync();

            // 7. Verify success message
            await Assertions.Expect(page.GetByText("Password updated successfully. You can sign in now.")).ToBeVisibleAsync();

            // 8. Log in with new password
            await page.Locator("#username-input").FillAsync(username);
            await page.Locator("#password-input").FillAsync(newPassword);
            await page.Locator("button[type='submit']").ClickAsync();

            // 9. Verify redirection away from login
            await page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = 10000 });
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }

    private static async Task<string?> FetchLatestTokenAsync(HttpClient client, string username, string purpose)
    {
        // Query admin logs export zip / admin log snapshot to extract generated token link
        var resp = await client.GetAsync("/api/admin/logs?me=SECRET");
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        if (!json.TryGetProperty("logs", out var logsArr)) return null;

        foreach (var item in logsArr.EnumerateArray())
        {
            var line = item.GetString() ?? "";
            if (line.Contains(username, StringComparison.OrdinalIgnoreCase) &&
                line.Contains(purpose == "email_confirm" ? "confirmEmail=" : "resetToken=", StringComparison.OrdinalIgnoreCase))
            {
                var key = purpose == "email_confirm" ? "confirmEmail=" : "resetToken=";
                var idx = line.IndexOf(key, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var sub = line[(idx + key.Length)..];
                    var spaceIdx = sub.IndexOfAny(new[] { ' ', '"', '\'', '\n', '\r', '&' });
                    return spaceIdx > 0 ? sub[..spaceIdx] : sub;
                }
            }
        }
        return null;
    }
}
