using System.Security.Claims;
using System.Text;
using AURA.Notification.Api;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var projectId = RequiredConfiguration("Firebase:ProjectId");

var databaseSettings = new NpgsqlConnectionStringBuilder
{
    Host = builder.Configuration["NotificationDatabase:Host"]
        ?? "127.0.0.1",
    Port = builder.Configuration.GetValue(
        "NotificationDatabase:Port",
        5432),
    Database = RequiredConfiguration("NotificationDatabase:Database"),
    Username = RequiredConfiguration("NotificationDatabase:Username"),
    Password = RequiredConfiguration("NotificationDatabase:Password"),
    Timeout = 5,
    CommandTimeout = 10,
    IncludeErrorDetail = false,

    // Local: loopback on host or private Docker network.
    // Remote deployments require a separate TLS configuration.
    SslMode = SslMode.Disable
};

builder.Services.AddSingleton<FirebaseApp>(_ =>
{
    var existing = FirebaseApp.GetInstance("aura-notification");
    if (existing != null) return existing;

    var credentialsPath = builder.Configuration["Firebase:CredentialsPath"]
        ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

    GoogleCredential credential;
    if (!string.IsNullOrEmpty(credentialsPath) && File.Exists(credentialsPath))
    {
        credential = GoogleCredential.FromFile(credentialsPath);
    }
    else
    {
        try
        {
            credential = GoogleCredential.GetApplicationDefault();
        }
        catch
        {
            if(!builder.Environment.IsDevelopment() || Environment.GetEnvironmentVariable("AURA_ALLOW_MOCK_NOTIFICATION")!="true")throw;
            credential = GoogleCredential.FromAccessToken("mock-test-token");
        }
    }

    return FirebaseApp.Create(new AppOptions
    {
        ProjectId = projectId,
        Credential = credential
    }, "aura-notification");
});

builder.Services.AddSingleton<FirebaseAuth>(services =>
    FirebaseAuth.GetAuth(services.GetRequiredService<FirebaseApp>()));

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(databaseSettings.ConnectionString));

builder.Services.AddSingleton<TokenCipher>();
builder.Services.AddSingleton<DeviceRegistrationRepository>();
builder.Services.AddSingleton<NotificationInboxRepository>();
builder.Services.AddSingleton<NotificationDeliveryJobRepository>();
builder.Services.AddHttpClient("IdentityAuthority", client=>{client.BaseAddress=new Uri(RequiredConfiguration("Services:Identity:BaseUrl"));client.Timeout=TimeSpan.FromSeconds(5);})
    .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false}).RedactLoggedHeaders(["Authorization"]);
builder.Services.AddHttpClient("AnalysisAuthority", client=>{client.BaseAddress=new Uri(RequiredConfiguration("Services:Analysis:BaseUrl"));client.Timeout=TimeSpan.FromSeconds(5);})
    .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false}).RedactLoggedHeaders(["Authorization"]);
builder.Services.AddHttpClient("BillingAuthority", client=>{client.BaseAddress=new Uri(RequiredConfiguration("Services:Billing:BaseUrl"));client.Timeout=TimeSpan.FromSeconds(5);})
    .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false}).RedactLoggedHeaders(["Authorization"]);
builder.Services.AddSingleton<NotificationAuthorityClient>();
builder.Services.AddSingleton<INotificationContextResolver,NotificationContextResolver>();
builder.Services.AddSingleton<IAccountResolver, AccountResolver>();
builder.Services.AddSingleton<IFcmClient, FirebaseAdminFcmClient>();
builder.Services.Configure<RabbitMqConsumerOptions>(builder.Configuration.GetSection(RabbitMqConsumerOptions.SectionName));
builder.Services.AddHostedService<NotificationEventConsumer>();
builder.Services.AddHostedService<NotificationDeliveryWorker>();
builder.Services.AddNotificationTestPush();

var app = builder.Build();

app.UseExceptionHandler();

