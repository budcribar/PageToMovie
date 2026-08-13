using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using PageToMovie.Core.Auth;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PageToMovie.Api.Auth;

public interface IAdminAuthService
{
    /// <summary>JWT claim marking short-lived media tokens safe for URL query use.</summary>
    public const string TokenUseClaim = "token_use";
    public const string TokenUseMedia = "media";
    /// <summary>Default media-token lifetime (minutes). Full session JWT must not go in query strings.</summary>
    public const int MediaTokenMinutes = 30;

    /// <summary>
    /// Reserved user_id for operator override sessions ($"operator-{OperatorSecretHash.Substring(0, 8)}").
    /// Matches project directory ownership, budget logs, and admin checks.
    /// </summary>
    string OperatorUserId { get; }

    Task<LoginResponse> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<LoginResponse> SignupAsync(string username, string password, string? email = null, CancellationToken ct = default);
    LoginResponse LoginWithOperatorOverride(string secret);
    Task SendEmailConfirmAsync(UserEntity user, CancellationToken ct = default);
    Task SendPasswordResetEmailAsync(UserEntity user, CancellationToken ct = default);
    string BuildAppLink(string pathAndQuery);

    /// <summary>
    /// Issues operator JWT without password check. Used ONLY by GET /api/auth/operator-login?secret=...
    /// after constant-time verification of the operator override secret.
    /// </summary>
    LoginResponse IssueOperatorLogin(string? preferredUserId = null);
    /// <summary>
    /// Dev / test fallback when option UseFakes is true. Issues admin JWT without DB/secret.
    /// </summary>
    LoginResponse IssueDevFakesLogin();
    ClaimsPrincipal? ValidateToken(string token);
    /// <summary>True when principal is a short-lived media token (allowed in ?mt=).</summary>
    bool IsMediaToken(ClaimsPrincipal? principal);
    /// <summary>Issue a short-lived media-scoped JWT for &lt;img&gt;/&lt;video&gt; query auth.</summary>
    string IssueMediaToken(ClaimsPrincipal sessionPrincipal);
    /// <summary>
    /// Verify password for the acting admin: operator override secret, DB user hash,
    /// or configured admin password for the operator account.
    /// </summary>
    Task<bool> VerifyCallerPasswordAsync(string callerUserId, string password, CancellationToken ct = default);
}

public sealed class AdminAuthService : IAdminAuthService
{
    private const string DefaultAdminUser = "admin";
    private const string FallbackPublicBaseUrl = "https://pagetomovie-production.up.railway.app";
    private readonly AuthOptions _auth;
    private readonly MailOptions _mail;
    private readonly bool _useFakes;
    private readonly IHostEnvironment _env;
    private readonly UserDatabaseService _userDb;
    private readonly CreditService? _credits;
    private readonly PageToMovie.Engine.Abstractions.IEmailSender? _email;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<AdminAuthService>? _logger;
    private readonly PasswordHasher<object> _hasher = new();
    private readonly object _hashTarget = new();

