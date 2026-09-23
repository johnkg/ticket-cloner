using TicketCloner.Api.Atlassian;

namespace TicketCloner.Api.OAuth;

/// <summary>
/// Hands out a usable access token, refreshing first when the stored one is
/// spent. Everything that needs to authorise as a signed-in user goes through
/// here rather than reading the store directly, so the refresh - and the
/// rotation it forces - happens in exactly one place.
/// </summary>
public sealed class TokenProvider(
    ITokenStore store,
    TokenRefreshLocks locks,
    AtlassianOAuthClient oauth,
    TimeProvider clock,
    ILogger<TokenProvider> logger)
{
    /// <summary>
    /// The credential for this user, or <see cref="NoCredential.Instance"/>
    /// when they are not signed in - which reads as "no credentials" to the
    /// endpoint filter and produces its clear 401.
    /// </summary>
    public async Task<IAtlassianCredential> CredentialForAsync(
        TokenKey key, CancellationToken cancellationToken)
    {
        var tokens = await ValidTokensAsync(key, cancellationToken);

        return tokens is null
            ? NoCredential.Instance
            : new BearerCredential(tokens.AccessToken);
    }

    /// <summary>
    /// Null means signed out: either nothing was stored, or the refresh token
    /// is dead and the user has to go through consent again.
    /// </summary>
    public async Task<OAuthTokens?> ValidTokensAsync(
        TokenKey key, CancellationToken cancellationToken)
    {
        var stored = store.Get(key);

        if (stored is null)
        {
            return null;
        }

        if (stored.IsUsableAt(clock.GetUtcNow()))
        {
            return stored;
        }

        var gate = locks.For(key);
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Read again inside the gate. Whoever held it may have refreshed
            // already, and refreshing a second time would burn the refresh token
            // they just stored.
            var current = store.Get(key);

            if (current is null)
            {
                return null;
            }

            if (current.IsUsableAt(clock.GetUtcNow()))
            {
                return current;
            }

            return await RefreshAsync(key, current, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OAuthTokens?> RefreshAsync(
        TokenKey key, OAuthTokens current, CancellationToken cancellationToken)
    {
        try
        {
            // With(), not a fresh record: the cloud id is not in the response
            // and losing it would leave the session pointed at nothing.
            var refreshed = current.With(await oauth.RefreshAsync(current.RefreshToken, cancellationToken));

            // The store write is the point of the whole exercise: the refresh
            // token that went up is now dead, so failing to keep the new one
            // signs this user out at the next refresh rather than now.
            store.Put(key, refreshed);

            logger.LogInformation("Refreshed the access token; it now {Tokens}", refreshed);

            return refreshed;
        }
        catch (OAuthTokenException failed)
        {
            // A refresh token is revoked, expired or already used. There is no
            // recovering from that here - the user has to consent again - so
            // drop it rather than retrying against a token that cannot work.
            logger.LogWarning(
                failed, "The refresh token is no longer usable; signing this user out");

            store.Remove(key);
            return null;
        }
    }
}
