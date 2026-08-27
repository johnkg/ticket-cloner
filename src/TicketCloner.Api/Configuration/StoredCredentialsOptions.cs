namespace TicketCloner.Api.Configuration;

/// <summary>
/// Credentials read from configuration, used only when the caller supplied
/// none of their own.
///
/// Deliberately NOT ValidateOnStart-validated: empty is a legitimate state.
/// An instance that holds no credentials still serves the UI, and every caller
/// simply has to bring their own headers.
/// </summary>
public sealed class StoredCredentialsOptions
{
    public const string SectionName = "Credentials";

    public string SourceEmail { get; init; } = "";
    public string SourceApiToken { get; init; } = "";
    public string TargetEmail { get; init; } = "";
    public string TargetApiToken { get; init; } = "";
}