_ = app.Services.GetRequiredService<FirebaseAuth>();
_ = app.Services.GetRequiredService<TokenCipher>();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/v1/notifications"))
    {
        await next(context);
        return;
    }

    context.Response.Headers["Cache-Control"] = "no-store";

    var authorization = context.Request.Headers.Authorization.ToString();

    if (!authorization.StartsWith(
        "Bearer ",
        StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["WWW-Authenticate"] = "Bearer";

        await ApiError(
            context,
            401,
            "ERR_UNAUTHORIZED",
            "Cần đăng nhập để sử dụng thông báo.",
            false).ExecuteAsync(context);

        return;
    }

    var idToken = authorization["Bearer ".Length..].Trim();

    if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 16384)
    {
        await ApiError(
            context,
            401,
            "ERR_UNAUTHORIZED",
            "Thông tin xác thực không hợp lệ.",
            false).ExecuteAsync(context);

        return;
    }

    FirebaseToken verifiedToken;

    try
    {
        var auth = context.RequestServices
            .GetRequiredService<FirebaseAuth>();

        verifiedToken = await auth.VerifyIdTokenAsync(
            idToken,
            true,
            context.RequestAborted);
    }
    catch (FirebaseAuthException exception)
    {
        var code = exception.AuthErrorCode.ToString();

        var invalidCredential = code is
            "InvalidIdToken" or
            "ExpiredIdToken" or
            "RevokedIdToken" or
            "UserDisabled" or
            "UserNotFound" or
            "InvalidArgument";

        await ApiError(
            context,
            invalidCredential ? 401 : 503,
            invalidCredential
                ? "ERR_UNAUTHORIZED"
                : "ERR_AUTH_UNAVAILABLE",
            invalidCredential
                ? "Phiên đăng nhập không hợp lệ. Hãy đăng nhập lại."
                : "Dịch vụ xác thực tạm thời không khả dụng.",
            !invalidCredential).ExecuteAsync(context);

        return;
    }
    catch (ArgumentException)
    {
        await ApiError(
            context,
            401,
            "ERR_UNAUTHORIZED",
            "Thông tin xác thực không hợp lệ.",
            false).ExecuteAsync(context);

        return;
    }
    catch (OperationCanceledException)
        when (context.RequestAborted.IsCancellationRequested)
    {
        return;
    }
    catch
    {
        await ApiError(
            context,
            503,
            "ERR_AUTH_UNAVAILABLE",
            "Dịch vụ xác thực tạm thời không khả dụng.",
            true).ExecuteAsync(context);

        return;
    }

    context.User = new ClaimsPrincipal(
        new ClaimsIdentity(
            new[]
            {
                new Claim(
                    ClaimTypes.NameIdentifier,
                    verifiedToken.Uid)
            },
            "Firebase"));

    try
    {
        await next(context);
    }
    catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested)
    {
        return;
    }
    catch(Exception ex) when(ex is HttpRequestException or TimeoutException or OperationCanceledException)
    {
        if(context.Response.HasStarted)throw;
        await ApiError(context,503,"ERR_AUTHORITY_UNAVAILABLE",
            "Dịch vụ xác minh tài khoản tạm thời không khả dụng.",true).ExecuteAsync(context);
    }
});

app.MapHealthChecks("/health");

app.MapGet("/health/ready", async Task<IResult> (
    DeviceRegistrationRepository repository,
    HttpContext context) =>
{
    try
    {
        var ready = await repository.CanConnectAsync(
            context.RequestAborted);

        return ready
            ? Results.Ok(new { ready = true })
            : Results.StatusCode(503);
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(503);
    }
    catch (TimeoutException)
    {
        return Results.StatusCode(503);
    }
});

app.MapGet("/api/v1/notifications/me", (HttpContext context) =>
    Results.Ok(new
    {
        authenticated = true,
        uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
    }));

app.MapPost(
    "/api/v1/notifications/devices",
    async Task<IResult> (
        DeviceRegistrationRequest request,
        DeviceRegistrationRepository repository,
        HttpContext context) =>
    {
        if (
            request.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Token) ||
            Encoding.UTF8.GetByteCount(request.Token) > 4096 ||
            request.Token.Any(char.IsWhiteSpace))
        {
            return ApiError(
                context,
                400,
                "ERR_VALIDATION",
                "DeviceId hoặc FCM token không hợp lệ.",
                false);
        }

        var uid = context.User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(uid))
        {
            return ApiError(
                context,
                401,
                "ERR_UNAUTHORIZED",
                "Cần đăng nhập.",
                false);
        }

        try
        {
            var outcome = await repository.RegisterAsync(
                projectId,
                uid,
                request.DeviceId,
                request.Token,
                context.RequestAborted);

            if (outcome == RegistrationOutcome.Conflict)
            {
                return ApiError(
                    context,
                    409,
                    "ERR_DEVICE_CONFLICT",
                    "Không thể đăng ký thiết bị với thông tin hiện tại.",
                    false);
            }

            return Results.Ok(new
            {
                registered = true,
                deviceId = request.DeviceId
            });
        }
        catch (NpgsqlException)
        {
            return ApiError(
                context,
                503,
                "ERR_STORAGE_UNAVAILABLE",
                "Chưa lưu được đăng ký thiết bị. Hãy thử lại.",
                true);
        }
        catch (TimeoutException)
        {
            return ApiError(
                context,
                503,
                "ERR_STORAGE_UNAVAILABLE",
                "Chưa xác nhận đăng ký thiết bị. Hãy thử lại.",
                true);
        }
    });

