using System.Security.Cryptography;
using TicketCloner.Api.Atlassian;

namespace TicketCloner.Api.OAuth;

/// <summary>
/// Who the browser is, and the one-shot value that ties a callback to the
/// sign-in that started it.
///
/// The tool has no user accounts, and this does not invent any. The cookie is
/// an opaque handle to a slot in the token store - it identifies a browser, not
/// a person, which is exactly as much as is needed to keep two people using the
/// same instance out of each other's tokens.
/// </summary>
public sealed class OAuthSession(IHttpContextAccessor accessor)
{
    public const string UserCookie = "tc.session";

    public const string StateCookie = "tc.oauth.state";

    private HttpContext Context =>
        accessor.HttpContext ?? throw new InvalidOperationException("No request in scope.");

    /// <summary>Null when this browser has never signed in.</summary>
    public string? User => Context.Request.Cookies[UserCookie] is { Length: > 0 } value
        ? value
        : null;

    public string EnsureUser()
    {
        if (User is { } existing)
        {
            return existing;
        }

        var user = Token();
        Context.Response.Cookies.Append(UserCookie, user, Cookie(TimeSpan.FromDays(30)));

        return user;
    }

    /// <summary>
    /// Starts a sign-in: returns the state to put on the authorize URL and
    /// remembers it, with the round, for the callback to check.
    ///
    /// The round exists because one consent adds one site. When the first
    /// grant reaches only one of the two tenants the callback starts a second
    /// round for the other, and the counter is what stops that bouncing for
    /// ever if the person keeps choosing the same site.
    /// </summary>
    public string StartState(int round)
    {
        var state = Token();

        // Short-lived and SameSite=Lax: the callback is a top-level GET
        // navigation from Atlassian, which Lax allows and Strict would not - the
        // cookie would simply not arrive and every sign-in would fail on state.
        Context.Response.Cookies.Append(
            StateCookie, $"{state}:{round}", Cookie(TimeSpan.FromMinutes(10)));

        return state;
    }

    /// <summary>
    /// Reads the pending sign-in and clears it. One shot: a replayed callback
    /// finds nothing, which is the point of the value.
    /// </summary>
    public (string State, int Round)? TakeState()
    {
        var raw = Context.Request.Cookies[StateCookie];
        Context.Response.Cookies.Delete(StateCookie, Cookie(TimeSpan.Zero));

        if (raw is null)
        {
            return null;
        }

        var split = raw.LastIndexOf(':');

        return split > 0 && int.TryParse(raw[(split + 1)..], out var round)
            ? (raw[..split], round)
            : null;
    }

    private CookieOptions Cookie(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,

        // Development runs on plain http and a Secure cookie would never be
        // sent, so sign-in would silently never work locally.
        Secure = Context.Request.IsHttps,
        Path = "/",
        MaxAge = lifetime == TimeSpan.Zero ? null : lifetime,
    };

    private static string Token() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
