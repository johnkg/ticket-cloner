using System.Collections.Concurrent;

namespace TicketCloner.Api.OAuth;

public interface ITokenStore
{
    OAuthTokens? Get(TokenKey key);

    void Put(TokenKey key, OAuthTokens tokens);

    void Remove(TokenKey key);
}

/// <summary>
/// Tokens for this process only.
///
/// KNOWN LIMIT, and it is not a detail: a restart signs everybody out, and two
/// instances behind a load balancer each hold half the sessions. That is
/// acceptable while this runs as one site on one box - a signed-out user
/// presses sign in again - and is the first thing to replace if it is ever
/// scaled out or asked to survive a deployment.
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<TokenKey, OAuthTokens> _tokens = new();

    public OAuthTokens? Get(TokenKey key) => _tokens.GetValueOrDefault(key);

    public void Put(TokenKey key, OAuthTokens tokens) => _tokens[key] = tokens;

    public void Remove(TokenKey key) => _tokens.TryRemove(key, out _);
}

/// <summary>
/// One refresh at a time per user.
///
/// Per user rather than per tenant because one account-level grant serves both
/// - two gates over one refresh token would not be a gate at all.
///
/// Without this, two requests arriving together on an expired token both
/// refresh. Both succeed, but the refresh tokens rotate - so the second call
/// invalidates the first call's, whichever of them the store ends up holding is
/// a coin toss, and the user is signed out at the next refresh. The window is
/// small and the symptom is a random logout, which is the worst combination to
/// diagnose after the fact.
/// </summary>
public sealed class TokenRefreshLocks : IDisposable
{
    private readonly ConcurrentDictionary<TokenKey, SemaphoreSlim> _locks = new();

    public SemaphoreSlim For(TokenKey key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        _locks.Clear();
    }
}
