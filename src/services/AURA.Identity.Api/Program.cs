using System.Net.Http.Headers;
using System.Text.Json;
using AURA.Identity.Api;
using AURA.Identity.Api.Models;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var postgresConnString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=127.0.0.1;Port=5432;Database=aura_dev;Username=aura_identity_app;Password=aura_identity_pass;";

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(postgresConnString));
builder.Services.AddSingleton<IdentityRepository>();
builder.Services.AddSingleton<ServiceTokenIssuer>();
builder.Services.AddSingleton<FirebaseAuthProvider>();

builder.Services.AddHttpClient("ProfileClient", (sp, client) =>
{
    var baseUrl = builder.Configuration["Services:Profile:BaseUrl"];
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        baseUrl = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            || File.Exists("/.dockerenv")
            ? "http://profile-api:8080"
            : "http://127.0.0.1:5005";
    }
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

// Validate provisioned service-auth material before health or business routes start.
_ = app.Services.GetRequiredService<ServiceTokenIssuer>();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Correlation ID & Security Headers Middleware
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

// Helper to extract Bearer token
static string? ExtractBearerToken(HttpRequest request)
{
    var authHeader = request.Headers.Authorization.FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return authHeader["Bearer ".Length..].Trim();
    }
    return null;
}

// Helper to verify Firebase Token
static async Task<(bool Success, string? Uid, string? Email, string? Name, string? ErrorCode, bool EmailVerified)> VerifyFirebaseTokenAsync(
    string token, FirebaseAuth? auth, IWebHostEnvironment env)
{
    // Support simulated development tokens for testing
    if (env.IsDevelopment() && Environment.GetEnvironmentVariable("AURA_ALLOW_MOCK_IDENTITY") == "true" && token.StartsWith("mock-dev-token-", StringComparison.OrdinalIgnoreCase))
    {
        var mockUid = token["mock-dev-token-".Length..];
        return (true, mockUid, $"{mockUid}@example.com", "Mock User", null, false);
    }
    if (env.IsDevelopment() && Environment.GetEnvironmentVariable("AURA_ALLOW_MOCK_IDENTITY") == "true" && token.StartsWith("mock-token-", StringComparison.OrdinalIgnoreCase))
    {
        var mockUid = token["mock-token-".Length..];
        return (true, mockUid, $"{mockUid}@example.com", "Mock User", null, false);
    }

    if (auth == null)
    {
        return (false, null, null, null, "ERR_UNAUTHORIZED", false);
    }

    try
    {
        var decoded = await auth.VerifyIdTokenAsync(token, checkRevoked: true);
        var email = decoded.Claims.TryGetValue("email", out var e) ? e?.ToString() : null;
        var name = decoded.Claims.TryGetValue("name", out var n) ? n?.ToString() : null;
        var verified = decoded.Claims.TryGetValue("email_verified", out var v) && v is bool b && b;
        return (true, decoded.Uid, email, name, null, verified);
    }
    catch (FirebaseAuthException)
    {
        return (false, null, null, null, "ERR_UNAUTHORIZED", false);
    }
    catch
    {
        return (false, null, null, null, "ERR_UNAUTHORIZED", false);
    }
}