app.MapDelete(
    "/api/v1/notifications/devices/{deviceId:guid}",
    async Task<IResult> (
        Guid deviceId,
        DeviceRegistrationRepository repository,
        HttpContext context) =>
    {
        if (deviceId == Guid.Empty)
        {
            return ApiError(
                context,
                400,
                "ERR_VALIDATION",
                "DeviceId không hợp lệ.",
                false);
        }

        var uid = context.User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(uid))
        {
            return ApiError(
                context,
                401,
                "ERR_UNAUTHORIZED",
                "Cần đăng nhập.",
                false);
        }

        try
        {
            await repository.DeleteAsync(
                projectId,
                uid,
                deviceId,
                context.RequestAborted);

            // Không tiết lộ thiết bị tồn tại hay thuộc người khác.
            return Results.NoContent();
        }
        catch (NpgsqlException)
        {
            return ApiError(
                context,
                503,
                "ERR_STORAGE_UNAVAILABLE",
                "Chưa hủy được đăng ký thiết bị. Hãy thử lại.",
                true);
        }
        catch (TimeoutException)
        {
            return ApiError(
                context,
                503,
                "ERR_STORAGE_UNAVAILABLE",
                "Chưa xác nhận hủy đăng ký thiết bị. Hãy thử lại.",
                true);
        }
    });

app.MapGet(
    "/api/v1/notifications/inbox",
    async Task<IResult> (
        int? limit,
        string? cursor,
        bool? unreadOnly,
        NotificationInboxRepository repository,
        IAccountResolver accountResolver,
        HttpContext context) =>
    {
        var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(uid))
        {
            return ApiError(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập để sử dụng thông báo.", false);
        }

        var accountId = await accountResolver.ResolveAccountIdAsync(uid, context.RequestAborted);
        if (accountId is null || accountId == Guid.Empty)
        {
            return ApiError(context, 401, "ERR_UNAUTHORIZED", "Không thể xác định tài khoản người dùng.", false);
        }

        try
        {
            var response = await repository.GetInboxAsync(
                accountId.Value,
                limit ?? 20,
                cursor,
                unreadOnly ?? false,
                context.RequestAborted);

            return Results.Ok(response);
        }
        catch (NpgsqlException)
        {
            return ApiError(context, 503, "ERR_STORAGE_UNAVAILABLE", "Dịch vụ thông báo tạm thời không khả dụng.", true);
        }
    });

app.MapGet(
    "/api/v1/notifications/inbox/unread-count",
    async Task<IResult> (
        NotificationInboxRepository repository,
        IAccountResolver accountResolver,
        HttpContext context) =>
    {
        var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(uid))
        {
            return ApiError(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập để sử dụng thông báo.", false);
        }

        var accountId = await accountResolver.ResolveAccountIdAsync(uid, context.RequestAborted);
        if (accountId is null || accountId == Guid.Empty)
        {
            return ApiError(context, 401, "ERR_UNAUTHORIZED", "Không thể xác định tài khoản người dùng.", false);
        }

        try
        {
            var count = await repository.GetUnreadCountAsync(accountId.Value, context.RequestAborted);
            return Results.Ok(new UnreadCountResponse(count));
        }
        catch (NpgsqlException)
        {
            return ApiError(context, 503, "ERR_STORAGE_UNAVAILABLE", "Dịch vụ thông báo tạm thời không khả dụng.", true);
        }
    });

app.MapPost(
    "/api/v1/notifications/inbox/{notificationId:guid}/read",
    async Task<IResult> (
        Guid notificationId,
        NotificationInboxRepository repository,
        IAccountResolver accountResolver,
        HttpContext context) =>
    {
        if (notificationId == Guid.Empty)
        {
            return ApiError(context, 400, "ERR_VALIDATION", "NotificationId không hợp lệ.", false);
        }

        var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(uid))
        {
            return ApiError(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập để sử dụng thông báo.", false);
        }

        var accountId = await accountResolver.ResolveAccountIdAsync(uid, context.RequestAborted);
        if (accountId is null || accountId == Guid.Empty)
        {
            return ApiError(context, 401, "ERR_UNAUTHORIZED", "Không thể xác định tài khoản người dùng.", false);
        }

        try
        {
            var outcome = await repository.MarkAsReadAsync(notificationId, accountId.Value, context.RequestAborted);
            if (outcome == MarkReadOutcome.NotFound || outcome == MarkReadOutcome.Forbidden)
            {
                return ApiError(context, 404, "ERR_NOT_FOUND", "Không tìm thấy thông báo.", false);
            }

            return Results.NoContent();
        }
        catch (NpgsqlException)
        {
            return ApiError(context, 503, "ERR_STORAGE_UNAVAILABLE", "Dịch vụ thông báo tạm thời không khả dụng.", true);
        }
    });

app.UseRateLimiter();
app.MapNotificationTestPush();
app.Run();

string RequiredConfiguration(string name)
{
    var value = builder.Configuration[name];

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            $"Missing configuration: {name}");
    }

    return value;
}

static IResult ApiError(
    HttpContext context,
    int status,
    string code,
    string message,
    bool retryable)
{
    return Results.Json(new
    {
        error = new
        {
            code,
            message,
            correlationId = context.TraceIdentifier,
            retryable
        }
    }, statusCode: status);
}

public sealed record DeviceRegistrationRequest(
    Guid DeviceId,
    string? Token);
