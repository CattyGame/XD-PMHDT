using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Notification.Api;
using AURA.Profile.Api;
using AURA.Profile.Api.Models;
using FirebaseAdmin.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AURA.Notification.ConsultationChecks;

internal static class CrossServiceSocketAndFcmChecks
{
    private sealed record TestAccount(Guid AccountId, string FirebaseUid, string Status, string[] Roles, string DisplayName);

    public static async Task RunAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 8 };
        if (builder.Host is not ("localhost" or "127.0.0.1") ||
            !(builder.Database?.Contains("test", StringComparison.OrdinalIgnoreCase) == true ||
              builder.Database == "aura_dev" && builder.Port == 55432 && Environment.GetEnvironmentVariable("AURA_TEST_ALLOW_DATABASE") == "aura_dev"))
        {
            throw new InvalidOperationException("Refusing to run cross-service consultation checks against a non-test database.");
        }

        await using var adminDb = NpgsqlDataSource.Create(builder.ConnectionString);

        var profileBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Options = "-c role=aura_profile_app" };
        await using var profileDb = NpgsqlDataSource.Create(profileBuilder.ConnectionString);

        var notificationBuilder = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Options = "-c role=aura_notification_app" };
        await using var notificationDb = NpgsqlDataSource.Create(notificationBuilder.ConnectionString);

        var clinicA = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var clinicB = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var patientAId = Guid.NewGuid();
        var patientBId = Guid.NewGuid();
        var analysisA = Guid.NewGuid();
        var analysisA2 = Guid.NewGuid();
        var analysisA3 = Guid.NewGuid();
        var analysisB = Guid.NewGuid();

        var patientAAccount = new TestAccount(Guid.NewGuid(), $"uid-patient-a-{Guid.NewGuid():N}", "ACTIVE", ["PATIENT"], "Patient Alpha");
        var doctorA1 = new TestAccount(Guid.NewGuid(), $"uid-doctor-a1-{Guid.NewGuid():N}", "ACTIVE", ["DOCTOR"], "Doctor Alpha 1");
        var doctorA2 = new TestAccount(Guid.NewGuid(), $"uid-doctor-a2-{Guid.NewGuid():N}", "ACTIVE", ["DOCTOR"], "Doctor Alpha 2");
        var adminA = new TestAccount(Guid.NewGuid(), $"uid-admin-a-{Guid.NewGuid():N}", "ACTIVE", ["PATIENT"], "Clinic Admin Alpha");
        var staffA = new TestAccount(Guid.NewGuid(), $"uid-staff-a-{Guid.NewGuid():N}", "ACTIVE", ["PATIENT"], "Staff Alpha");

        var patientBAccount = new TestAccount(Guid.NewGuid(), $"uid-patient-b-{Guid.NewGuid():N}", "ACTIVE", ["PATIENT"], "Patient Beta");
        var doctorB = new TestAccount(Guid.NewGuid(), $"uid-doctor-b-{Guid.NewGuid():N}", "ACTIVE", ["DOCTOR"], "Doctor Beta");
        var adminB = new TestAccount(Guid.NewGuid(), $"uid-admin-b-{Guid.NewGuid():N}", "ACTIVE", ["PATIENT"], "Clinic Admin Beta");
        var staffB = new TestAccount(Guid.NewGuid(), $"uid-staff-b-{Guid.NewGuid():N}", "ACTIVE", ["PATIENT"], "Staff Beta");
        var systemAdmin = new TestAccount(Guid.NewGuid(), $"uid-sysadmin-{Guid.NewGuid():N}", "ACTIVE", ["SYSTEM_ADMIN"], "System Admin");

        var accounts = new ConcurrentDictionary<Guid, TestAccount>();
        var accountsByUid = new ConcurrentDictionary<string, TestAccount>(StringComparer.Ordinal);
        foreach (var acct in new[] { patientAAccount, doctorA1, doctorA2, adminA, staffA, patientBAccount, doctorB, adminB, staffB, systemAdmin })
        {
            accounts[acct.AccountId] = acct;
            accountsByUid[acct.FirebaseUid] = acct;
        }

        var bindings = new Dictionary<Guid, AnalysisBinding>
        {
            [analysisA] = new(analysisA, tenantA, patientAId),
            [analysisA2] = new(analysisA2, tenantA, patientAId),
            [analysisA3] = new(analysisA3, tenantA, patientAId),
            [analysisB] = new(analysisB, tenantB, patientBId)
        };

        var trackedConversationIds = new ConcurrentBag<Guid>();
        var trackedInboxIds = new ConcurrentBag<Guid>();
        var trackedDeviceUids = new[] { doctorA1.FirebaseUid, doctorA2.FirebaseUid, patientAAccount.FirebaseUid };

        async Task SqlAsync(string sql, params object[] args)
        {
            await using var cmd = adminDb.CreateCommand(sql);
            foreach (var arg in args) cmd.Parameters.AddWithValue(arg);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await SqlAsync(
                "INSERT INTO profile.tenants(tenant_id,tenant_kind,owner_principal_id) VALUES($1,'CLINIC',$2),($3,'CLINIC',$4)",
                tenantA, clinicA, tenantB, clinicB);
            await SqlAsync(
                "INSERT INTO profile.clinics(clinic_id,tenant_id,name,status) VALUES($1,$2,'W6-M6-04 Clinic Alpha','ACTIVE'),($3,$4,'W6-M6-04 Clinic Beta','ACTIVE')",
                clinicA, tenantA, clinicB, tenantB);

            foreach (var (clinicId, accountId, role) in new[]
            {
                (clinicA, adminA.AccountId, "CLINIC_ADMIN"),
                (clinicA, doctorA1.AccountId, "DOCTOR"),
                (clinicA, doctorA2.AccountId, "DOCTOR"),
                (clinicA, staffA.AccountId, "STAFF"),
                (clinicB, adminB.AccountId, "CLINIC_ADMIN"),
                (clinicB, doctorB.AccountId, "DOCTOR"),
                (clinicB, staffB.AccountId, "STAFF")
            })
            {
                await SqlAsync(
                    "INSERT INTO profile.memberships(clinic_id,account_id,role,status) VALUES($1,$2,$3,'ACTIVE')",
                    clinicId, accountId, role);
            }

            await SqlAsync(
                "INSERT INTO profile.patients(patient_id,tenant_id,owner_principal_id,linked_account_id,full_name,status) VALUES($1,$2,$3,$4,'Patient Alpha','ACTIVE'),($5,$6,$7,$8,'Patient Beta','ACTIVE')",
                patientAId, tenantA, clinicA, patientAAccount.AccountId,
                patientBId, tenantB, clinicB, patientBAccount.AccountId);

            var accountAuthority = new MutableAccountAuthority(accounts);
            var profileRepo = new ProfileRepository(profileDb, accountAuthority, new StaticBindingAuthority(bindings));
            await profileRepo.ProvisionProfileAsync(doctorA1.AccountId, doctorA1.DisplayName, $"{doctorA1.FirebaseUid}@example.test");

            var assignmentA1 = await profileRepo.CreateOrReactivateDoctorAssignmentAsync(clinicA, doctorA1.AccountId, patientAId, analysisA);
            Require(assignmentA1.Success && assignmentA1.Assignment is not null, "Seed assignment for DoctorA1 must succeed");
            var assignmentA1Second = await profileRepo.CreateOrReactivateDoctorAssignmentAsync(clinicA, doctorA1.AccountId, patientAId, analysisA2);
            Require(assignmentA1Second.Success && assignmentA1Second.Assignment is not null, "Seed second assignment for DoctorA1 must succeed");
            var assignmentB = await profileRepo.CreateOrReactivateDoctorAssignmentAsync(clinicB, doctorB.AccountId, patientBId, analysisB);
            Require(assignmentB.Success && assignmentB.Assignment is not null, "Seed assignment for DoctorB must succeed");

            var encryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Firebase:ProjectId"] = "w6-m6-04-test",
                ["Notification:EncryptionKeyId"] = "w6-m6-04-key",
                ["Notification:EncryptionKey"] = encryptionKey,
                ["Notification:PublicBaseUrl"] = "http://localhost:5173",
                ["ServiceAuth:ClientId"] = "profile-api",
                ["ServiceAuth:ClientSecret"] = "w6-m6-04-secret"
            }).Build();

            var fcmSpy = new CapturingFcmClient();
            var observedHubQueries = new ConcurrentQueue<string>();

            var webBuilder = WebApplication.CreateBuilder();
            webBuilder.Logging.ClearProviders();
            webBuilder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));

            webBuilder.Services.AddSingleton<IConfiguration>(config);
            webBuilder.Services.AddSingleton(profileDb);
            webBuilder.Services.AddSingleton(notificationDb);
            webBuilder.Services.AddSingleton(profileRepo);
            webBuilder.Services.AddSingleton<IAccountAuthority>(accountAuthority);
            webBuilder.Services.AddSingleton<IHttpClientFactory>(new AuthorityHttpFactory(accountsByUid, profileRepo));
            webBuilder.Services.AddSingleton<UserIdentityResolver>();

            webBuilder.Services.AddSingleton<NotificationAuthorityClient>();
            webBuilder.Services.AddSingleton<ConsultationAuthorityClient>();
            webBuilder.Services.AddSingleton<IAccountResolver>(new MutableNotificationAccountResolver(accounts, accountsByUid));
            webBuilder.Services.AddSingleton<TokenCipher>();
            webBuilder.Services.AddSingleton(sp => new DeviceRegistrationRepository(notificationDb, sp.GetRequiredService<TokenCipher>()));
            webBuilder.Services.AddSingleton(sp => new NotificationInboxRepository(notificationDb, sp.GetRequiredService<ILogger<NotificationInboxRepository>>()));
            webBuilder.Services.AddSingleton(sp => new NotificationDeliveryJobRepository(notificationDb, sp.GetRequiredService<ILogger<NotificationDeliveryJobRepository>>()));
            webBuilder.Services.AddSingleton(new PushTemplates(notificationDb));
            webBuilder.Services.AddSingleton<IFcmClient>(fcmSpy);
            webBuilder.Services.AddSingleton(new ConsultationRepository(notificationDb));
            webBuilder.Services.AddSingleton(new ConsultationOutboxRepository(notificationDb));
            webBuilder.Services.AddSingleton<ConsultationConnectionRegistry>();
            webBuilder.Services.AddSingleton<IConsultationRealtimePublisher, ConsultationRealtimePublisher>();
            webBuilder.Services.AddSingleton<ConsultationService>();
            webBuilder.Services.AddSingleton<NotificationDeliveryWorker>();
            webBuilder.Services.AddHostedService<ConsultationConnectionMonitor>();
            webBuilder.Services.AddHostedService<ConsultationNotificationOutboxWorker>();
            webBuilder.Services.AddSignalR();

            await using var app = webBuilder.Build();
            app.Use((context, next) =>
            {
                context.Items["CorrelationId"] = Guid.NewGuid().ToString("D");
                return next();
            });

            app.Use(async (context, next) =>
            {
                var isHub = context.Request.Path.StartsWithSegments("/hubs/consultations");
                var isProtected = context.Request.Path.StartsWithSegments("/api/v1/consultations") || isHub;
                if (!isProtected)
                {
                    await next(context);
                    return;
                }

                var authHeader = context.Request.Headers.Authorization.ToString();
                if (isHub)
                {
                    var tokenQuery = ConsultationTokenQuery.ExtractAndRedact(context.Request);
                    observedHubQueries.Enqueue($"{context.Request.Method} {context.Request.Path} query={context.Request.QueryString.Value}");
                    if (tokenQuery.Present)
                    {
                        if (!tokenQuery.Valid || !ConsultationTokenQuery.IsQueryTokenTransport(context.Request))
                        {
                            context.Response.StatusCode = 401;
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(authHeader))
                        {
                            authHeader = "Bearer " + tokenQuery.Token;
                            context.Request.Headers.Authorization = authHeader;
                        }
                    }
                }

                if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var token = authHeader["Bearer ".Length..].Trim();
                if (!accountsByUid.TryGetValue(token, out var acct))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString();
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, acct.FirebaseUid),
                    new Claim("firebase_uid", acct.FirebaseUid),
                    new Claim("firebase_token_exp", exp)
                }, "Firebase"));

                await next(context);
            });

            app.MapProfilePublicMembershipEndpoints();
            app.MapConsultationEndpoints();
            app.MapHub<ConsultationHub>("/hubs/consultations");

            await app.StartAsync();
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

            HttpRequestMessage Req(HttpMethod method, string path, TestAccount actor, object? body = null, string? idempotencyKey = null)
            {
                var req = new HttpRequestMessage(method, path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.FirebaseUid);
                if (idempotencyKey is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                if (body is not null) req.Content = JsonContent.Create(body);
                return req;
            }

            async Task<HubConnection> ConnectHubAsync(TestAccount actor, HttpTransportType transports = HttpTransportType.WebSockets, bool useQueryAccessToken = false)
            {
                var hubUrl = useQueryAccessToken
                    ? $"{baseUrl}/hubs/consultations?access_token={Uri.EscapeDataString(actor.FirebaseUid)}&client=w6m604"
                    : $"{baseUrl}/hubs/consultations";

                var connBuilder = new HubConnectionBuilder()
                    .WithUrl(hubUrl, options =>
                    {
                        options.Transports = transports;
                        if (!useQueryAccessToken)
                        {
                            options.AccessTokenProvider = () => Task.FromResult<string?>(actor.FirebaseUid);
                        }
                    });
                var conn = connBuilder.Build();
                await conn.StartAsync();
                return conn;
            }

            // =========================================================================
            // TC-W6-M6-04.1: 4-Role x 2-Clinic Matrix (Positive first, then Negative)
            // =========================================================================
            var doctorsRouteA = $"/api/v1/analyses/{analysisA:D}/consultation-doctors?clinicContextId={clinicA:D}";
            using (var r = await http.SendAsync(Req(HttpMethod.Get, doctorsRouteA, patientAAccount)))
            {
                Require(r.StatusCode == HttpStatusCode.OK, "Positive: PatientA reads eligible doctors HTTP 200");
                var docJson = await r.Content.ReadFromJsonAsync<JsonElement>();
                var docIds = docJson.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("accountId").GetGuid()).ToArray();
                Require(docIds.SequenceEqual([doctorA1.AccountId]), "Positive: only actively assigned DoctorA1 is returned");
            }
            using (var r = await http.SendAsync(Req(HttpMethod.Get, doctorsRouteA, doctorA1)))
            {
                Require(r.StatusCode == HttpStatusCode.OK, "Positive: DoctorA1 reads eligible doctors HTTP 200");
            }

            var createKey = Guid.NewGuid().ToString("D");
            Guid convA;
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisA, clinicContextId = clinicA, doctorAccountId = doctorA1.AccountId }, createKey)))
            {
                Require(r.StatusCode == HttpStatusCode.Created, "Positive: PatientA creates Consultation A HTTP 201");
                convA = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
                trackedConversationIds.Add(convA);
            }
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisA, clinicContextId = clinicA, doctorAccountId = doctorA1.AccountId }, createKey)))
            {
                Require(r.StatusCode == HttpStatusCode.OK, "Positive: Replay create with identical Idempotency-Key returns HTTP 200");
                Require((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid() == convA,
                    "Positive: Replay create returns identical conversationId");
            }
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisA2, clinicContextId = clinicA, doctorAccountId = doctorA1.AccountId }, createKey)))
            {
                Require(r.StatusCode == HttpStatusCode.Conflict, "Negative: Replay create key with changed authorized payload returns HTTP 409");
            }
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisB, clinicContextId = clinicB, doctorAccountId = doctorB.AccountId }, createKey)))
            {
                Require(r.StatusCode == HttpStatusCode.Forbidden, "Negative: Cross-clinic create attempt returns HTTP 403 before idempotency");
            }

            // Connect PatientA and DoctorA1 to Hub and run positive send + read cursor + pagination
            var patientSocket = await ConnectHubAsync(patientAAccount);
            var doctorSocketA1 = await ConnectHubAsync(doctorA1);
            var doctorA1Received = new ConcurrentQueue<string>();
            var doctorA1ReadCursors = new ConcurrentQueue<Guid>();
            var doctorA1RevokedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var doctorA1ClosedTcs = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);

            doctorSocketA1.On<JsonElement>("MessagePersisted", payload =>
            {
                doctorA1Received.Enqueue(payload.GetProperty("text").GetString() ?? "");
            });
            doctorSocketA1.On<JsonElement>("ReadCursorAdvanced", payload =>
            {
                doctorA1ReadCursors.Enqueue(payload.GetProperty("lastReadMessageId").GetGuid());
            });
            doctorSocketA1.On<JsonElement>("AccessRevoked", payload =>
            {
                doctorA1RevokedTcs.TrySetResult(payload.GetProperty("code").GetString() ?? "");
            });
            doctorSocketA1.Closed += _ =>
            {
                doctorA1ClosedTcs.TrySetResult(DateTimeOffset.UtcNow);
                return Task.CompletedTask;
            };

            await patientSocket.InvokeAsync("JoinConversation", convA);
            await doctorSocketA1.InvokeAsync("JoinConversation", convA);

            var msg1Client = Guid.NewGuid();
            var sentMsg1 = await patientSocket.InvokeAsync<ConsultationMessage>("SendMessage", convA, msg1Client, "Positive control hello");
            Require(sentMsg1 is not null && sentMsg1.Text == "Positive control hello", "Positive: Hub SendMessage persists and returns message");
            await WaitUntilAsync(() => doctorA1Received.Contains("Positive control hello"), TimeSpan.FromSeconds(3),
                "Positive: DoctorA1 receives MessagePersisted over open SignalR socket");

            // Verify message persisted in DB before ACK/broadcast
            await using (var msgCountCmd = adminDb.CreateCommand("SELECT count(*) FROM notification.consultation_messages WHERE message_id=$1"))
            {
                msgCountCmd.Parameters.AddWithValue(sentMsg1!.MessageId);
                Require(Convert.ToInt64(await msgCountCmd.ExecuteScalarAsync()) == 1, "Message persisted in PostgreSQL");
            }

            await patientSocket.InvokeAsync("MarkRead", convA, sentMsg1!.MessageId);
            await WaitUntilAsync(() => doctorA1ReadCursors.Contains(sentMsg1.MessageId), TimeSpan.FromSeconds(3),
                "Positive: ReadCursorAdvanced pushed over SignalR");

            using (var r = await http.SendAsync(Req(HttpMethod.Get, $"/api/v1/consultations/{convA:D}/messages?limit=100", doctorA1)))
                Require(r.StatusCode == HttpStatusCode.OK, "Positive: GET /messages limit=100 succeeds");
            using (var r = await http.SendAsync(Req(HttpMethod.Get, $"/api/v1/consultations/{convA:D}/messages?limit=101", doctorA1)))
                Require(r.StatusCode == HttpStatusCode.BadRequest, "Negative: GET /messages limit=101 rejected with 400");

            // Negative 4-Role x 2-Clinic Matrix (unassigned doctor, clinic admins, staff, cross-clinic actors, global system admin)
            var negativeActors = new[] { doctorA2, adminA, staffA, doctorB, adminB, staffB, patientBAccount, systemAdmin };
            foreach (var actor in negativeActors)
            {
                using (var r = await http.SendAsync(Req(HttpMethod.Get, doctorsRouteA, actor)))
                {
                    Require(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                        $"Negative: {actor.DisplayName} denied consultation-doctors (got {(int)r.StatusCode})");
                }
                using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", actor,
                    new { analysisId = analysisA, clinicContextId = clinicA, doctorAccountId = doctorA1.AccountId }, Guid.NewGuid().ToString("D"))))
                {
                    Require(r.StatusCode == HttpStatusCode.Forbidden,
                        $"Negative: {actor.DisplayName} denied create consultation (got {(int)r.StatusCode})");
                }
                using (var r = await http.SendAsync(Req(HttpMethod.Get, $"/api/v1/consultations/{convA:D}/messages", actor)))
                {
                    Require(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                        $"Negative: {actor.DisplayName} denied reading Conversation A (got {(int)r.StatusCode})");
                }
                using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convA:D}/messages", actor,
                    new { clientMessageId = Guid.NewGuid(), text = "unauthorized send" })))
                {
                    Require(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                        $"Negative: {actor.DisplayName} denied sending to Conversation A (got {(int)r.StatusCode})");
                }
                using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convA:D}/read", actor,
                    new { lastReadMessageId = sentMsg1.MessageId })))
                {
                    Require(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                        $"Negative: {actor.DisplayName} denied advancing read cursor (got {(int)r.StatusCode})");
                }
                using (var r = await http.SendAsync(Req(HttpMethod.Get, "/api/v1/consultations", actor)))
                {
                    Require(r.StatusCode == HttpStatusCode.OK, "List consultations returns 200 with authorized filter");
                    var items = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();
                    Require(items.All(i => i.GetProperty("conversationId").GetGuid() != convA),
                        $"Negative: {actor.DisplayName} never sees Conversation A in list");
                }

                await using var badSocket = await ConnectHubAsync(actor);
                var badHubMessages = new ConcurrentQueue<string>();
                badSocket.On<JsonElement>("MessagePersisted", p => badHubMessages.Enqueue(p.GetProperty("text").GetString() ?? ""));
                var hubDenied = false;
                try { await badSocket.InvokeAsync("JoinConversation", convA); }
                catch { hubDenied = true; }
                Require(hubDenied, $"Negative: {actor.DisplayName} denied SignalR JoinConversation");
                Require(badHubMessages.IsEmpty, $"Negative: {actor.DisplayName} received no SignalR messages");
            }
            Console.WriteLine("PASS: TC-W6-M6-04.1: 4-role x 2-clinic matrix (positive controls + negative join/read/send/push/eligible-doctors).");

            // =========================================================================
            // TC-W6-M6-04.2: Open-Socket Revocation #1 — Assignment Revoked <= 5s
            // =========================================================================
            var swAssign = Stopwatch.StartNew();
            var revokeAssign = await profileRepo.RevokeDoctorAssignmentAsync(clinicA, assignmentA1.Assignment!.AssignmentId);
            Require(revokeAssign.Success, "Assignment revocation must succeed");

            // Attempt immediate send after revoke commit; revoked doctor socket must NOT receive it
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convA:D}/messages", patientAAccount,
                new { clientMessageId = Guid.NewGuid(), text = "POST_REVOKE_SECRET_ASSIGNMENT" })))
            {
                // SendAsync may return 201 (patient still allowed to send to self/thread, though doctor broadcast skipped) or 403 depending on participant check;
                // Crucially, DoctorA1's open socket MUST NEVER receive POST_REVOKE_SECRET_ASSIGNMENT.
                Require(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Forbidden,
                    $"Post-assignment-revoke send handled cleanly ({(int)r.StatusCode})");
            }

            var revokedCode1 = await doctorA1RevokedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await doctorA1ClosedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            swAssign.Stop();
            Require(revokedCode1 == "ACCESS_REVOKED", "DoctorA1 received AccessRevoked event");
            Require(swAssign.Elapsed <= TimeSpan.FromSeconds(5), $"Socket close elapsed {swAssign.ElapsedMilliseconds}ms <= 5000ms");
            Require(!doctorA1Received.Contains("POST_REVOKE_SECRET_ASSIGNMENT"),
                "Revoked open socket must NOT receive post-revoke payload");

            // Reconnect/REST after assignment revoke must be denied
            await using (var reconnectSocket = await ConnectHubAsync(doctorA1))
            {
                var failedJoin = false;
                try { await reconnectSocket.InvokeAsync("JoinConversation", convA); } catch { failedJoin = true; }
                Require(failedJoin, "Reconnect JoinConversation denied after assignment revoke");
            }
            using (var r = await http.SendAsync(Req(HttpMethod.Get, $"/api/v1/consultations/{convA:D}/messages", doctorA1)))
                Require(r.StatusCode == HttpStatusCode.Forbidden, "REST GET /messages returns 403 after assignment revoke");
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convA:D}/messages", doctorA1,
                new { clientMessageId = Guid.NewGuid(), text = "blocked after revoke" })))
                Require(r.StatusCode == HttpStatusCode.Forbidden, "REST POST /messages returns 403 after assignment revoke");
            Console.WriteLine($"PASS: TC-W6-M6-04.2: Assignment revoke closed open socket in {swAssign.ElapsedMilliseconds}ms (<=5000ms), blocked post-revoke payload & reconnect.");

            // =========================================================================
            // TC-W6-M6-04.3: Open-Socket Revocation #2 — Clinic Membership Revoked <= 5s
            // =========================================================================
            var assignmentA1Next = await profileRepo.CreateOrReactivateDoctorAssignmentAsync(clinicA, doctorA1.AccountId, patientAId, analysisA);
            Require(assignmentA1Next.Success, "Re-create assignment for membership revoke test");
            Guid convMembership;
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisA, clinicContextId = clinicA, doctorAccountId = doctorA1.AccountId }, Guid.NewGuid().ToString("D"))))
            {
                Require(r.StatusCode == HttpStatusCode.Created, "Positive: new conversation created for membership revoke check");
                convMembership = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
                trackedConversationIds.Add(convMembership);
            }

            var docSocketMem = await ConnectHubAsync(doctorA1);
            var docMemMsgs = new ConcurrentQueue<string>();
            var docMemRevoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var docMemClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            docSocketMem.On<JsonElement>("MessagePersisted", p => docMemMsgs.Enqueue(p.GetProperty("text").GetString() ?? ""));
            docSocketMem.On<JsonElement>("AccessRevoked", p => docMemRevoked.TrySetResult(p.GetProperty("code").GetString() ?? ""));
            docSocketMem.Closed += _ => { docMemClosed.TrySetResult(true); return Task.CompletedTask; };
            await docSocketMem.InvokeAsync("JoinConversation", convMembership);

            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convMembership:D}/messages", patientAAccount,
                new { clientMessageId = Guid.NewGuid(), text = "positive before membership revoke" })))
                Require(r.StatusCode == HttpStatusCode.Created, "Positive control before membership revoke");
            await WaitUntilAsync(() => docMemMsgs.Contains("positive before membership revoke"), TimeSpan.FromSeconds(3),
                "Doctor receives positive control before membership revoke");

            var swMember = Stopwatch.StartNew();
            var revokeMember = await profileRepo.RevokeClinicMemberAsync(clinicA, doctorA1.AccountId, adminA.AccountId);
            Require(revokeMember.Success, "Membership revoke must succeed");
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convMembership:D}/messages", patientAAccount,
                new { clientMessageId = Guid.NewGuid(), text = "POST_REVOKE_SECRET_MEMBERSHIP" })))
                Require(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Forbidden, "Post-membership-revoke send handled");

            Require(await docMemRevoked.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "ACCESS_REVOKED", "AccessRevoked received on membership revoke");
            await docMemClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            swMember.Stop();
            Require(swMember.Elapsed <= TimeSpan.FromSeconds(5), $"Membership revoke close {swMember.ElapsedMilliseconds}ms <= 5000ms");
            Require(!docMemMsgs.Contains("POST_REVOKE_SECRET_MEMBERSHIP"), "Revoked member socket must NOT receive post-revoke payload");
            using (var r = await http.SendAsync(Req(HttpMethod.Get, $"/api/v1/consultations/{convMembership:D}/messages", doctorA1)))
                Require(r.StatusCode == HttpStatusCode.Forbidden, "REST denied after membership revoke");
            await docSocketMem.DisposeAsync();
            Console.WriteLine($"PASS: TC-W6-M6-04.3: Membership revoke closed open socket in {swMember.ElapsedMilliseconds}ms (<=5000ms), blocked post-revoke payload & reconnect.");

            // =========================================================================
            // TC-W6-M6-04.4: Open-Socket Revocation #3 — Account Disabled <= 5s
            // =========================================================================
            var assignA2 = await profileRepo.CreateOrReactivateDoctorAssignmentAsync(clinicA, doctorA2.AccountId, patientAId, analysisA);
            Require(assignA2.Success, "Assign DoctorA2 for account-disable check");
            Guid convAccount;
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisA, clinicContextId = clinicA, doctorAccountId = doctorA2.AccountId }, Guid.NewGuid().ToString("D"))))
            {
                Require(r.StatusCode == HttpStatusCode.Created, "Positive: conversation created for account-disable check");
                convAccount = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
                trackedConversationIds.Add(convAccount);
            }

            var docSocketAcct = await ConnectHubAsync(doctorA2);
            var docAcctMsgs = new ConcurrentQueue<string>();
            var docAcctRevoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var docAcctClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            docSocketAcct.On<JsonElement>("MessagePersisted", p => docAcctMsgs.Enqueue(p.GetProperty("text").GetString() ?? ""));
            docSocketAcct.On<JsonElement>("AccessRevoked", p => docAcctRevoked.TrySetResult(p.GetProperty("code").GetString() ?? ""));
            docSocketAcct.Closed += _ => { docAcctClosed.TrySetResult(true); return Task.CompletedTask; };
            await docSocketAcct.InvokeAsync("JoinConversation", convAccount);

            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convAccount:D}/messages", patientAAccount,
                new { clientMessageId = Guid.NewGuid(), text = "positive before account disable" })))
                Require(r.StatusCode == HttpStatusCode.Created, "Positive control before account disable");
            await WaitUntilAsync(() => docAcctMsgs.Contains("positive before account disable"), TimeSpan.FromSeconds(3),
                "DoctorA2 receives positive control before account disable");

            var swAcct = Stopwatch.StartNew();
            accounts[doctorA2.AccountId] = doctorA2 with { Status = "DISABLED" };
            accountsByUid[doctorA2.FirebaseUid] = accounts[doctorA2.AccountId];

            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convAccount:D}/messages", patientAAccount,
                new { clientMessageId = Guid.NewGuid(), text = "POST_REVOKE_SECRET_ACCOUNT" })))
                Require(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Forbidden, "Post-account-disable send handled");

            Require(await docAcctRevoked.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "ACCESS_REVOKED", "AccessRevoked received on account disable");
            await docAcctClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            swAcct.Stop();
            Require(swAcct.Elapsed <= TimeSpan.FromSeconds(5), $"Account disable close {swAcct.ElapsedMilliseconds}ms <= 5000ms");
            Require(!docAcctMsgs.Contains("POST_REVOKE_SECRET_ACCOUNT"), "Disabled account socket must NOT receive post-revoke payload");
            using (var r = await http.SendAsync(Req(HttpMethod.Get, $"/api/v1/consultations/{convAccount:D}/messages", doctorA2)))
                Require(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, "Disabled account denied REST read on reconnect");
            await docSocketAcct.DisposeAsync();
            Console.WriteLine($"PASS: TC-W6-M6-04.4: Account DISABLED closed open socket in {swAcct.ElapsedMilliseconds}ms (<=5000ms), blocked post-revoke payload & reconnect.");

            // =========================================================================
            // TC-W6-M6-04.5: access_token Redaction & Transport Restriction
            // =========================================================================
            using (var negotiateQuery = new HttpRequestMessage(HttpMethod.Post, $"/hubs/consultations/negotiate?access_token={Uri.EscapeDataString(patientAAccount.FirebaseUid)}"))
            using (var r = await http.SendAsync(negotiateQuery))
            {
                Require(r.StatusCode == HttpStatusCode.Unauthorized, "Query access_token on negotiate endpoint rejected 401");
            }
            using (var nonGetSse = new HttpRequestMessage(HttpMethod.Post, $"/hubs/consultations?access_token={Uri.EscapeDataString(patientAAccount.FirebaseUid)}"))
            {
                nonGetSse.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var r = await http.SendAsync(nonGetSse);
                Require(r.StatusCode == HttpStatusCode.Unauthorized, "Non-GET request with query access_token rejected 401");
            }

            string connectionToken;
            using (var negotiate = Req(HttpMethod.Post, "/hubs/consultations/negotiate?negotiateVersion=1", patientAAccount))
            using (var r = await http.SendAsync(negotiate))
            {
                Require(r.StatusCode == HttpStatusCode.OK, "Authorized negotiate succeeds");
                connectionToken = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionToken").GetString()!;
            }
            using (var sseReq = new HttpRequestMessage(HttpMethod.Get,
                $"/hubs/consultations?id={Uri.EscapeDataString(connectionToken)}&access_token={Uri.EscapeDataString(patientAAccount.FirebaseUid)}&client=w6m604"))
            {
                sseReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var r = await http.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
                Require(r.StatusCode == HttpStatusCode.OK, "SSE GET with query access_token succeeds after negotiate");
                Require(observedHubQueries.Any(q => q.StartsWith("GET /hubs/consultations ", StringComparison.Ordinal) &&
                    q.Contains("client=w6m604") && !q.Contains("access_token") && !q.Contains(patientAAccount.FirebaseUid)),
                    "SSE query access_token was extracted and redacted while non-secret query params remained");
            }
            Console.WriteLine("PASS: TC-W6-M6-04.5: SignalR query access_token redacted on SSE and rejected on negotiate/non-GET.");

            // =========================================================================
            // TC-W6-M6-04.6: Replay Dedup + Outbox -> Generic FCM Push + Revoke-before-Push
            // =========================================================================
            // Reactivate DoctorA1 & assignment in Clinic A for push checks
            var reactivate = await profileRepo.AddOrReactivateClinicMemberAsync(clinicA, doctorA1.AccountId, "DOCTOR", adminA.AccountId);
            Require(reactivate.Success, "Reactivate DoctorA1 membership for FCM checks");
            var assignFcm = await profileRepo.CreateOrReactivateDoctorAssignmentAsync(clinicA, doctorA1.AccountId, patientAId, analysisA3);
            Require(assignFcm.Success, $"Assign DoctorA1 for FCM checks ({assignFcm.ErrorCode})");

            Guid convFcm;
            using (var r = await http.SendAsync(Req(HttpMethod.Post, "/api/v1/consultations", patientAAccount,
                new { analysisId = analysisA3, clinicContextId = clinicA, doctorAccountId = doctorA1.AccountId }, Guid.NewGuid().ToString("D"))))
            {
                Require(r.StatusCode == HttpStatusCode.Created, "Positive: conversation created for FCM outbox checks");
                convFcm = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
                trackedConversationIds.Add(convFcm);
            }

            var devicesRepo = app.Services.GetRequiredService<DeviceRegistrationRepository>();
            var deviceId = Guid.NewGuid();
            await devicesRepo.RegisterAsync("w6-m6-04-test", doctorA1.FirebaseUid, deviceId, "fcm-device-token-w6m604", CancellationToken.None);

            var sensitiveText = "PHI_SENSITIVE_PATIENT_NGUYEN_VAN_X_HEMORRHAGE";
            var replayClientId = Guid.NewGuid();
            Guid msgFcmId;
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convFcm:D}/messages", patientAAccount,
                new { clientMessageId = replayClientId, text = sensitiveText })))
            {
                Require(r.StatusCode == HttpStatusCode.Created, "Send sensitive consultation message HTTP 201");
                msgFcmId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetGuid();
            }
            // Replay identical payload -> 200, changed payload -> 409
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convFcm:D}/messages", patientAAccount,
                new { clientMessageId = replayClientId, text = sensitiveText })))
                Require(r.StatusCode == HttpStatusCode.OK, "Replay same clientMessageId + text returns HTTP 200");
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convFcm:D}/messages", patientAAccount,
                new { clientMessageId = replayClientId, text = "MUTATED_TEXT" })))
                Require(r.StatusCode == HttpStatusCode.Conflict, "Replay same clientMessageId + changed text returns HTTP 409");

            // Wait for ConsultationNotificationOutboxWorker to complete the outbox row
            Guid outboxId = Guid.Empty;
            await WaitUntilAsync(async () =>
            {
                await using var cmd = adminDb.CreateCommand(
                    "SELECT outbox_id, status FROM notification.consultation_notification_outbox WHERE message_id=$1");
                cmd.Parameters.AddWithValue(msgFcmId);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return false;
                outboxId = reader.GetGuid(0);
                return reader.GetString(1) == "COMPLETED";
            }, TimeSpan.FromSeconds(5), "ConsultationNotificationOutboxWorker completes outbox row");

            // Verify exactly 1 inbox item & 1 delivery job; replay SaveWithDeliveryJobsAsync does NOT multiply
            var inboxRepo = app.Services.GetRequiredService<NotificationInboxRepository>();
            var jobsRepo = app.Services.GetRequiredService<NotificationDeliveryJobRepository>();
            await using (var checkCmd = adminDb.CreateCommand(
                "SELECT id FROM notification.inbox_items WHERE account_id=$1 AND event_id=$2"))
            {
                checkCmd.Parameters.AddWithValue(doctorA1.AccountId);
                checkCmd.Parameters.AddWithValue(outboxId);
                var inboxItemId = (Guid)(await checkCmd.ExecuteScalarAsync())!;
                trackedInboxIds.Add(inboxItemId);
            }

            var replayInbox = await inboxRepo.SaveWithDeliveryJobsAsync(
                new SaveInboxItemCommand(doctorA1.AccountId, tenantA, outboxId, null, "CONSULTATION_MESSAGE",
                    "Có tin nhắn tư vấn mới", $"/consultations/{convFcm:D}"),
                [(deviceId, SHA256.HashData(Encoding.UTF8.GetBytes("fcm-device-token-w6m604")))],
                jobsRepo, CancellationToken.None);
            Require(replayInbox.Outcome == SaveInboxOutcome.Deduplicated, "Replay outbox save is deduplicated");

            await using (var countInbox = adminDb.CreateCommand(
                "SELECT (SELECT count(*) FROM notification.inbox_items WHERE account_id=$1 AND event_id=$2), (SELECT count(*) FROM notification.delivery_jobs WHERE account_id=$1 AND inbox_item_id=$3)"))
            {
                countInbox.Parameters.AddWithValue(doctorA1.AccountId);
                countInbox.Parameters.AddWithValue(outboxId);
                countInbox.Parameters.AddWithValue(replayInbox.NotificationId);
                await using var r = await countInbox.ExecuteReaderAsync();
                Require(await r.ReadAsync() && r.GetInt64(0) == 1 && r.GetInt64(1) == 1,
                    "Replay never multiplies inbox_items or delivery_jobs");
            }

            var deliveryWorker = app.Services.GetRequiredService<NotificationDeliveryWorker>();
            var dispatched = await deliveryWorker.DispatchPendingAsync(CancellationToken.None);
            Require(dispatched == 1, "DeliveryWorker dispatches 1 generic consultation push");

            var fcmMessage = fcmSpy.Messages.Single();
            var serializedPush = JsonSerializer.Serialize(new
            {
                fcmMessage.Notification?.Title,
                fcmMessage.Notification?.Body,
                fcmMessage.Data,
                WebTitle = fcmMessage.Webpush?.Notification?.Title,
                WebBody = fcmMessage.Webpush?.Notification?.Body,
                Link = fcmMessage.Webpush?.FcmOptions?.Link
            });
            Require(!serializedPush.Contains("PHI_SENSITIVE", StringComparison.OrdinalIgnoreCase) &&
                    !serializedPush.Contains("NGUYEN_VAN_X", StringComparison.OrdinalIgnoreCase) &&
                    !serializedPush.Contains("HEMORRHAGE", StringComparison.OrdinalIgnoreCase),
                "FCM push payload is strictly generic and contains zero chat content or PHI");
            Require(fcmMessage.Data["targetPath"] == $"/consultations/{convFcm:D}",
                "FCM targetPath points to protected consultation thread requiring re-auth");

            // Explicit separation: FCM accepted (delivery_jobs=COMPLETED) vs actual client receipt (inbox is_read=false)
            await using (var sepCmd = adminDb.CreateCommand(
                "SELECT dj.status, ii.is_read FROM notification.delivery_jobs dj JOIN notification.inbox_items ii ON ii.id=dj.inbox_item_id WHERE ii.id=$1"))
            {
                sepCmd.Parameters.AddWithValue(replayInbox.NotificationId);
                await using var r = await sepCmd.ExecuteReaderAsync();
                Require(await r.ReadAsync() && r.GetString(0) == "COMPLETED" && !r.GetBoolean(1),
                    "FCM accepted (delivery_jobs=COMPLETED) remains distinct from actual client receipt (inbox is_read=false)");
            }

            // Revoke DoctorA1 BEFORE outbox claims a second message -> outbox must CANCELED, 0 new push
            var fcmCountBeforeRevoke = fcmSpy.Messages.Count;
            var secondClientId = Guid.NewGuid();
            Guid secondMsgId;
            // Insert second message and immediately revoke assignment in quick succession
            using (var r = await http.SendAsync(Req(HttpMethod.Post, $"/api/v1/consultations/{convFcm:D}/messages", patientAAccount,
                new { clientMessageId = secondClientId, text = "message while being revoked" })))
            {
                Require(r.StatusCode == HttpStatusCode.Created, "Patient sends second message");
                secondMsgId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetGuid();
            }
            await profileRepo.RevokeDoctorAssignmentAsync(clinicA, assignFcm.Assignment!.AssignmentId);
            // Reset outbox row to PENDING if worker raced ahead so we deterministically verify revoked recipient check
            await SqlAsync(
                "UPDATE notification.consultation_notification_outbox SET status='PENDING', attempts=0, next_attempt_at=now(), lease_token=NULL, lease_expires_at=NULL WHERE message_id=$1 AND status<>'CANCELED'",
                secondMsgId);
            await SqlAsync(
                "DELETE FROM notification.inbox_items WHERE account_id=$1 AND event_id IN (SELECT outbox_id FROM notification.consultation_notification_outbox WHERE message_id=$2)",
                doctorA1.AccountId, secondMsgId);

            await WaitUntilAsync(async () =>
            {
                await using var cmd = adminDb.CreateCommand(
                    "SELECT status FROM notification.consultation_notification_outbox WHERE message_id=$1");
                cmd.Parameters.AddWithValue(secondMsgId);
                return (await cmd.ExecuteScalarAsync()) as string == "CANCELED";
            }, TimeSpan.FromSeconds(5), "Revoked recipient outbox item is CANCELED without creating push/inbox");

            Require(await deliveryWorker.DispatchPendingAsync(CancellationToken.None) == 0,
                "No delivery jobs created for revoked recipient");
            Require(fcmSpy.Messages.Count == fcmCountBeforeRevoke,
                "FCM provider receives zero calls for revoked recipient");

            Console.WriteLine("PASS: TC-W6-M6-04.6: Replay dedup, generic FCM push (zero PHI), FCM accepted != actual receipt, and post-revoke outbox cancellation.");
            Console.WriteLine($"ALL W6-M6-04 CROSS-SERVICE CHECKS PASSED (assign={swAssign.ElapsedMilliseconds}ms, member={swMember.ElapsedMilliseconds}ms, account={swAcct.ElapsedMilliseconds}ms).");

            await patientSocket.DisposeAsync();
            await doctorSocketA1.DisposeAsync();
        }
        finally
        {
            await TrySqlAsync("DELETE FROM notification.consultation_conversations WHERE conversation_id = ANY($1);", trackedConversationIds.ToArray());
            await TrySqlAsync("DELETE FROM notification.inbox_items WHERE id = ANY($1);", trackedInboxIds.ToArray());
            await TrySqlAsync("DELETE FROM notification.device_registrations WHERE firebase_uid = ANY($1);", trackedDeviceUids);
            await TrySqlAsync("DELETE FROM notification.consultation_send_buckets WHERE account_id = ANY($1);",
                accounts.Keys.ToArray());
            await TrySqlAsync("DELETE FROM profile.doctor_assignments WHERE clinic_id = ANY($1);", new[] { clinicA, clinicB });
            await TrySqlAsync("DELETE FROM profile.patients WHERE patient_id = ANY($1) OR linked_account_id = ANY($1);", new[] { patientAId, patientBId, doctorA1.AccountId });
            await TrySqlAsync("DELETE FROM profile.memberships WHERE clinic_id = ANY($1);", new[] { clinicA, clinicB });
            await TrySqlAsync("DELETE FROM profile.profiles WHERE account_id = ANY($1);", new[] { doctorA1.AccountId });
            await TrySqlAsync("DELETE FROM profile.clinics WHERE clinic_id = ANY($1);", new[] { clinicA, clinicB });
            await TrySqlAsync("DELETE FROM profile.tenants WHERE tenant_id = ANY($1) OR owner_principal_id = ANY($1);", new[] { tenantA, tenantB, doctorA1.AccountId });
        }

        async Task TrySqlAsync(string sql, object arrayArg)
        {
            try { await SqlAsync(sql, new object[] { arrayArg }); }
            catch (Exception ex) { Console.Error.WriteLine($"WARN cleanup: {ex.Message}"); }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for: {description}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("TC-W6-M6-04 FAIL: " + message);
    }

    private sealed class MutableAccountAuthority(ConcurrentDictionary<Guid, TestAccount> accounts) : IAccountAuthority
    {
        public Task<AccountAuthoritySnapshot?> GetAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult(accounts.TryGetValue(accountId, out var acct)
                ? new AccountAuthoritySnapshot(acct.AccountId, acct.Status, acct.Roles)
                : null);
    }

    private sealed class StaticBindingAuthority(Dictionary<Guid, AnalysisBinding> bindings) : IAnalysisBindingAuthority
    {
        public Task<AnalysisBinding?> GetAsync(Guid analysisId, CancellationToken ct) =>
            Task.FromResult(bindings.TryGetValue(analysisId, out var binding) ? binding : null);
    }

    private sealed class MutableNotificationAccountResolver(
        ConcurrentDictionary<Guid, TestAccount> accounts,
        ConcurrentDictionary<string, TestAccount> byUid) : IAccountResolver
    {
        public Task<Guid?> ResolveAccountIdAsync(string firebaseUid, CancellationToken ct) =>
            Task.FromResult<Guid?>(byUid.TryGetValue(firebaseUid, out var acct) && acct.Status == "ACTIVE" ? acct.AccountId : null);

        public Task<string?> ResolveFirebaseUidAsync(Guid accountId, CancellationToken ct) =>
            Task.FromResult<string?>(accounts.TryGetValue(accountId, out var acct) && acct.Status == "ACTIVE" ? acct.FirebaseUid : null);
    }

    private sealed class CapturingFcmClient : IFcmClient
    {
        public List<MulticastMessage> Messages { get; } = [];
        public Task<FcmMulticastResult> SendMulticastAsync(IReadOnlyList<ActiveDeviceToken> devices, MulticastMessage message, CancellationToken ct)
        {
            lock (Messages) Messages.Add(message);
            var outcomes = devices.Select(d => new FcmSendOutcome(d.Token, d.DeviceId, true, $"fcm-msg-{Guid.NewGuid():N}", null, null)).ToList();
            return Task.FromResult(new FcmMulticastResult(outcomes.Count, 0, outcomes));
        }
    }

    private sealed class AuthorityHttpFactory(
        ConcurrentDictionary<string, TestAccount> byUid,
        ProfileRepository profileRepository) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var baseAddress = name switch
            {
                "IdentityClient" or "IdentityAuthority" => "http://identity.local/",
                "ProfileAuthority" => "http://profile.local/",
                _ => "http://internal.local/"
            };
            return new HttpClient(new AuthorityHandler(byUid, profileRepository), disposeHandler: false)
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = TimeSpan.FromSeconds(5)
            };
        }
    }

    private sealed class AuthorityHandler(
        ConcurrentDictionary<string, TestAccount> byUid,
        ProfileRepository profileRepository) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path == "/internal/v1/identity/service-tokens")
                return JsonOk(new { accessToken = "w6m604-service-token", tokenType = "Bearer", expiresIn = 300 });

            if (path == "/internal/v1/identity/context")
            {
                var rawUser = request.Headers.TryGetValues("X-User-Authorization", out var values) ? values.FirstOrDefault() : null;
                var uid = rawUser?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (uid is not null && byUid.TryGetValue(uid, out var acct))
                {
                    return JsonOk(new
                    {
                        accountId = acct.AccountId,
                        accountStatus = acct.Status,
                        roles = acct.Roles,
                        email = $"{acct.FirebaseUid}@example.test",
                        emailVerified = true
                    });
                }
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (path == "/internal/v1/profile/consultation-access/resolve")
            {
                var body = await request.Content!.ReadFromJsonAsync<ConsultationAccessResolveRequest>(cancellationToken: ct);
                var decision = await profileRepository.ResolveConsultationAccessAsync(body!, ct);
                return JsonOk(decision);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonOk(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }
}