// 1. POST /api/v1/identity/bootstrap
app.MapPost("/api/v1/identity/bootstrap", async (
    HttpRequest request,
    IdentityRepository repository,
    ServiceTokenIssuer issuer,
    FirebaseAuthProvider authProvider,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;
    var token = ExtractBearerToken(request);
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_UNAUTHORIZED", "Thiếu Firebase ID token.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var (success, uid, email, name, errorCode, _) = await VerifyFirebaseTokenAsync(token, authProvider.Auth, env);
    if (!success || string.IsNullOrWhiteSpace(uid))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errorCode ?? "ERR_UNAUTHORIZED", "Firebase token không hợp lệ hoặc đã bị thu hồi.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    // Idempotent account lookup or creation
    var account = await repository.GetAccountByFirebaseUidAsync(uid);
    if (account == null)
    {
        account = await repository.CreateAccountAsync(uid);
    }

    if (account.AccountStatus != "ACTIVE")
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_ACCOUNT_DISABLED", "Tài khoản đã bị khóa.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    // Call Profile service to provision profile/personal tenant
    if (account.ProfileProvisioningStatus != "READY")
    {
        try
        {
            var profileClient = httpClientFactory.CreateClient("ProfileClient");
            var provisionPayload = new ProvisionProfileRequest(
                account.AccountId,
                account.FirebaseUid,
                !string.IsNullOrWhiteSpace(name) ? name : "Người dùng AURA",
                email);

            var profileClientSecret = builder.Configuration["ServiceAuth:ClientSecret"];
            if (string.IsNullOrWhiteSpace(profileClientSecret))
            {
                throw new InvalidOperationException("ServiceAuth:ClientSecret is not configured.");
            }

            var serviceTokenResult = issuer.IssueToken(
                "identity-api",
                profileClientSecret,
                "aura-profile",
                "profile:provision");
            var serviceAccessToken = serviceTokenResult.AccessToken;
            if (!serviceTokenResult.Success || string.IsNullOrWhiteSpace(serviceAccessToken))
            {
                throw new InvalidOperationException(
                    "Could not issue service token for Profile provisioning. Error code: " +
                    (serviceTokenResult.ErrorCode ?? "ERR_UNAUTHORIZED"));
            }
            var requestMsg = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/provision")
            {
                Content = JsonContent.Create(provisionPayload)
            };
            requestMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceAccessToken);
            requestMsg.Headers.Add("X-Correlation-Id", correlationId);

            var profileResponse = await profileClient.SendAsync(requestMsg);
            if (profileResponse.IsSuccessStatusCode)
            {
                await repository.UpdateProvisioningStatusAsync(account.AccountId, "READY");
                account = account with { ProfileProvisioningStatus = "READY" };
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Profile provisioning call failed for account {AccountId}, will keep PENDING for retry.", account.AccountId);
        }
    }

    return Results.Ok(account);
})
.WithName("Bootstrap")
.WithTags("Identity");

// 2. GET /api/v1/identity/me
app.MapGet("/api/v1/identity/me", async (
    HttpRequest request,
    IdentityRepository repository,
    FirebaseAuthProvider authProvider,
    IWebHostEnvironment env) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;
    var token = ExtractBearerToken(request);
    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_UNAUTHORIZED", "Thiếu Firebase ID token.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var (success, uid, email, _, errorCode, emailVerified) = await VerifyFirebaseTokenAsync(token, authProvider.Auth, env);
    if (!success || string.IsNullOrWhiteSpace(uid))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errorCode ?? "ERR_UNAUTHORIZED", "Firebase token không hợp lệ hoặc đã bị thu hồi.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var account = await repository.GetAccountByFirebaseUidAsync(uid);
    if (account == null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_ACCOUNT_NOT_INITIALIZED", "Tài khoản chưa được khởi tạo. Hãy gọi bootstrap trước.", correlationId, true)),
            statusCode: StatusCodes.Status409Conflict);
    }

    if (account.AccountStatus != "ACTIVE")
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_ACCOUNT_DISABLED", "Tài khoản đã bị khóa.", correlationId, false)),
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(account);
})
.WithName("GetIdentityMe")
.WithTags("Identity");

// 3. POST /internal/v1/identity/service-tokens
app.MapPost("/internal/v1/identity/service-tokens", (
    ServiceTokenRequest request,
    ServiceTokenIssuer issuer) =>
{
    var correlationId = Guid.NewGuid().ToString("D");
    if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Thiếu clientId hoặc clientSecret.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var result = issuer.IssueToken(request.ClientId, request.ClientSecret, request.Audience, request.Scope);
    if (!result.Success)
    {
        var status = result.ErrorCode switch
        {
            "ERR_FORBIDDEN" => StatusCodes.Status403Forbidden,
            "ERR_VALIDATION" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status401Unauthorized
        };

        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(result.ErrorCode ?? "ERR_UNAUTHORIZED", result.ErrorMessage ?? "Xác thực dịch vụ thất bại.", correlationId, false)),
            statusCode: status);
    }

    return Results.Ok(new ServiceTokenResponse(result.AccessToken!, "Bearer", result.ExpiresIn));
})
.WithName("IssueServiceToken")
.WithTags("Internal");

