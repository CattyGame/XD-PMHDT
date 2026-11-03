using AURA.Profile.Api;
using AURA.Profile.Api.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var postgresConnString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=127.0.0.1;Port=5432;Database=aura_dev;Username=aura_profile_app;Password=aura_profile_pass;";

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(postgresConnString));
builder.Services.AddSingleton<ProfileRepository>();
builder.Services.AddSingleton<UserIdentityResolver>();

builder.Services.AddHttpClient("IdentityClient", (sp, client) =>
{
    var baseUrl = builder.Configuration["Services:Identity:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        baseUrl = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            || File.Exists("/.dockerenv")
            ? "http://identity-api:8080"
            : "http://127.0.0.1:5004";
    }
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
.RedactLoggedHeaders(new[] { "Authorization", "X-User-Authorization" });

builder.Services.AddSingleton<ServiceTokenValidator>();

var app = builder.Build();

// Validate provisioned service-auth material before health or business routes start.
_ = app.Services.GetRequiredService<ServiceTokenValidator>();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Correlation ID Middleware
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 64)
    {
        correlationId = Guid.NewGuid().ToString("D");
    }
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    context.Items["CorrelationId"] = correlationId;

    await next();
});

app.MapHealthChecks("/health");

// 1. POST /internal/v1/profile/provision (Bảo vệ bằng Service JWT: scope profile:provision)
app.MapPost("/internal/v1/profile/provision", async (
    HttpRequest request,
    ProvisionProfileRequest body,
    ProfileRepository repository,
    ServiceTokenValidator tokenValidator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;

    // 1.1. Xác thực Service JWT
    var authHeader = request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_UNAUTHORIZED", "Thiếu service token xác thực.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = authHeader["Bearer ".Length..].Trim();
    var (valid, _, errCode, errMsg) = await tokenValidator.ValidateTokenAsync(token, "aura-profile", "profile:provision");
    if (!valid)
    {
        var statusCode = errCode == "ERR_FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_UNAUTHORIZED", errMsg ?? "Service token không hợp lệ.", correlationId, false)),
            statusCode: statusCode);
    }

    try
    {
        if (body.AccountId == Guid.Empty)
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Thiếu accountId hợp lệ.", correlationId, false)),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var profile = await repository.ProvisionProfileAsync(body.AccountId, body.DisplayName, body.Email, ct);
        return Results.Ok(profile);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[{CorrelationId}] Lỗi khi khởi tạo hồ sơ", correlationId);
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_INTERNAL", "Lỗi xử lý nội bộ khi khởi tạo hồ sơ.", correlationId, true)),
            statusCode: StatusCodes.Status500InternalServerError);
    }
})
.WithName("ProvisionProfile")
.WithTags("Internal");

// 2. GET /api/v1/profiles/me (Bảo vệ bằng User Bearer Token qua Identity Context)
app.MapGet("/api/v1/profiles/me", async (
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

    var profile = await repository.GetProfileByAccountIdAsync(identity.AccountId, ct);
    if (profile == null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_PROFILE_NOT_READY", "Tài khoản chưa hoàn tất bootstrap hồ sơ.", correlationId, true)),
            statusCode: StatusCodes.Status409Conflict);
    }

    if (!string.IsNullOrWhiteSpace(profile.Email))
    {
        await repository.AutoActivatePendingMembershipsAsync(identity.AccountId, profile.Email, ct);
    }

    request.HttpContext.Response.Headers.ETag = $"\"{profile.ProfileVersion}\"";
    return Results.Ok(profile);
})
.WithName("GetMyProfile")
.WithTags("Profiles");

// 3. PATCH /api/v1/profiles/me (Cập nhật DisplayName, Phone kèm If-Match ETag)
app.MapPatch("/api/v1/profiles/me", async (
    HttpRequest request,
    UpdateProfileRequest body,
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

    var ifMatch = request.Headers.IfMatch.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(ifMatch))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Bắt buộc cung cấp Header If-Match chứa ETag hiện tại.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var cleanIfMatch = ifMatch.Trim('\"', ' ');
    if (!int.TryParse(cleanIfMatch, out var expectedVersion))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Header If-Match không đúng định dạng số phiên bản.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(body.DisplayName))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "DisplayName không được để trống.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var (success, updated, conflict) = await repository.UpdateProfileAsync(
        identity.AccountId, body.DisplayName.Trim(), body.Phone?.Trim(), expectedVersion, ct);

    if (conflict)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_PROFILE_VERSION_CONFLICT", "Hồ sơ đã bị thay đổi bởi một phiên khác. Vui lòng làm mới.", correlationId, false)),
            statusCode: StatusCodes.Status409Conflict);
    }

    if (!success || updated == null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_PROFILE_NOT_FOUND", "Không tìm thấy hồ sơ cá nhân tương ứng.", correlationId, false)),
            statusCode: StatusCodes.Status404NotFound);
    }

    request.HttpContext.Response.Headers.ETag = $"\"{updated.ProfileVersion}\"";
    return Results.Ok(updated);
})
.WithName("UpdateMyProfile")
.WithTags("Profiles");

