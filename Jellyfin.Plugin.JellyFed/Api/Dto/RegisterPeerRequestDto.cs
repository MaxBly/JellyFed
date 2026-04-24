namespace Jellyfin.Plugin.JellyFed.Api.Dto;

/// <summary>
/// Request body for POST /JellyFed/v1/peer/register.
/// Sent by a remote instance that wants to federate with this one.
/// </summary>
public class RegisterPeerRequestDto
{
    /// <summary>Gets or sets the display name of the requesting instance.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL of the requesting instance.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the federation token to use when calling back to that instance.</summary>
    public string FederationToken { get; set; } = string.Empty;
}