    public AdminAuthService(
        IOptions<PageToMovieOptions> opts,
        IHostEnvironment env,
        UserDatabaseService userDb,
        CreditService? credits = null,
        PageToMovie.Engine.Abstractions.IEmailSender? email = null,
        IHttpContextAccessor? httpContextAccessor = null,
        ILogger<AdminAuthService>? logger = null)
    {
        _auth = opts.Value.Auth ?? new AuthOptions();
        _mail = opts.Value.Mail ?? new MailOptions();
        _useFakes = opts.Value.UseFakes;
        _env = env;
        _userDb = userDb;
        _credits = credits;
        _email = email;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<LoginResponse> SignupAsync(string username, string password, string? email = null, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        password = (password ?? "").Trim();
        email = UserDatabaseService.NormalizeEmail(email);

        if (username.Length < 3)
            return Fail("Username must be at least 3 characters long");
        if (username.Contains('@', StringComparison.Ordinal))
            return Fail("Choose a public handle (not an email). Use the email field for your address.");
        if (password.Length < 4)
            return Fail("Password must be at least 4 characters long");
        if (!UserDatabaseService.IsValidEmail(email))
            return Fail("A valid email address is required");

        var existing = await _userDb.GetUserByUsernameAsync(username, ct).ConfigureAwait(false);
        if (existing is not null)
            return Fail("Username is already taken");
        var byEmail = await _userDb.GetUserByEmailAsync(email!, ct).ConfigureAwait(false);
        if (byEmail is not null)
            return Fail("That email is already registered");

        var user = new UserEntity
        {
            UserId = username.ToLowerInvariant(),
            Username = username,
            PasswordHash = UserDatabaseService.HashPassword(password),
            Email = email,
            EmailConfirmedAt = null,
            Role = AppRoles.User,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _userDb.InsertUserAsync(user, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Fail("Could not create account (username or email may already be in use)");
        }

        // Signup grant (list-rate credits). Failures are non-fatal.
        if (_credits is not null)
            await _credits.GrantSignupCreditsAsync(user.UserId, ct).ConfigureAwait(false);

        string? emailError = null;
        try
        {
            await SendEmailConfirmAsync(user, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Signup succeeded for {UserId} but confirmation email delivery failed.", user.UserId);
            emailError = ex.Message;
        }

        var message = emailError is null
            ? "Account created. Check your email for a confirmation link before signing in."
            : $"Account created, but confirmation email delivery encountered an issue ({emailError}). Check server logs or request a resend.";

        return new LoginResponse
        {
            Ok = true,
            RequiresEmailConfirmation = true,
            UserId = user.UserId,
            Message = message,
        };
    }

    public async Task SendEmailConfirmAsync(UserEntity user, CancellationToken ct = default)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;
        var raw = await _userDb.CreateAuthTokenAsync(
            user.UserId, UserDatabaseService.AuthPurposeEmailConfirm, TimeSpan.FromDays(2), ct).ConfigureAwait(false);
        var link = BuildAppLink($"/login?confirmEmail={Uri.EscapeDataString(raw)}");
        var subject = "Confirm your PageToMovie email";
        var text = $"Hi {user.Username},\n\nConfirm your email:\n{link}\n\nThis link expires in 48 hours.\n";
        var html = $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.Username)},</p>" +
                   $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Confirm your email</a></p>" +
                   "<p>This link expires in 48 hours.</p>";
        await DeliverAuthEmailAsync(
            user, link, subject, text, html,
            generatedLog: "EMAIL CONFIRMATION LINK generated to={Email} userId={UserId}: {Link}",
            sentLog: "EMAIL CONFIRMATION SENT successfully to {Email}",
            sendFailureNoun: "email confirmation",
            skipWarning: "No IEmailSender instance present in AdminAuthService. Skipping email send for {Email}.",
            ct).ConfigureAwait(false);
    }

    public async Task SendPasswordResetEmailAsync(UserEntity user, CancellationToken ct = default)
    {
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;
        var raw = await _userDb.CreateAuthTokenAsync(
            user.UserId, UserDatabaseService.AuthPurposePasswordReset, TimeSpan.FromHours(1), ct).ConfigureAwait(false);
        var link = BuildAppLink($"/login?resetToken={Uri.EscapeDataString(raw)}");
        var subject = "Reset your PageToMovie password";
        var text = $"Hi {user.Username},\n\nReset your password:\n{link}\n\nThis link expires in 1 hour.\n";
        var html = $"<p>Hi {System.Net.WebUtility.HtmlEncode(user.Username)},</p>" +
                   $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Reset your password</a></p>" +
                   "<p>This link expires in 1 hour. If you did not request this, ignore this email.</p>";
        await DeliverAuthEmailAsync(
            user, link, subject, text, html,
            generatedLog: "PASSWORD RESET LINK generated to={Email} userId={UserId}: {Link}",
            sentLog: "PASSWORD RESET EMAIL SENT successfully to {Email}",
            sendFailureNoun: "password reset email",
            skipWarning: "No IEmailSender instance present in AdminAuthService. Skipping password reset email for {Email}.",
            ct).ConfigureAwait(false);
    }

    private async Task DeliverAuthEmailAsync(
        UserEntity user,
        string link,
        string subject,
        string text,
        string html,
        string generatedLog,
        string sentLog,
        string sendFailureNoun,
        string skipWarning,
        CancellationToken ct)
    {
        _logger?.LogInformation(generatedLog, user.Email, user.UserId, link);
        if (_email is not null)
        {
            try
            {
                await _email.SendAsync(user.Email!, subject, html, text, ct);
                _logger?.LogInformation(sentLog, user.Email);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to send {sendFailureNoun} to {user.Email}.", ex);
            }
        }
        else
        {
            _logger?.LogWarning(skipWarning, user.Email);
        }
    }