// 4. POST /internal/v1/profile/access/resolve (Bảo vệ bằng Service JWT: scope profile:resolve)
app.MapPost("/internal/v1/profile/access/resolve", async (
    HttpRequest request,
    AccessResolveRequest body,
    ProfileRepository repository,
    ServiceTokenValidator tokenValidator,
    CancellationToken ct) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;

    var authHeader = request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_UNAUTHORIZED", "Thiếu service token xác thực.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = authHeader["Bearer ".Length..].Trim();
    var (valid, _, errCode, errMsg) = await tokenValidator.ValidateTokenAsync(token, "aura-profile", "profile:resolve");
    if (!valid)
    {
        var statusCode = errCode == "ERR_FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_UNAUTHORIZED", errMsg ?? "Service token không hợp lệ.", correlationId, false)),
            statusCode: statusCode);
    }

    var response = await repository.ResolveAccessAsync(
        body.ActorAccountId, body.PatientId, body.ClinicContextId, body.Action, body.AnalysisId, ct);

    return Results.Ok(response);
})
.WithName("ResolveAccess")
.WithTags("Internal");

// 5. POST /internal/v1/profile/camera-delegation/resolve (Bảo vệ bằng Service JWT: scope profile:resolve)
app.MapPost("/internal/v1/profile/camera-delegation/resolve", async (
    HttpRequest request,
    CameraDelegationResolveRequest body,
    ProfileRepository repository,
    ServiceTokenValidator tokenValidator,
    CancellationToken ct) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;

    var authHeader = request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_UNAUTHORIZED", "Thiếu service token xác thực.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = authHeader["Bearer ".Length..].Trim();
    var (valid, _, errCode, errMsg) = await tokenValidator.ValidateTokenAsync(token, "aura-profile", "profile:resolve");
    if (!valid)
    {
        var statusCode = errCode == "ERR_FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_UNAUTHORIZED", errMsg ?? "Service token không hợp lệ.", correlationId, false)),
            statusCode: statusCode);
    }

    var response = await repository.ResolveCameraDelegationAsync(body, ct);
    return Results.Ok(response);
})
.WithName("ResolveCameraDelegation")
.WithTags("Internal");

// --- CLINIC LIFECYCLE & MEMBERSHIP ENDPOINTS (Task W4-M6-01 / SCRUM-358) ---

// POST /api/v1/clinics (Đăng ký phòng khám mới)
app.MapPost("/api/v1/clinics", async (
    RegisterClinicRequest body,
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

    if (string.IsNullOrWhiteSpace(body.Name))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Tên phòng khám không được để trống.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var clinic = await repository.RegisterClinicAsync(identity.AccountId, body, ct);
    return Results.Created($"/api/v1/clinics/{clinic.ClinicId}", clinic);
})
.WithName("RegisterClinic")
.WithTags("Clinics");

// PATCH /api/v1/admin/clinics/{clinicId:guid}/status (Admin xét duyệt trạng thái phòng khám)
app.MapPatch("/api/v1/admin/clinics/{clinicId:guid}/status", async (
    Guid clinicId,
    UpdateClinicStatusRequest body,
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

    if (!identity.Roles.Contains("SYSTEM_ADMIN", StringComparer.OrdinalIgnoreCase))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Chỉ Quản trị viên hệ thống mới có quyền xét duyệt phòng khám.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var normalizedStatus = body.Status?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(normalizedStatus) || !ClinicStatuses.AllowedStatuses.Contains(normalizedStatus))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_INVALID_STATUS", "Trạng thái phòng khám không hợp lệ.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var updatedClinic = await repository.UpdateClinicStatusAsync(identity.AccountId, clinicId, normalizedStatus, body.Reason?.Trim(), ct);
    if (updatedClinic == null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
            statusCode: StatusCodes.Status404NotFound);
    }

    return Results.Ok(updatedClinic);
})
.WithName("UpdateClinicStatus")
.WithTags("Admin");

