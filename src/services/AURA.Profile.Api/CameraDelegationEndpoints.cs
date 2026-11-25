using AURA.Profile.Api.Models;
using Npgsql;

namespace AURA.Profile.Api;

public static class CameraDelegationEndpoints
{
    public static void MapCameraDelegation(this WebApplication app)
    {
        app.MapPost("/internal/v1/profile/camera-delegation/resolve", async Task<IResult> (
            HttpRequest request, CameraDelegationResolveRequest body, ProfileRepository repository,
            ServiceTokenValidator validator, CancellationToken ct) =>
        {
            var headers = request.Headers.Authorization;
            if (headers.Count != 1 || !System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(headers[0], out var bearer)
                || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();
            var (valid, principal, code, _) = validator.ValidateToken(bearer.Parameter ?? "", "aura-profile", "profile:camera");
            if (!valid) return Results.StatusCode(code == "ERR_FORBIDDEN" ? 403 : 401);

            var clientId = principal!.FindFirst("client_id")!.Value;
            var principalId = Guid.Parse(principal.FindFirst("sub")!.Value);
            if (body.ServicePrincipalId != Guid.Empty && body.ServicePrincipalId != principalId ||
                !string.IsNullOrEmpty(body.ClientId) && body.ClientId != clientId)
                return Results.StatusCode(403);
            try
            {
                return Results.Ok(await repository.ResolveCameraDelegationAsync(
                    body with {ServicePrincipalId = principalId, ClientId = clientId}, ct));
            }
            catch (Exception e) when (!ct.IsCancellationRequested &&
                e is HttpRequestException or IOException or System.Text.Json.JsonException or NpgsqlException or TimeoutException or OperationCanceledException)
            {
                return Results.StatusCode(503);
            }
        }).WithName("ResolveCameraDelegation").WithTags("Internal");
    }
}
