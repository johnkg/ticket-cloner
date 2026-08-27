using System.Net;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// Honours Jira Cloud's 429 + Retry-After.
///
/// Copying runs in batches, which is exactly the shape of traffic that trips
/// the limiter. Without this, a run of fifty tickets fails somewhere in the
/// middle for no reason a user could act on.
/// </summary>
public sealed class RateLimitHandler(ILogger<RateLimitHandler> logger) : DelegatingHandler
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxAttempts)
            {
                return response;
            }

            var delay = RetryAfter(response) ?? FallbackDelay;

            logger.LogWarning(
                "{Host} returned 429; waiting {Delay} before attempt {Attempt} of {MaxAttempts}",
                request.RequestUri?.Host, delay, attempt + 1, MaxAttempts);

            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Retry-After is either seconds or an HTTP date, and Jira uses both
    /// depending on which limiter fired.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        var delay = header switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => (TimeSpan?)null,
        };

        return delay is null or { Ticks: <= 0 } ? null : Min(delay.Value, MaxDelay);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