// POST /api/v1/clinics/{clinicId:guid}/memberships/invite (Mời hội viên qua email)
app.MapPost("/api/v1/clinics/{clinicId:guid}/memberships/invite", async (
    Guid clinicId,
    InviteClinicMemberRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý thành viên phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@'))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Email không đúng định dạng.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var normalizedRole = ClinicMemberRoles.NormalizeRole(body.Role);
    if (normalizedRole == null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_INVALID_ROLE", "Vai trò thành viên không hợp lệ.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var (result, errorCode) = await repository.InviteClinicMemberAsync(clinicId, body.Email, normalizedRole, ct);
    if (errorCode == "ERR_MEMBERSHIP_ALREADY_EXISTS")
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errorCode, "Thành viên này đã tham gia phòng khám.", correlationId, false)),
            statusCode: StatusCodes.Status409Conflict);
    }

    return Results.Created($"/api/v1/clinics/{clinicId}/memberships", result);
})
.WithName("InviteClinicMember")
.WithTags("Clinics");

// 5b. POST /api/v1/clinics/invitations/{membershipId:guid}/accept (Chấp nhận lời mời phòng khám - ADR-005 D2)
app.MapPost("/api/v1/clinics/invitations/{membershipId:guid}/accept", async (
    Guid membershipId,
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

    var profile = await repository.GetProfileByAccountIdAsync(identity.AccountId, ct);
    if (profile == null || string.IsNullOrWhiteSpace(profile.Email))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_PROFILE_NOT_READY", "Hồ sơ chưa có thông tin email xác thực.", correlationId, false)),
            statusCode: StatusCodes.Status409Conflict);
    }

    var (succeeded, errorCode, member) = await repository.AcceptClinicInvitationExplicitAsync(
        identity.AccountId, profile.Email, membershipId, ct);

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


// 6. GET /api/v1/clinics/{clinicId:guid}/memberships
app.MapGet("/api/v1/clinics/{clinicId:guid}/memberships", async (
    Guid clinicId,
    HttpRequest request,
    ProfileRepository repository,
    UserIdentityResolver identityResolver,
    CancellationToken ct,
    string? status = null) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;
    var identity = await identityResolver.ResolveAsync(request, ct);
    if (!identity.Succeeded)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(identity.Code, identity.Message, correlationId, identity.Retryable)),
            statusCode: identity.StatusCode);
    }

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền truy cập thông tin phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var items = await repository.GetClinicMembersAsync(clinicId, status, ct);
    return Results.Ok(new ClinicMemberListResponse(clinicId, items));
})
.WithName("GetClinicMemberships")
.WithTags("Clinics");

// 7. POST /api/v1/clinics/{clinicId:guid}/memberships
app.MapPost("/api/v1/clinics/{clinicId:guid}/memberships", async (
    Guid clinicId,
    AddClinicMemberRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý thành viên phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (body.AccountId == Guid.Empty)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Thiếu accountId hợp lệ.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(body.Role) || !ClinicMemberRoles.AllowedRoles.Contains(body.Role.Trim()))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_INVALID_ROLE", "Vai trò thành viên không hợp lệ. Chỉ chấp nhận CLINIC_ADMIN, DOCTOR, STAFF.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var targetRole = body.Role.Trim().ToUpperInvariant();
    var (success, member, errCode, errMsg) = await repository.AddOrReactivateClinicMemberAsync(
        clinicId, body.AccountId, targetRole, ct);

    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_MEMBERSHIP_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
            "ERR_ACCOUNT_NOT_FOUND" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể thêm thành viên.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.Json(member, statusCode: StatusCodes.Status201Created);
})
.WithName("AddClinicMembership")
.WithTags("Clinics");

// 8. DELETE /api/v1/clinics/{clinicId:guid}/memberships/{accountId:guid}
app.MapDelete("/api/v1/clinics/{clinicId:guid}/memberships/{accountId:guid}", async (
    Guid clinicId,
    Guid accountId,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý thành viên phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (success, errCode, errMsg) = await repository.RevokeClinicMemberAsync(clinicId, accountId, ct);
    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_CANNOT_REVOKE_SOLE_ADMIN" => StatusCodes.Status400BadRequest,
            "ERR_MEMBERSHIP_NOT_FOUND" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể thu hồi thành viên.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.NoContent();
})
.WithName("RevokeClinicMembership")
.WithTags("Clinics");

