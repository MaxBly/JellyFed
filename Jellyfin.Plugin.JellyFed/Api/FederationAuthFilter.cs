using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyFed.Api;

/// <summary>
/// Action filter that validates the federation Bearer token.
/// Apply to any endpoint that should only be accessible to registered peers.
/// </summary>
public sealed class FederationAuthFilter : ActionFilterAttribute
{
    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var config = Plugin.Instance?.Configuration;

        if (config is null || string.IsNullOrWhiteSpace(config.FederationToken))
        {
            context.Result = new ObjectResult("Federation token not configured on this instance.")
            {
                StatusCode = 503
            };
            return;
        }

        var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();

        if (!authHeader.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new UnauthorizedObjectResult("Bearer token required.");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();

        if (!string.Equals(token, config.FederationToken, System.StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedObjectResult("Invalid federation token.");
            return;
        }
    }
}
