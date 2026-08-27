using System.Text;

namespace TicketCloner.Api.Atlassian;

/// <summary>
/// One tenant's credential. Atlassian Cloud takes an account email and an API
/// token as HTTP Basic.
/// </summary>
public sealed record AtlassianCredentials(string Email, string ApiToken)
{
    public static readonly AtlassianCredentials None = new("", "");

    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(ApiToken);

    /// <summary>The base64 half of an <c>Authorization: Basic</c> header.</summary>
    public string ToBasicParameter() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{ApiToken}"));

    /// <summary>Safe to log. Never render the token itself.</summary>
    public override string ToString() =>
        IsPresent ? $"{Email} (token present)" : "(no credentials)";
}