// 9. POST /api/v1/clinics/{clinicId:guid}/assignments (Phân công Bác sĩ phụ trách bệnh nhân)
app.MapPost("/api/v1/clinics/{clinicId:guid}/assignments", async (
    Guid clinicId,
    CreateDoctorAssignmentRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý phân công phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (body.DoctorId == Guid.Empty || body.PatientId == Guid.Empty)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Thiếu doctorId hoặc patientId hợp lệ.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var (success, dto, errCode, errMsg) = await repository.CreateOrReactivateDoctorAssignmentAsync(
        clinicId, body.DoctorId, body.PatientId, body.AnalysisId, body.Notes, null, ct);

    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_ASSIGNMENT_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
            "ERR_CLINIC_NOT_FOUND" => StatusCodes.Status404NotFound,
            "ERR_PATIENT_NOT_IN_CLINIC" => StatusCodes.Status400BadRequest,
            "ERR_INVALID_DOCTOR" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể phân công bác sĩ.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.Ok(dto);
})
.WithName("CreateDoctorAssignment")
.WithTags("DoctorAssignments");

// 10. GET /api/v1/clinics/{clinicId:guid}/assignments (Xem danh sách phân công bác sĩ)
app.MapGet("/api/v1/clinics/{clinicId:guid}/assignments", async (
    Guid clinicId,
    HttpRequest request,
    ProfileRepository repository,
    UserIdentityResolver identityResolver,
    CancellationToken ct,
    Guid? doctorId = null,
    Guid? patientId = null,
    string? status = null) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;
    var identity = await identityResolver.ResolveAsync(request, ct);
    if (!identity.Succeeded)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(identity.Code, identity.Message, correlationId, identity.Retryable)),
            statusCode: identity.StatusCode);
    }

    var (success, items, errCode, errMsg) = await repository.GetDoctorAssignmentsAsync(
        clinicId, identity.AccountId, identity.Roles, doctorId, patientId, status, ct);

    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_CLINIC_NOT_FOUND" => StatusCodes.Status404NotFound,
            "ERR_FORBIDDEN_DOCTOR_ACCESS" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể tải danh sách phân công.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.Ok(new DoctorAssignmentListResponse(clinicId, items!));
})
.WithName("GetDoctorAssignments")
.WithTags("DoctorAssignments");

// 11. DELETE /api/v1/clinics/{clinicId:guid}/assignments/{assignmentId:guid} (Thu hồi phân công)
app.MapDelete("/api/v1/clinics/{clinicId:guid}/assignments/{assignmentId:guid}", async (
    Guid clinicId,
    Guid assignmentId,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý phân công phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (success, errCode, errMsg) = await repository.RevokeDoctorAssignmentAsync(clinicId, assignmentId, ct);
    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_ASSIGNMENT_NOT_FOUND" => StatusCodes.Status404NotFound,
            "ERR_ASSIGNMENT_NOT_ACTIVE" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể thu hồi phân công.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.NoContent();
})
.WithName("RevokeDoctorAssignment")
.WithTags("DoctorAssignments");


// 12. POST /api/v1/clinics/{clinicId:guid}/assignments/batch (Phân công theo lô / nhóm bệnh nhân)
app.MapPost("/api/v1/clinics/{clinicId:guid}/assignments/batch", async (
    Guid clinicId,
    BatchAssignDoctorRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý phân công phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (body.DoctorId == Guid.Empty || body.PatientIds == null || body.PatientIds.Count == 0)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Dữ liệu phân công theo nhóm không hợp lệ.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var (success, assignments, errCode, errMsg) = await repository.BatchAssignDoctorAsync(
        clinicId, body.DoctorId, body.PatientIds, body.Notes, ct);

    if (!success)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể phân công nhóm bệnh nhân.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Ok(new DoctorAssignmentListResponse(clinicId, assignments!));
})
.WithName("BatchAssignDoctor")
.WithTags("DoctorAssignments");

// 13. POST /api/v1/clinics/{clinicId:guid}/assignments/{assignmentId:guid}/transfer (Chuyển giao ca bệnh cho Bác sĩ khác)
app.MapPost("/api/v1/clinics/{clinicId:guid}/assignments/{assignmentId:guid}/transfer", async (
    Guid clinicId,
    Guid assignmentId,
    TransferDoctorAssignmentRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicManagementAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền chuyển giao phân công phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (body.NewDoctorId == Guid.Empty)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Thiếu newDoctorId hợp lệ.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var (success, dto, errCode, errMsg) = await repository.TransferDoctorAssignmentAsync(
        clinicId, assignmentId, body.NewDoctorId, body.Notes, ct);

    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_ASSIGNMENT_NOT_FOUND" => StatusCodes.Status404NotFound,
            "ERR_ASSIGNMENT_NOT_ACTIVE" => StatusCodes.Status400BadRequest,
            "ERR_SAME_DOCTOR" => StatusCodes.Status400BadRequest,
            "ERR_INVALID_DOCTOR" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể chuyển giao phân công.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.Ok(dto);
})
.WithName("TransferDoctorAssignment")
.WithTags("DoctorAssignments");

