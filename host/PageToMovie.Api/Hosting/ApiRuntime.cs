namespace PageToMovie.Api;

/// <summary>
/// Process-wide flags resolved once during host configuration.
/// <see cref="UseFakes"/> mirrors the startup check (config + PageToMovie_USE_FAKES env)
/// so request handlers do not close over top-level Program.cs locals.
/// </summary>
internal static class ApiRuntime
{
    public static bool UseFakes { get; set; }
}
