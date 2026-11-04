using AURA.Profile.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AURA.Profile.Api;

public static class ProfilePublicMembershipEndpoints
{
    public static void MapProfilePublicMembershipEndpoints(this WebApplication app)
    {
app.MapGet("/api/v1/clinics", async Task<IResult> (HttpRequest request,UserIdentityResolver identities,ProfileRepository repository,CancellationToken ct) =>
{
    var identity=await identities.ResolveAsync(request,ct);
    if(!identity.Succeeded)return Results.Json(new ErrorEnvelope(new ErrorDetails(identity.Code,identity.Message,request.HttpContext.Items["CorrelationId"] as string ?? "",identity.Retryable)),statusCode:identity.StatusCode);
    return Results.Ok(new { items=await repository.ListActorClinicsAsync(identity.AccountId,ct) });
});
app.MapPost("/api/v1/clinics/invitations/{membershipId:guid}/accept", async (
    Guid membershipId,
    AcceptClinicInvitationRequest body,
    HttpRequest request,
    ProfileRepository repository,
    UserIdentityResolver identityResolver,
    CancellationToken ct) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;
    var identity = await identityResolver.ResolveAsync(request, ct);
    if (!identity.Succeeded)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(identity.Code, identity.Message, correlationId, identity.Retryable)),
            statusCode: identity.StatusCode);
    }

    if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
        return Results.Json(new ErrorEnvelope(new ErrorDetails("ERR_EMAIL_UNVERIFIED","A verified Firebase email is required.",correlationId,false)),statusCode:403);
    var (succeeded,errorCode,member)=await repository.AcceptClinicInvitationExplicitAsync(identity.AccountId,identity.Email,membershipId,body.Token,ct);

    if (!succeeded)
    {
        return errorCode switch
        {
            "ERR_INVITATION_NOT_FOUND" => Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_NOT_FOUND", "Không tìm thấy lời mời.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound),
            "ERR_CLINIC_SUSPENDED" => Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Phòng khám đang bị tạm đình chỉ (SUSPENDED).", correlationId, false)),
                statusCode: StatusCodes.Status403Forbidden),
            "ERR_INVITATION_EXPIRED" => Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_INVITATION_EXPIRED", "Lời mời đã hết hạn sau 72 giờ.", correlationId, false)),
                statusCode: StatusCodes.Status410Gone),
            "ERR_EMAIL_MISMATCH" => Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Email tài khoản không khớp với email được mời.", correlationId, false)),
                statusCode: StatusCodes.Status403Forbidden),
            "ERR_ALREADY_ACTIVE" => Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CONFLICT", "Thành viên đã ở trạng thái hoạt động.", correlationId, false)),
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(
                new ErrorEnvelope(new ErrorDetails(errorCode ?? "ERR_INVALID_INVITATION", "Không thể chấp nhận lời mời.", correlationId, false)),
                statusCode: StatusCodes.Status400BadRequest)
        };
    }

    return Results.Ok(member);
})
.WithName("AcceptClinicInvitation")
.WithTags("Clinics");
    }
}