// 14. POST /api/v1/clinics/{clinicId:guid}/patients (Đăng ký bệnh nhân phòng khám mới)
app.MapPost("/api/v1/clinics/{clinicId:guid}/patients", async (
    Guid clinicId,
    RegisterClinicPatientRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicAccessAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền quản lý bệnh nhân tại phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(body.FullName))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Họ và tên bệnh nhân không được để trống.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var (success, patient, errCode, errMsg) = await repository.RegisterClinicPatientAsync(clinicId, body, ct);
    if (!success)
    {
        var statusCode = errCode switch
        {
            "ERR_CLINIC_NOT_FOUND" => StatusCodes.Status404NotFound,
            "ERR_VALIDATION" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể lưu bệnh nhân.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.Created($"/api/v1/clinics/{clinicId}/patients/{patient!.PatientId}", patient);
})
.WithName("RegisterClinicPatient")
.WithTags("ClinicPatients");

// 15. GET /api/v1/clinics/{clinicId:guid}/patients (Lấy danh sách bệnh nhân của phòng khám)
app.MapGet("/api/v1/clinics/{clinicId:guid}/patients", async (
    Guid clinicId,
    HttpRequest request,
    ProfileRepository repository,
    UserIdentityResolver identityResolver,
    CancellationToken ct,
    string? search = null,
    string? status = null,
    int limit = 20,
    int offset = 0) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;
    var identity = await identityResolver.ResolveAsync(request, ct);
    if (!identity.Succeeded)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(identity.Code, identity.Message, correlationId, identity.Retryable)),
            statusCode: identity.StatusCode);
    }

    var (allowed, failureReason) = await repository.CheckClinicAccessAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền xem danh sách bệnh nhân tại phòng khám này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (success, response, errCode, errMsg) = await repository.GetClinicPatientsAsync(
        clinicId, search, status, limit, offset, ct);

    if (!success)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể tải danh sách bệnh nhân.", correlationId, false)),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Ok(response);
})
.WithName("GetClinicPatients")
.WithTags("ClinicPatients");

// 16. GET /api/v1/clinics/{clinicId:guid}/patients/{patientId:guid} (Xem chi tiết bệnh nhân)
app.MapGet("/api/v1/clinics/{clinicId:guid}/patients/{patientId:guid}", async (
    Guid clinicId,
    Guid patientId,
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

    var (allowed, failureReason) = await repository.CheckClinicAccessAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền xem thông tin bệnh nhân này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (success, patient, errCode, errMsg) = await repository.GetClinicPatientByIdAsync(clinicId, patientId, ct);
    if (!success)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_PATIENT_NOT_FOUND", errMsg ?? "Không tìm thấy bệnh nhân.", correlationId, false)),
            statusCode: StatusCodes.Status404NotFound);
    }

    return Results.Ok(patient);
})
.WithName("GetClinicPatientById")
.WithTags("ClinicPatients");

// 17. PATCH /api/v1/clinics/{clinicId:guid}/patients/{patientId:guid} (Cập nhật thông tin bệnh nhân)
app.MapPatch("/api/v1/clinics/{clinicId:guid}/patients/{patientId:guid}", async (
    Guid clinicId,
    Guid patientId,
    UpdateClinicPatientRequest body,
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

    var (allowed, failureReason) = await repository.CheckClinicAccessAuthorityAsync(
        identity.AccountId, clinicId, identity.Roles, ct);

    if (!allowed)
    {
        if (failureReason == "CLINIC_NOT_FOUND" || failureReason == "NOT_A_MEMBER")
        {
            return Results.Json(
                new ErrorEnvelope(new ErrorDetails("ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.", correlationId, false)),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_FORBIDDEN", "Bạn không có quyền sửa thông tin bệnh nhân này.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (success, updated, errCode, errMsg) = await repository.UpdateClinicPatientAsync(clinicId, patientId, body, ct);
    if (!success)
    {
        var statusCode = errCode == "ERR_PATIENT_NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể cập nhật hồ sơ bệnh nhân.", correlationId, false)),
            statusCode: statusCode);
    }

    return Results.Ok(updated);
})
.WithName("UpdateClinicPatient")
.WithTags("ClinicPatients");

app.Run();
