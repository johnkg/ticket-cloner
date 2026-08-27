using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using TicketCloner.Api.Atlassian;

namespace TicketCloner.Api.Tests;

/// <summary>
/// Copying runs in batches, which is the traffic shape that trips Jira's
/// limiter. A run that dies partway through with a bare 429 gives a user
/// nothing to act on.
/// </summary>
public class RateLimitTests
{
    private sealed class Sequence(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
    }

    private static HttpResponseMessage TooManyRequests(int retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private static HttpClient ClientOver(HttpMessageHandler inner) =>
        new(new RateLimitHandler(NullLogger<RateLimitHandler>.Instance) { InnerHandler = inner })
        {
            BaseAddress = new Uri("https://target-domain.atlassian.net/"),
        };

    [Fact]
    public async Task A_429_is_retried_after_the_interval_the_tenant_asked_for()
    {
        var sequence = new Sequence(
            TooManyRequests(1),
            new HttpResponseMessage(HttpStatusCode.OK));

        using var client = ClientOver(sequence);

        var response = await client.GetAsync("rest/api/3/myself");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sequence.Calls);
    }

    [Fact]
    public async Task Anything_that_is_not_a_429_goes_straight_through()
    {
        var sequence = new Sequence(new HttpResponseMessage(HttpStatusCode.BadRequest));

        using var client = ClientOver(sequence);

        var response = await client.GetAsync("rest/api/3/issue");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, sequence.Calls);
    }

    [Fact]
    public async Task It_gives_up_rather_than_retrying_forever()
    {
        // A tenant that is limiting hard should surface as a reported failure
        // on that ticket, not as a request that never returns.
        var sequence = new Sequence(
            TooManyRequests(1), TooManyRequests(1), TooManyRequests(1), TooManyRequests(1));

        using var client = ClientOver(sequence);

        var response = await client.GetAsync("rest/api/3/myself");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(4, sequence.Calls);
    }
}
