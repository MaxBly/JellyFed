namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Response returned by <c>POST /JellyFed/v1/peer/register</c>.
/// </summary>
public class RegisterPeerResponseDto
{
    /// <summary>Gets or sets the registration status ("ok" or "blocked").</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets a human-readable message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the access token issued by this instance to the registering peer.
    /// The peer must store this and use it as Bearer token for all subsequent API calls
    /// to this instance. Null when status is not "ok".
    /// </summary>
    public string? AccessToken { get; set; }
}