// 4. POST /internal/v1/identity/context
app.MapPost("/internal/v1/identity/context", async (
    HttpRequest request,
    IdentityRepository repository,
    ServiceTokenIssuer issuer,
    FirebaseAuthProvider authProvider,
    IWebHostEnvironment env) =>
{
    var correlationId = (string)request.HttpContext.Items["CorrelationId"]!;

    // 1. Kiểm tra Service JWT bắt buộc tại header Authorization
    var serviceAuth = request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(serviceAuth) || !serviceAuth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_UNAUTHORIZED", "Thiếu service token xác thực tại header Authorization.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var serviceToken = serviceAuth["Bearer ".Length..].Trim();
    var (serviceValid, _, sErrCode, sErrMsg) = issuer.ValidateServiceToken(serviceToken, "aura-identity", "identity:context");
    if (!serviceValid)
    {
        var status = sErrCode == "ERR_FORBIDDEN" ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(sErrCode ?? "ERR_UNAUTHORIZED", sErrMsg ?? "Service token không hợp lệ.", correlationId, false)),
            statusCode: status);
    }

    // 2. Kiểm tra User token bắt buộc tại header X-User-Authorization
    var userAuth = request.Headers["X-User-Authorization"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(userAuth))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_VALIDATION", "Thiếu User token trong header X-User-Authorization.", correlationId, false)),
            statusCode: StatusCodes.Status400BadRequest);
    }

    var token = userAuth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? userAuth["Bearer ".Length..].Trim()
        : userAuth.Trim();

    var (success, uid, email, _, errorCode, emailVerified) = await VerifyFirebaseTokenAsync(token, authProvider.Auth, env);
    if (!success || string.IsNullOrWhiteSpace(uid))
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails(errorCode ?? "ERR_UNAUTHORIZED", "Token người dùng không hợp lệ hoặc đã bị thu hồi.", correlationId, false)),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var account = await repository.GetAccountByFirebaseUidAsync(uid);
    if (account == null)
    {
        return Results.Json(
            new ErrorEnvelope(new ErrorDetails("ERR_NOT_FOUND", "Không tìm thấy tài khoản người dùng tương ứng.", correlationId, false)),
            statusCode: StatusCodes.Status404NotFound);
    }

    return Results.Ok(new IdentityContextResponse(account.AccountId, account.AccountStatus, account.Roles, email, emailVerified));
})
.WithName("ResolveIdentityContext")
.WithTags("Internal");

// 5. GET /internal/v1/identity/public-key
app.MapGet("/internal/v1/identity/public-key", (ServiceTokenIssuer issuer) =>
{
    return Results.Text(issuer.GetPublicKeyPem(), "application/x-pem-file");
})
.WithName("GetIdentityPublicKey")
.WithTags("Internal");


app.MapGet("/internal/v1/identity/accounts/{accountId:guid}/context", async Task<IResult> (Guid accountId, HttpRequest request, IdentityRepository repository, ServiceTokenIssuer issuer) =>
{
    var token=ExtractBearerToken(request);
    var (valid,_,code,message)=issuer.ValidateServiceToken(token ?? "", "aura-identity", "identity:context");
    if(!valid)return Results.Json(new {error=new {code,message}},statusCode:code=="ERR_FORBIDDEN"?403:401);
    var account=await repository.GetAccountByIdAsync(accountId);
    return account is null ? Results.NotFound() : Results.Ok(new IdentityContextResponse(account.AccountId,account.AccountStatus,account.Roles));
});
app.MapPut("/api/v1/admin/accounts/{accountId:guid}/doctor-capability", async Task<IResult> (Guid accountId, DoctorCapabilityRequest body, HttpRequest request, IdentityRepository repository, FirebaseAuthProvider authProvider, IWebHostEnvironment env) =>
{
    var (valid,uid,_,_,_,_)=await VerifyFirebaseTokenAsync(ExtractBearerToken(request) ?? "",authProvider.Auth,env);
    if(!valid || uid is null)return Results.Unauthorized();
    var actor=await repository.GetAccountByFirebaseUidAsync(uid);
    if(actor?.AccountStatus!="ACTIVE" || !actor.Roles.Contains("SYSTEM_ADMIN",StringComparer.Ordinal))return Results.StatusCode(403);
    if(string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length>1000)return Results.BadRequest();
    return await repository.SetDoctorCapabilityAsync(actor.AccountId,accountId,body.Approved,body.Reason.Trim()) ? Results.NoContent() : Results.NotFound();
});

app.Run();

public class FirebaseAuthProvider
{
    public FirebaseAuth? Auth { get; }

    public FirebaseAuthProvider(IConfiguration config)
    {
        var projectId = config["Firebase:ProjectId"] ?? "aura-dev-97716";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
        {
            var defaultCredPath = config["Firebase:CredentialPath"]
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".aura", "credentials", "aura-dev-service-account.json");
            if (File.Exists(defaultCredPath))
            {
                Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", defaultCredPath);
            }
        }
        try
        {
            var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
            {
                ProjectId = projectId,
                Credential = GoogleCredential.GetApplicationDefault()
            }, "aura-identity");
            Auth = FirebaseAuth.GetAuth(app);
        }
        catch
        {
            Auth = null;
        }
    }
}

public sealed record DoctorCapabilityRequest(bool Approved, string Reason);
