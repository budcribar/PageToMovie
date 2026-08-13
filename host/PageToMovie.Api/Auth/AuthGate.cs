using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Options;

namespace PageToMovie.Api.Auth;

/// <summary>
/// Shared 401/403 checks for project mutation, API-key settings, and import/gen.
/// </summary>
public static class AuthGate
{
    /// <summary>401 unless JWT present (or <see cref="AuthOptions.RequireLogin"/> is false).</summary>
    public static IResult? RequireLogin(IUserContext user, IOptions<PageToMovieOptions> opts)
    {
        var auth = opts.Value.Auth ?? new AuthOptions();
        if (!auth.RequireLogin)
            return null;
        if (user.IsAuthenticated || user.IsAdmin)
            return null;
        return Results.Json(
            new
            {
                ok = false,
                error = "Sign in required. Open /login or /signup, then try again.",
                code = "auth_required",
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Requires the project owner (or an administrator) for mutations and paid operations.
    /// Authentication-disabled test/load-simulation hosts retain their existing permissive behavior.
    /// </summary>
    public static async Task<IResult?> RequireProjectOwnerAsync(
        string projectId,
        IUserContext user,
        ProjectStore projects,
        IOptions<PageToMovieOptions> opts,
        CancellationToken ct = default)
    {
        var auth = opts.Value.Auth ?? new AuthOptions();
        if (!auth.RequireLogin)
            return null;

        var login = RequireLogin(user, opts);
        if (login is not null)
            return login;

        if (await projects.CanUserPublishDemoAsync(projectId, user.UserId, user.IsAdmin, ct).ConfigureAwait(false))
            return null;

        return Results.Json(
            new
            {
                ok = false,
                error = "Only this project's owner or an administrator can perform that action.",
                code = "project_access_denied",
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Login + personal (BYOK) key in the user DB. Server env keys do not count until
    /// <see cref="PageToMovieOptions.AllowServerApiKeyFallback"/> is enabled.
    /// Vision OCR needs personal Grok; screenplay may use personal Grok / OpenAI / Anthropic / Gemini.
    /// </summary>
    public static async Task<IResult?> RequirePersonalGrokKeyAsync(
        IUserContext user,
        UserDatabaseService userDb,
        IOptions<PageToMovieOptions> opts,
        bool useFakes = false,
        IUserApiKeyProvider? keys = null,
        bool requireVisionKey = false)
    {
        var login = RequireLogin(user, opts);
        if (login is not null)
            return login;

        if (useFakes || opts.Value.UseFakes)
            return null;

        var providers = PersonalKeyProviders(requireVisionKey);
        if (await HasAnyPersonalProviderKeyAsync(user, userDb, providers).ConfigureAwait(false))
            return null;

        if (opts.Value.AllowServerApiKeyFallback &&
            await HasServerFallbackKeyAsync(user, keys, providers, requireVisionKey).ConfigureAwait(false))
            return null;

        return PersonalKeyForbiddenResult(requireVisionKey);
    }

    private static string[] PersonalKeyProviders(bool requireVisionKey) =>
        requireVisionKey
            ? new[] { "grok" }
            : new[] { "grok", "openai", "anthropic", "gemini" };

    private static async Task<bool> HasAnyPersonalProviderKeyAsync(
        IUserContext user,
        UserDatabaseService userDb,
        IReadOnlyList<string> providers)
    {
        foreach (var p in providers)
        {
            try
            {
                var personal = await userDb.GetDecryptedProviderApiKeyAsync(user.UserId, p).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(personal))
                    return true;
            }
            catch { /* next */ }
        }

        return false;
    }

    private static async Task<bool> HasServerFallbackKeyAsync(
        IUserContext user,
        IUserApiKeyProvider? keys,
        IReadOnlyList<string> providers,
        bool requireVisionKey)
    {
        if (keys is not null)
            return await HasProviderKeySlotAsync(user, keys, providers).ConfigureAwait(false);

        var envKeys = requireVisionKey
            ? new[] { "XAI_API_KEY" }
            : new[] { "XAI_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "GEMINI_API_KEY" };
        return envKeys.Any(env => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env)));
    }

    private static async Task<bool> HasProviderKeySlotAsync(
        IUserContext user,
        IUserApiKeyProvider keys,
        IReadOnlyList<string> providers)
    {
        foreach (var p in providers)
        {
            if (await keys.HasKeyAsync(user.UserId, p).ConfigureAwait(false) ||
                await keys.HasKeyAsync(null, p).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private static IResult PersonalKeyForbiddenResult(bool requireVisionKey)
    {
        var error = requireVisionKey
            ? "Save your personal xAI / Grok API key in Settings before PDF vision OCR. (Server env keys are not used in bring-your-own-key mode.)"
            : "Save a personal API key in Settings (Grok, OpenAI, Anthropic, or Gemini) before generating a screenplay. Server env keys are not used until cost management is enabled.";

        return Results.Json(
            new
            {
                ok = false,
                error,
                code = "personal_key_required",
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// 403 unless the signed-in user has accepted the Terms & IP Licensing Agreement
    /// (<c>terms_accepted_at</c>). Composes with <see cref="RequireLogin"/> — a caller only needs
    /// this one check on gated endpoints (project create, gen, publish).
    /// Skips entirely when <see cref="AuthOptions.RequireLogin"/> is false (tests / LoadSim), and
    /// for <see cref="IUserContext.IsAdmin"/>, matching <see cref="RequireLogin"/>'s own bypasses.
    /// </summary>
    public static async Task<IResult?> RequireTermsAcceptedAsync(
        IUserContext user,
        UserDatabaseService userDb,
        IOptions<PageToMovieOptions> opts)
    {
        var auth = opts.Value.Auth ?? new AuthOptions();
        if (!auth.RequireLogin)
            return null;

        var login = RequireLogin(user, opts);
        if (login is not null)
            return login;

        if (user.IsAdmin)
            return null;

        var accepted = await userDb.HasAcceptedTermsAsync(user.UserId).ConfigureAwait(false);
        if (accepted)
            return null;

        return Results.Json(
            new
            {
                ok = false,
                error = "Accept the Terms & IP Licensing Agreement before creating or generating content.",
                code = "terms_required",
            },
            statusCode: StatusCodes.Status403Forbidden);
    }
}