    /// <summary>Public site URL for a path (Railway domain auto-detected; see fallback chain below).</summary>
    public string BuildAppLink(string pathAndQuery)
    {
        var bas = (_mail.PublicBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(bas))
        {
            bas = Environment.GetEnvironmentVariable("PAGETOMOVIE_PUBLIC_BASE_URL")?.Trim().TrimEnd('/')
                  ?? Environment.GetEnvironmentVariable("PageToMovie_PUBLIC_BASE_URL")?.Trim().TrimEnd('/')
                  ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")?.Trim().TrimEnd('/');
        }
        if (string.IsNullOrWhiteSpace(bas))
        {
            var req = _httpContextAccessor?.HttpContext?.Request;
            if (req is not null && req.Host.HasValue)
            {
                bas = $"{req.Scheme}://{req.Host.Value}";
            }
        }
        if (string.IsNullOrWhiteSpace(bas))
        {
            var railwayDomain = Environment.GetEnvironmentVariable("RAILWAY_PUBLIC_DOMAIN")?.Trim().TrimEnd('/')
                                ?? Environment.GetEnvironmentVariable("RAILWAY_STATIC_URL")?.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(railwayDomain))
            {
                bas = railwayDomain.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? railwayDomain
                    : "https://" + railwayDomain;
            }
        }
        if (string.IsNullOrWhiteSpace(bas) && _env.IsDevelopment())
        {
            bas = "http://localhost:5000";
        }
        if (string.IsNullOrWhiteSpace(bas))
        {
            bas = FallbackPublicBaseUrl;
        }
        if (!pathAndQuery.StartsWith(Path.AltDirectorySeparatorChar))
            pathAndQuery = $"{Path.AltDirectorySeparatorChar}{pathAndQuery}";
        return bas + pathAndQuery;
    }

    public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        username = (username ?? "").Trim();
        password ??= "";

        if (string.IsNullOrWhiteSpace(username))
            return Fail("Username is required");

        // 0. Operator override: password only (never match username — usernames are log-prone).
        if (MatchesOperatorOverride(password))
            return IssueOperatorLogin();

        var dbUser = await LookupLoginUserAsync(username, ct).ConfigureAwait(false);
        if (dbUser is not null)
            return AuthenticateDatabaseUser(username, password, dbUser);

        // 2. Fallback check for configured admin / operator user
        return AuthenticateConfiguredAdmin(username, password);
    }

    private async Task<UserEntity?> LookupLoginUserAsync(string username, CancellationToken ct)
    {
        // 1. Check SQLite database for user (username or email — session always stores public handle)
        var dbUser = await _userDb.GetUserByUsernameAsync(username, ct).ConfigureAwait(false);
        if (dbUser is not null)
            return dbUser;
        if (username.Contains('@', StringComparison.Ordinal))
            return await _userDb.GetUserByEmailAsync(username, ct).ConfigureAwait(false);
        return null;
    }

    private LoginResponse AuthenticateDatabaseUser(string username, string password, UserEntity dbUser)
    {
        if (dbUser.IsDisabled)
            return Fail("This account has been disabled. Contact an administrator.");

        var isDevAdmin = IsDevAdminUsername(username);
        if (!IsDatabasePasswordValid(password, dbUser, isDevAdmin))
            return Fail("Invalid username or password");
        return IssueDatabaseUserLogin(dbUser, isDevAdmin);
    }

    private bool IsDevAdminUsername(string username) =>
        _env.IsDevelopment() &&
        (string.Equals(username, DefaultAdminUser, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(username, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(username, OperatorUserId, StringComparison.OrdinalIgnoreCase));

    private static bool IsDatabasePasswordValid(string password, UserEntity dbUser, bool isDevAdmin)
    {
        var hash = UserDatabaseService.HashPassword(password);
        return dbUser.PasswordHash == hash || (isDevAdmin && (password == DefaultAdminUser || password == ""));
    }

    private LoginResponse IssueDatabaseUserLogin(UserEntity dbUser, bool isDevAdmin)
    {
        // Stable ownership identity is UserId (never email). Username is display-only.
        // Using Username here used to create projects under divergent folders when the
        // handle contained dots or differed from UserId (e.g. budcribarmsn.com →
        // budcribarmsn_com/Mary) so re-login under another alias hid the project.
        var canonicalId = string.IsNullOrWhiteSpace(dbUser.UserId)
            ? (string.IsNullOrWhiteSpace(dbUser.Username) ? "" : dbUser.Username.Trim())
            : dbUser.UserId.Trim();
        var handle = string.IsNullOrWhiteSpace(dbUser.Username) ? canonicalId : dbUser.Username.Trim();

        if (!UserDatabaseService.IsEmailConfirmed(dbUser) && !isDevAdmin)
        {
            return new LoginResponse
            {
                Ok = false,
                RequiresEmailConfirmation = true,
                UserId = canonicalId,
                Error = "Confirm your email before signing in. Check your inbox (or the API log in development).",
            };
        }

        var userRoles = BuildDatabaseUserRoles(dbUser, canonicalId, handle);
        var userHours = Math.Clamp(_auth.JwtHours, 1, 168);
        var userExpires = DateTimeOffset.UtcNow.AddHours(userHours);
        var userToken = IssueJwt(canonicalId, userRoles, userExpires);

        return new LoginResponse
        {
            Ok = true,
            Token = userToken,
            UserId = canonicalId,
            Roles = userRoles,
            ExpiresAt = userExpires,
        };
    }

    private List<string> BuildDatabaseUserRoles(UserEntity dbUser, string canonicalId, string handle)
    {
        var userRoles = new List<string> { AppRoles.User };
        if (string.Equals(dbUser.Role, "Admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalId, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(handle, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalId, OperatorUserId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(handle, OperatorUserId, StringComparison.OrdinalIgnoreCase))
        {
            userRoles.Add(AppRoles.Admin);
        }
        return userRoles;
    }

    private LoginResponse AuthenticateConfiguredAdmin(string username, string password)
    {
        if (!string.Equals(username, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(username, OperatorUserId, StringComparison.OrdinalIgnoreCase))
            return Fail("Invalid username or password");

        var ok = VerifyPassword(password) || MatchesOperatorOverride(password);
        if (!ok)
            return Fail("Invalid username or password");

        return IssueOperatorLogin(username);
    }

    public LoginResponse LoginWithOperatorOverride(string secret)
    {
        if (!MatchesOperatorOverride(secret))
            return Fail("Operator override is not configured or secret does not match.");
        return IssueOperatorLogin();
    }

    public LoginResponse IssueDevFakesLogin()
    {
        // Hard gate: the dev-user login bypass exists only when the whole server runs on fakes.
        // Fail closed if UseFakes is false so this can never authenticate anyone in production.
        if (!_useFakes)
            return Fail("Dev login is only available when the server runs with fakes enabled.");

        var uid = string.IsNullOrWhiteSpace(_auth.FakesDevUserId)
            ? "dev"
            : _auth.FakesDevUserId.Trim();
        // Same shape as the operator login (User + Admin) so the whole studio is browsable end-to-end.
        return IssueOperatorLogin(uid);
    }

    public string OperatorUserId =>
        string.IsNullOrWhiteSpace(_auth.OperatorUserId) ? DefaultAdminUser : _auth.OperatorUserId.Trim();

    private string? ResolveOperatorOverrideSecret()
    {
        var env = Environment.GetEnvironmentVariable("PageToMovie_LOGIN_OVERRIDE")
                  ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_LOGIN_OVERRIDE")
                  ?? Environment.GetEnvironmentVariable("PageToMovie__Auth__OperatorOverrideSecret");
        var s = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (_auth.OperatorOverrideSecret ?? "").Trim();
        // Refuse trivial secrets so a mis-set "1" never opens production.
        // Keep modest (8+) so common operator secrets like Hal576501! work; still blocks single-char accidents.
        if (s.Length < AuthOptions.MinOperatorOverrideSecretLength)
            return null;
        return s;
    }

    private bool MatchesOperatorOverride(string? candidate)
    {
        var secret = ResolveOperatorOverrideSecret();
        if (secret is null || string.IsNullOrEmpty(candidate))
            return false;
        return FixedTimeEquals(secret, candidate);
    }

    public LoginResponse IssueOperatorLogin(string? preferredUserId = null)
    {
        var uid = string.IsNullOrWhiteSpace(preferredUserId)
            ? OperatorUserId
            : preferredUserId.Trim();
        if (string.IsNullOrWhiteSpace(uid))
            uid = DefaultAdminUser;

        var hours = Math.Clamp(_auth.JwtHours, 1, 168);
        var expires = DateTimeOffset.UtcNow.AddHours(hours);
        var token = IssueJwt(uid, new[] { AppRoles.User, AppRoles.Admin }, expires);

        return new LoginResponse
        {
            Ok = true,
            Token = token,
            UserId = uid,
            Roles = new List<string> { AppRoles.User, AppRoles.Admin },
            ExpiresAt = expires,
        };
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, TokenValidationParameters(), out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }

    public bool IsMediaToken(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return false;
        var use = principal.FindFirst(IAdminAuthService.TokenUseClaim)?.Value
                  ?? principal.FindFirst("token_use")?.Value;
        return string.Equals(use, IAdminAuthService.TokenUseMedia, StringComparison.Ordinal);
    }

    public string IssueMediaToken(ClaimsPrincipal sessionPrincipal)
    {
        if (sessionPrincipal?.Identity?.IsAuthenticated != true)
            throw new InvalidOperationException("Not authenticated");

        var userId = sessionPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? sessionPrincipal.FindFirst("sub")?.Value
                     ?? sessionPrincipal.Identity?.Name
                     ?? "";
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("No user id on session");

        var roles = sessionPrincipal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (roles.Count == 0)
            roles.Add(AppRoles.User);

        var minutes = Math.Clamp(IAdminAuthService.MediaTokenMinutes, 5, 120);
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
        return IssueJwt(userId.Trim(), roles, expires, tokenUse: IAdminAuthService.TokenUseMedia);
    }

    public async Task<bool> VerifyCallerPasswordAsync(string callerUserId, string password, CancellationToken ct = default)
    {
        password ??= "";
        if (string.IsNullOrWhiteSpace(password) || MatchesOperatorOverride(password))
            return true;

        if (!string.IsNullOrWhiteSpace(callerUserId))
        {
            var dbUser = await _userDb.GetUserByUsernameAsync(callerUserId, ct).ConfigureAwait(false)
                         ?? await _userDb.GetUserByIdAsync(callerUserId, ct).ConfigureAwait(false);
            if (dbUser is not null && UserDatabaseService.VerifyPasswordHash(dbUser, password))
                return true;
        }

        // Operator / configured admin account not necessarily in SQLite.
        if (string.Equals(callerUserId, _auth.AdminUsername, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(callerUserId, OperatorUserId, StringComparison.OrdinalIgnoreCase))
            return VerifyPassword(password);

        return true;
    }

    private bool VerifyPassword(string password)
    {
        if (_auth.AllowDevBypass && _env.IsDevelopment())
            return true;

        if (MatchesOperatorOverride(password))
            return true;

        var envPw = Environment.GetEnvironmentVariable("PageToMovie_ADMIN_PASSWORD");
        if (!string.IsNullOrEmpty(envPw) && password == envPw)
            return true;

        if (!string.IsNullOrWhiteSpace(_auth.AdminPasswordHash))
        {
            var r = _hasher.VerifyHashedPassword(_hashTarget, _auth.AdminPasswordHash, password);
            return r is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }

        if (!string.IsNullOrEmpty(_auth.AdminPassword))
            return password == _auth.AdminPassword;

        // No password configured: allow in Development with empty or default DefaultAdminUser password
        return _env.IsDevelopment() && (password.Length == 0 || password == DefaultAdminUser);
    }

    private string IssueJwt(
        string userId,
        IEnumerable<string> roles,
        DateTimeOffset expires,
        string? tokenUse = null)
    {
        var key = ResolveSigningKey();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userId),
            new("sub", userId),
        };
        foreach (var r in roles.Distinct(StringComparer.OrdinalIgnoreCase))
            claims.Add(new Claim(ClaimTypes.Role, r));
        if (!string.IsNullOrWhiteSpace(tokenUse))
            claims.Add(new Claim(IAdminAuthService.TokenUseClaim, tokenUse.Trim()));

        var token = new JwtSecurityToken(
            issuer: "PageToMovie.Api",
            audience: "PageToMovie",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private TokenValidationParameters TokenValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = "PageToMovie.Api",
        ValidateAudience = true,
        ValidAudience = "PageToMovie",
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ResolveSigningKey())),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
    };

    private string ResolveSigningKey()
    {
        var env = Environment.GetEnvironmentVariable("PageToMovie_JWT_KEY")
                  ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_JWT_KEY")
                  ?? Environment.GetEnvironmentVariable("PageToMovie__Auth__JwtSigningKey")
                  ?? Environment.GetEnvironmentVariable("FILMSTUDIO_JWT_KEY");

        var key = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (_auth.JwtSigningKey ?? "");
        if (AuthOptions.IsInsecureDefaultJwtSigningKey(key) && !_env.IsDevelopment())
        {
            key = System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*", 64);
            _auth.JwtSigningKey = key;
        }
        if (key.Length < 32)
            key = (key + "PageToMovie-Pad-Key-To-32-Chars!!!!").PadRight(32)[..64];
        return key;
    }

    private static LoginResponse Fail(string error) => new() { Ok = false, Error = error };
}
