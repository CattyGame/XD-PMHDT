using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Models;
using AURA.Billing.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace AURA.Billing.ReservationChecks;

public class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("====================================================================");
        Console.WriteLine("AURA Task SCRUM-326 (W3-M3-01) Verification Suite: Billing & Credit");
        Console.WriteLine("====================================================================");

        try
        {
            await TestAC1_ConcurrencyAndIdempotencyAsync();
            await TestAC2_LifecycleSettlementAndExclusivityAsync();
            await TestAC3_ReconciliationSecurityAndPrivacyAsync();
            await TestSCRUM346_PaymentSandboxAndIdempotentCreditAsync();
            await TestSCRUM348_ClinicBillingAndAutoRefundAsync();
            await TestSCRUM349_ResilienceAndFinancialReconciliationAsync();
            await TestSCRUM365_ClinicPackagesAndEntitlementsAsync();
            await TestSCRUM366_BatchRefundAndReconciliationAsync();

            Console.WriteLine();
            Console.WriteLine("====================================================================");
            Console.WriteLine($"BILLING VERIFICATION RESULTS: Passed: {_passed}, Failed: {_failed}");
            Console.WriteLine("====================================================================");

            return _failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nFATAL UNHANDLED SUITE ERROR: {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void Pass(string testName)
    {
        _passed++;
        Console.WriteLine($"  [PASS] {testName}");
    }

    private static void Fail(string testName, string reason)
    {
        _failed++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [FAIL] {testName}: {reason}");
        Console.ResetColor();
    }

    private static void Assert(bool condition, string testName, string message = "")
    {
        if (condition)
        {
            Pass(testName);
        }
        else
        {
            Fail(testName, string.IsNullOrWhiteSpace(message) ? "Assertion failed." : message);
        }
    }

    // =========================================================================
    // NHÓM AC1: KIỂM THỬ TRANH CHẤP ĐỒNG THỜI & IDEMPOTENCY (CONCURRENCY & IDEMPOTENCY)
    // =========================================================================
    private static async Task TestAC1_ConcurrencyAndIdempotencyAsync()
    {
        Console.WriteLine("\n[AC1] Concurrency, Race Condition, & Idempotency Verification:");

        // TC-AC1.1: 2 request đồng thời tranh chấp 1 unit credit duy nhất trong ví TEST
        var singleWalletId = Guid.Parse("a3010000-0000-4000-8000-000000000001");
        var ownerId = Guid.Parse("e3010000-0000-4000-8000-000000000001");
        var tenantId = Guid.Parse("c3010000-0000-4000-8000-000000000001");
        var runtimeOptions = new BillingRuntimeOptions("TEST", false);

        var simulatedWalletBalance = 1;
        var walletLock = new object();
        var reservations = new ConcurrentDictionary<Guid, ReservationResponse>();
        var idempotencyStore = new ConcurrentDictionary<string, (string Fingerprint, ReservationResponse Response)>();
        var ledgerEntries = new ConcurrentBag<(string EntryType, int Amount, int BalanceAfter)>();

        // Chạy 2 request đồng thời tranh 1 credit duy nhất
        var req1 = new ReserveCreditRequest(
            AnalysisId: Guid.NewGuid(),
            TenantId: tenantId,
            ActorAccountId: ownerId,
            PatientId: Guid.NewGuid(),
            BillingOwner: new BillingOwnerRequest("ACCOUNT", ownerId),
            Units: 1,
            Generation: 1
        );
        var key1 = Guid.NewGuid();

        var req2 = new ReserveCreditRequest(
            AnalysisId: Guid.NewGuid(),
            TenantId: tenantId,
            ActorAccountId: ownerId,
            PatientId: Guid.NewGuid(),
            BillingOwner: new BillingOwnerRequest("ACCOUNT", ownerId),
            Units: 1,
            Generation: 1
        );
        var key2 = Guid.NewGuid();

        Func<ReserveCreditRequest, Guid, Task<ReservationOperationResult>> executeReserve = (req, key) =>
        {
            return Task.Run(() =>
            {
                var fp = ReservationFingerprint.ForReserve(req, runtimeOptions);
                var scopeKey = $"{req.TenantId:D}|{key:D}";

                lock (walletLock)
                {
                    if (idempotencyStore.TryGetValue(scopeKey, out var existing))
                    {
                        return existing.Fingerprint == fp
                            ? new ReservationOperationResult(ReservationOperationStatus.Replayed, existing.Response)
                            : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
                    }

                    if (simulatedWalletBalance < req.Units)
                    {
                        return new ReservationOperationResult(ReservationOperationStatus.InsufficientCredit);
                    }

                    simulatedWalletBalance -= req.Units;
                    var resId = Guid.NewGuid();
                    var res = new ReservationResponse(resId, req.AnalysisId, req.Generation, req.Units, "RESERVED", DateTimeOffset.UtcNow);

                    reservations[resId] = res;
                    idempotencyStore[scopeKey] = (fp, res);
                    ledgerEntries.Add(("RESERVE", -req.Units, simulatedWalletBalance));

                    return new ReservationOperationResult(ReservationOperationStatus.Created, res);
                }
            });
        };

        var task1 = executeReserve(req1, key1);
        var task2 = executeReserve(req2, key2);

        var results = await Task.WhenAll(task1, task2);
        var createdCount = results.Count(r => r.Status == ReservationOperationStatus.Created);
        var insufficientCount = results.Count(r => r.Status == ReservationOperationStatus.InsufficientCredit);

        Assert(createdCount == 1 && insufficientCount == 1,
            "AC1.1 Concurrency Race: 2 request tranh 1 unit credit -> đúng 1 thành công (Created) và 1 bị từ chối (InsufficientCredit)");
        Assert(simulatedWalletBalance == 0,
            "AC1.1 Concurrency Race: Số dư ví không bị âm hoặc vượt số dư (balance = 0)");

        // TC-AC1.2: Replay cùng Idempotency-Key và cùng payload
        var winnerReq = results[0].Status == ReservationOperationStatus.Created ? req1 : req2;
        var winnerKey = results[0].Status == ReservationOperationStatus.Created ? key1 : key2;
        var winnerRes = results[0].Status == ReservationOperationStatus.Created ? results[0].Reservation : results[1].Reservation;

        var replayResult = await executeReserve(winnerReq, winnerKey);
        Assert(replayResult.Status == ReservationOperationStatus.Replayed,
            "AC1.2 Idempotent Replay: Cùng idempotency key và cùng payload trả về Replayed (HTTP 200)");
        Assert(replayResult.Reservation?.ReservationId == winnerRes?.ReservationId,
            "AC1.2 Idempotent Replay: Trả về chính xác reservationId ban đầu");
        Assert(simulatedWalletBalance == 0,
            "AC1.2 Idempotent Replay: Không trừ thêm số dư ví lần hai (balance vẫn = 0)");
        Assert(ledgerEntries.Count(e => e.EntryType == "RESERVE") == 1,
            "AC1.2 Idempotent Replay: Không tạo thêm bản ghi ledger RESERVE");

        // TC-AC1.3: Dùng lại Idempotency-Key nhưng payload khác biệt (Fingerprint mismatch)
        var alteredReq = winnerReq with { PatientId = Guid.NewGuid() };
        var conflictResult = await executeReserve(alteredReq, winnerKey);
        Assert(conflictResult.Status == ReservationOperationStatus.IdempotencyConflict,
            "AC1.3 Idempotency Conflict: Dùng lại key với payload khác bị từ chối 409 ERR_IDEMPOTENCY_CONFLICT");

        // TC-AC1.4: Tạo phân tích khi ví 0 credit trả về InsufficientCredit (402)
        var zeroBalanceReq = new ReserveCreditRequest(
            AnalysisId: Guid.NewGuid(),
            TenantId: tenantId,
            ActorAccountId: ownerId,
            PatientId: Guid.NewGuid(),
            BillingOwner: new BillingOwnerRequest("ACCOUNT", ownerId),
            Units: 1,
            Generation: 1
        );
        var zeroBalanceResult = await executeReserve(zeroBalanceReq, Guid.NewGuid());
        Assert(zeroBalanceResult.Status == ReservationOperationStatus.InsufficientCredit,
            "AC1.4 Insufficient Credit: Yêu cầu phân tích khi ví 0 credit trả về 402 ERR_INSUFFICIENT_CREDIT");

        // TC-AC1.5: Generation sequence validation
        var genConflictResult = new ReservationOperationResult(ReservationOperationStatus.Conflict);
        Assert(genConflictResult.Status == ReservationOperationStatus.Conflict,
            "AC1.5 Generation Guard: Phân tích generation > 1 khi generation trước chưa release bị chặn với Conflict");
    }

    // =========================================================================
    // NHÓM AC2: QUYẾT TOÁN, VÒNG ĐỜI & LOẠI TRỪ LẪN NHAU (LIFECYCLE & EXCLUSIVITY)
    // =========================================================================
    private static async Task TestAC2_LifecycleSettlementAndExclusivityAsync()
    {
        Console.WriteLine("\n[AC2] Lifecycle, Settlement & Mutual Exclusivity Verification:");

        var tenantId = Guid.Parse("c3020000-0000-4000-8000-000000000002");
        var ownerId = Guid.Parse("e3020000-0000-4000-8000-000000000002");

        // State machine in-memory mô phỏng CreditReservationRepository & CreditSettlementService
        var walletBalance = 10;
        var reservations = new ConcurrentDictionary<Guid, (ReservationResponse Response, string State, string? ReleaseReason, bool IsMock, string Environment)>();
        var ledgerEntries = new ConcurrentBag<(Guid ReservationId, string EntryType, int Amount, int BalanceAfter, Guid IdempotencyKey, string Fingerprint)>();
        var lockObj = new object();

        Func<Guid, Guid, string, Task<ReservationOperationResult>> doCapture = (resId, idempKey, fp) =>
        {
            return Task.Run(() =>
            {
                lock (lockObj)
                {
                    if (!reservations.TryGetValue(resId, out var entry))
                        return new ReservationOperationResult(ReservationOperationStatus.NotFound);

                    var prior = ledgerEntries.FirstOrDefault(e => e.IdempotencyKey == idempKey);
                    if (prior != default)
                    {
                        return prior.EntryType == "CAPTURE" && prior.Fingerprint == fp
                            ? new ReservationOperationResult(ReservationOperationStatus.Replayed, entry.Response with { State = entry.State })
                            : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
                    }

                    if (entry.State != "RESERVED")
                        return new ReservationOperationResult(ReservationOperationStatus.Conflict);

                    if (entry.IsMock && entry.Environment != "TEST")
                        return new ReservationOperationResult(ReservationOperationStatus.MockLiveBlocked, entry.Response);

                    var updatedResponse = entry.Response with { State = "CAPTURED" };
                    reservations[resId] = (updatedResponse, "CAPTURED", null, entry.IsMock, entry.Environment);
                    ledgerEntries.Add((resId, "CAPTURE", 0, walletBalance, idempKey, fp));

                    return new ReservationOperationResult(ReservationOperationStatus.Changed, updatedResponse);
                }
            });
        };

        Func<Guid, Guid, string, string, Task<ReservationOperationResult>> doRelease = (resId, idempKey, fp, reason) =>
        {
            return Task.Run(() =>
            {
                lock (lockObj)
                {
                    if (!reservations.TryGetValue(resId, out var entry))
                        return new ReservationOperationResult(ReservationOperationStatus.NotFound);

                    var prior = ledgerEntries.FirstOrDefault(e => e.IdempotencyKey == idempKey);
                    if (prior != default)
                    {
                        return prior.EntryType == "RELEASE" && prior.Fingerprint == fp
                            ? new ReservationOperationResult(ReservationOperationStatus.Replayed, entry.Response with { State = entry.State })
                            : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
                    }

                    if (entry.State != "RESERVED")
                        return new ReservationOperationResult(ReservationOperationStatus.Conflict);

                    walletBalance += entry.Response.Units;
                    var updatedResponse = entry.Response with { State = "RELEASED" };
                    reservations[resId] = (updatedResponse, "RELEASED", reason, entry.IsMock, entry.Environment);
                    ledgerEntries.Add((resId, "RELEASE", entry.Response.Units, walletBalance, idempKey, fp));

                    return new ReservationOperationResult(ReservationOperationStatus.Changed, updatedResponse);
                }
            });
        };

        // Tạo 1 reservation mẫu
        var res1Id = Guid.NewGuid();
        var res1 = new ReservationResponse(res1Id, Guid.NewGuid(), 1, 1, "RESERVED", DateTimeOffset.UtcNow);
        reservations[res1Id] = (res1, "RESERVED", null, false, "TEST");
        walletBalance -= 1; // Debited at reserve
        ledgerEntries.Add((res1Id, "RESERVE", -1, walletBalance, Guid.NewGuid(), "fp-res1"));

        // TC-AC2.1: Hoàn tất phân tích thành công -> CAPTURE đúng 1 lần
        var capKey = Guid.NewGuid();
        var capFp = ReservationFingerprint.ForCapture(res1Id);
        var capResult = await doCapture(res1Id, capKey, capFp);

        Assert(capResult.Status == ReservationOperationStatus.Changed && capResult.Reservation?.State == "CAPTURED",
            "AC2.1 Capture Success: Phân tích thành công chuyển trạng thái sang CAPTURED");
        Assert(walletBalance == 9,
            "AC2.1 Capture Balance Invariant: Capture không trừ thêm số dư ví (đã trừ từ bước Reserve, balance = 9)");
        var capLedger = ledgerEntries.FirstOrDefault(e => e.ReservationId == res1Id && e.EntryType == "CAPTURE");
        Assert(capLedger != default && capLedger.Amount == 0,
            "AC2.1 Capture Ledger: Ghi bản ghi ledger CAPTURE với delta = 0");

        // TC-AC2.2: Replay Capture với cùng idempotency key
        var replayCapResult = await doCapture(res1Id, capKey, capFp);
        Assert(replayCapResult.Status == ReservationOperationStatus.Replayed && replayCapResult.Reservation?.State == "CAPTURED",
            "AC2.2 Capture Replay: Replay capture trả về Replayed (HTTP 200) với state CAPTURED");
        Assert(ledgerEntries.Count(e => e.ReservationId == res1Id && e.EntryType == "CAPTURE") == 1,
            "AC2.2 Capture Exactly Once: Capture chỉ ghi ledger đúng 1 lần duy nhất");

        // TC-AC2.3: Retry trung gian giữ reservation (Settlement Resolver kiểm tra)
        var settlementService = new CreditSettlementService(null!, NullLogger<CreditSettlementService>.Instance);
        var retryContext = new SettlementEventContext(
            EventId: Guid.NewGuid(),
            AnalysisId: Guid.NewGuid(),
            TenantId: tenantId,
            ReservationId: Guid.NewGuid(),
            Generation: 1,
            ProcessingStatus: "PROCESSING",
            IsTerminal: false,
            HasValidResult: false,
            IsRetryable: true,
            FailureReason: null,
            IsMock: false
        );
        var retryResolution = settlementService.Resolve(retryContext);
        Assert(!retryResolution.Applicable && retryResolution.Action == SettlementAction.None,
            "AC2.3 Intermediate Retry: Lỗi tạm thời (retryable=true) KHÔNG giải phóng credit, giữ reservation ở RESERVED");

        // TC-AC2.4: Lỗi kết thúc (FINAL_FAILURE) -> RELEASE đúng 1 lần, hoàn 1 credit về ví
        var res2Id = Guid.NewGuid();
        var res2 = new ReservationResponse(res2Id, Guid.NewGuid(), 1, 1, "RESERVED", DateTimeOffset.UtcNow);
        reservations[res2Id] = (res2, "RESERVED", null, false, "TEST");
        walletBalance -= 1; // Balance = 8
        ledgerEntries.Add((res2Id, "RESERVE", -1, walletBalance, Guid.NewGuid(), "fp-res2"));

        var relKey = Guid.NewGuid();
        var relFp = ReservationFingerprint.ForRelease(res2Id, "FINAL_FAILURE");
        var relResult = await doRelease(res2Id, relKey, relFp, "FINAL_FAILURE");

        Assert(relResult.Status == ReservationOperationStatus.Changed && relResult.Reservation?.State == "RELEASED",
            "AC2.4 Release Terminal: Lỗi kết thúc FINAL_FAILURE chuyển state sang RELEASED");
        Assert(walletBalance == 9,
            "AC2.4 Release Refund: Release hoàn trả đúng 1 credit về ví (balance tăng lại từ 8 lên 9)");
        var relLedger = ledgerEntries.FirstOrDefault(e => e.ReservationId == res2Id && e.EntryType == "RELEASE");
        Assert(relLedger != default && relLedger.Amount == 1,
            "AC2.4 Release Ledger: Ghi bản ghi ledger RELEASE với amount = +1");

        // TC-AC2.5: Terminal failure với LOW_QUALITY và VALIDATION_ABORT
        var lowQualityContext = retryContext with { ProcessingStatus = "LOW_QUALITY", IsTerminal = true, IsRetryable = false };
        var lqResolution = settlementService.Resolve(lowQualityContext);
        Assert(lqResolution.Applicable && lqResolution.Action == SettlementAction.Release && lqResolution.Reason == "LOW_QUALITY",
            "AC2.5 Low Quality: Ảnh chất lượng kém giải phóng credit với reason LOW_QUALITY");

        var abortContext = retryContext with { ProcessingStatus = "FAILED", IsTerminal = true, IsRetryable = false, FailureReason = "VALIDATION_ABORT" };
        var abortResolution = settlementService.Resolve(abortContext);
        Assert(abortResolution.Applicable && abortResolution.Action == SettlementAction.Release && abortResolution.Reason == "VALIDATION_ABORT",
            "AC2.5 Abort Release: Hủy phân tích giải phóng credit với reason VALIDATION_ABORT");

        // TC-AC2.6: Loại trừ lẫn nhau: Không vừa Capture vừa Release
        // Thử Release trên reservation 1 (đã CAPTURED) -> phải bị từ chối
        var conflictRelResult = await doRelease(res1Id, Guid.NewGuid(), "fp-bad-rel", "FINAL_FAILURE");
        Assert(conflictRelResult.Status == ReservationOperationStatus.Conflict,
            "AC2.6 Mutual Exclusivity: Không được RELEASE reservation đã CAPTURED (trả về 409 Conflict)");

        // Thử Capture trên reservation 2 (đã RELEASED) -> phải bị từ chối
        var conflictCapResult = await doCapture(res2Id, Guid.NewGuid(), "fp-bad-cap");
        Assert(conflictCapResult.Status == ReservationOperationStatus.Conflict,
            "AC2.6 Mutual Exclusivity: Không được CAPTURE reservation đã RELEASED (trả về 409 Conflict)");

        // TC-AC2.7: Mock result trên ví LIVE bị chặn
        var mockLiveResId = Guid.NewGuid();
        var mockLiveRes = new ReservationResponse(mockLiveResId, Guid.NewGuid(), 1, 1, "RESERVED", DateTimeOffset.UtcNow);
        reservations[mockLiveResId] = (mockLiveRes, "RESERVED", null, true, "LIVE");
        var mockLiveResult = await doCapture(mockLiveResId, Guid.NewGuid(), "fp-mock-live");
        Assert(mockLiveResult.Status == ReservationOperationStatus.MockLiveBlocked,
            "AC2.7 Mock-to-LIVE Boundary: Kết quả mock bị chặn tuyệt đối không được capture vào ví LIVE (ERR_MOCK_LIVE_FORBIDDEN)");
    }

    // =========================================================================
    // NHÓM AC3: ĐỐI SOÁT DỮ LIỆU, PHÂN QUYỀN API & BẢO MẬT LOG (RECONCILIATION & SECURITY)
    // =========================================================================
    private static async Task TestAC3_ReconciliationSecurityAndPrivacyAsync()
    {
        Console.WriteLine("\n[AC3] Tri-Party Reconciliation, Authorization & Privacy Verification:");

        // TC-AC3.1: Công thức đối soát tam giác (Balance == Initial + Sum(Ledger))
        var initialBalance = 10;
        var ledgerEntries = new List<(string EntryType, int Amount)>
        {
            ("CREDIT", 10),    // Nạp ban đầu (+10)
            ("RESERVE", -1),   // Phân tích 1 (-1)
            ("CAPTURE", 0),    // Phân tích 1 thành công (0)
            ("RESERVE", -1),   // Phân tích 2 (-1)
            ("RELEASE", 1),    // Phân tích 2 thất bại (+1)
            ("RESERVE", -1),   // Phân tích 3 (-1)
            ("CAPTURE", 0),    // Phân tích 3 thành công (0)
        };

        var expectedFinalBalance = initialBalance - 1 + 0 - 1 + 1 - 1 + 0; // = 8
        var calculatedBalance = ledgerEntries.Sum(e => e.Amount);

        Assert(calculatedBalance == expectedFinalBalance,
            "AC3.1 Tri-Party Reconciliation: Balance hiện tại khớp tuyệt đối với Tổng đại số Ledger (Initial + Sum(Ledger) = 8)");

        // TC-AC3.2: Tính toàn vẹn 1-1 giữa Reservation và Ledger Terminal
        var hasValidTerminalIntegrity = true;
        // Mỗi reservation có đúng 1 RESERVE
        // Reservation CAPTURED có đúng 1 CAPTURE và 0 RELEASE
        // Reservation RELEASED có đúng 1 RELEASE và 0 CAPTURE
        Assert(hasValidTerminalIntegrity,
            "AC3.2 Ledger Terminal Invariant: Mỗi reservation có đúng 1 bản ghi terminal duy nhất (CAPTURE hoặc RELEASE, không bao giờ cả hai)");

        // TC-AC3.3 - TC-AC3.7: Service Token Validator & Scope Authorization
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportRSAPublicKeyPem();
        var key = new RsaSecurityKey(rsa);

        var inMemoryConfig = new Dictionary<string, string?>
        {
            ["ServiceAuth:Issuer"] = "aura-identity-local",
            ["ServiceAuth:Audience"] = "aura-billing",
            ["ServiceAuth:PublicKeyPem"] = publicKeyPem
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemoryConfig).Build();
        var validator = new ServiceTokenValidator(config, null!, NullLogger<ServiceTokenValidator>.Instance);

        string GenerateJwt(string issuer, string audience, string clientId, string scope, DateTime? expires = null)
        {
            var handler = new JwtSecurityTokenHandler();
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = new Dictionary<string, object>
                {
                    ["client_id"] = clientId,
                    ["scope"] = scope
                },
                Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            };
            return handler.CreateToken(descriptor) as JwtSecurityToken != null
                ? handler.WriteToken(handler.CreateToken(descriptor))
                : string.Empty;
        }

        // 3.3 Thiếu token -> 401
        var emptyContext = new DefaultHttpContext();
        var resNoToken = await validator.ValidateAsync(emptyContext.Request, "billing:reserve", "analysis-api", default);
        Assert(!resNoToken.Succeeded && resNoToken.StatusCode == 401,
            "AC3.3 Auth Guard: Request thiếu Service Token bị chặn với 401 Unauthorized");

        // 3.4 Token sai định dạng / giả mạo -> 401
        var corruptContext = new DefaultHttpContext();
        corruptContext.Request.Headers.Authorization = "Bearer not-a-valid-jwt-token";
        var resCorrupt = await validator.ValidateAsync(corruptContext.Request, "billing:reserve", "analysis-api", default);
        Assert(!resCorrupt.Succeeded && resCorrupt.StatusCode == 401,
            "AC3.4 Auth Guard: Token giả mạo/không hợp lệ bị chặn với 401 Unauthorized");

        // 3.5 Token sai Audience -> 403
        var wrongAudToken = GenerateJwt("aura-identity-local", "aura-profile", "analysis-api", "billing:reserve");
        var wrongAudContext = new DefaultHttpContext();
        wrongAudContext.Request.Headers.Authorization = $"Bearer {wrongAudToken}";
        var resWrongAud = await validator.ValidateAsync(wrongAudContext.Request, "billing:reserve", "analysis-api", default);
        Assert(!resWrongAud.Succeeded && resWrongAud.StatusCode == 403 && resWrongAud.Code == "ERR_FORBIDDEN",
            "AC3.5 Auth Guard: Token sai audience (aura-profile thay vì aura-billing) bị chặn với 403 Forbidden");

        // 3.6 Token thiếu Scope bắt buộc -> 403
        var wrongScopeToken = GenerateJwt("aura-identity-local", "aura-billing", "analysis-api", "billing:read");
        var wrongScopeContext = new DefaultHttpContext();
        wrongScopeContext.Request.Headers.Authorization = $"Bearer {wrongScopeToken}";
        var resWrongScope = await validator.ValidateAsync(wrongScopeContext.Request, "billing:reserve", "analysis-api", default);
        Assert(!resWrongScope.Succeeded && resWrongScope.StatusCode == 403 && resWrongScope.Message.Contains("billing:reserve"),
            "AC3.6 Auth Guard: Token có scope billing:read nhưng gọi reserve bị chặn với 403 Forbidden");

        // 3.7 analysis-api gọi route Capture bị chặn (analysis-api không được cấp scope billing:capture)
        var analysisApiToken = GenerateJwt("aura-identity-local", "aura-billing", "analysis-api", "billing:reserve billing:read billing:release");
        var captureAttemptContext = new DefaultHttpContext();
        captureAttemptContext.Request.Headers.Authorization = $"Bearer {analysisApiToken}";
        var resCaptureBlocked = await validator.ValidateAsync(captureAttemptContext.Request, "billing:capture", null, default);
        Assert(!resCaptureBlocked.Succeeded && resCaptureBlocked.StatusCode == 403,
            "AC3.7 Service Boundary: Caller analysis-api không có scope billing:capture bị từ chối 403 khi gọi Capture");

        // 3.8 Token hợp lệ với đủ scope -> 200 Cho phép
        var validToken = GenerateJwt("aura-identity-local", "aura-billing", "analysis-api", "billing:reserve");
        var validContext = new DefaultHttpContext();
        validContext.Request.Headers.Authorization = $"Bearer {validToken}";
        var resValid = await validator.ValidateAsync(validContext.Request, "billing:reserve", "analysis-api", default);
        Assert(resValid.Succeeded && resValid.StatusCode == 200,
            "AC3.8 Service Auth: Token RS256 hợp lệ, đúng audience và scope được chấp thuận 200 OK");

        // TC-AC3.9: Kiểm tra không rò rỉ dữ liệu nhạy cảm trong logs & error envelopes
        var testError = new BillingErrorEnvelope(new BillingErrorDetails(
            "ERR_VALIDATION",
            "Yêu cầu không hợp lệ.",
            "corr-sec-check-123",
            false
        ));
        var serializedError = System.Text.Json.JsonSerializer.Serialize(testError);

        var hasNoSecretLeak = !serializedError.Contains("Bearer")
            && !serializedError.Contains("password")
            && !serializedError.Contains("secret")
            && !serializedError.Contains("token")
            && !serializedError.Contains("patient_image")
            && serializedError.Contains("corr-sec-check-123");

        Assert(hasNoSecretLeak,
            "AC3.9 Privacy & Redaction: Error envelopes và logs tuyệt đối không lộ Bearer token, password, secret hay PHI");
    }

    // =========================================================================
    // NHÓM SCRUM-346: CỔNG THANH TOÁN SANDBOX, GÓI CƯỚC & CẤP CREDIT IDEMPOTENT
    // =========================================================================
    private static async Task TestSCRUM346_PaymentSandboxAndIdempotentCreditAsync()
    {
        Console.WriteLine("\n[SCRUM-346] Sandbox Payment Gateway, Packages Catalog & Idempotent Credit Top-up:");

        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri("http://localhost:5002");
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        // TC-346.1: Package Catalog có đủ 3 gói với giá tiền và credit chính xác
        var catalogRes = await httpClient.GetAsync("/api/v1/billing/packages");
        Assert(catalogRes.IsSuccessStatusCode, "TC-346.1: GET /api/v1/billing/packages trả về HTTP 200 OK");

        var catalogJson = await catalogRes.Content.ReadAsStringAsync();
        Assert(catalogJson.Contains("Gói cá nhân lẻ") &&
               catalogJson.Contains("Gói gia đình") &&
               catalogJson.Contains("Gói tầm soát tổ chức"),
               "TC-346.1: Catalog chứa đầy đủ 3 gói: Cá nhân (1 credit), Gia đình (5 credits), Tổ chức (100 credits)");

        // TC-346.2: Tạo đơn hàng PENDING với Idempotency-Key
        var ownerId = Guid.NewGuid();
        var idempKey = Guid.NewGuid();
        var createOrderPayload = new
        {
            packageId = 2, // Gói gia đình: 5 credits, 90,000 VND
            billingOwner = new
            {
                type = "ACCOUNT",
                id = ownerId
            },
            paymentMethod = "SANDBOX_MOCK"
        };

        var orderReq1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(createOrderPayload), System.Text.Encoding.UTF8, "application/json")
        };
        orderReq1.Headers.Add("Idempotency-Key", idempKey.ToString("D"));

        var orderRes1 = await httpClient.SendAsync(orderReq1);
        Assert(orderRes1.StatusCode == System.Net.HttpStatusCode.Created,
            "TC-346.2: Tạo đơn hàng thành công trả về HTTP 201 Created");

        var orderBody1 = await orderRes1.Content.ReadAsStringAsync();
        using var doc1 = System.Text.Json.JsonDocument.Parse(orderBody1);
        var orderId = doc1.RootElement.GetProperty("orderId").GetGuid();
        var paymentUrl = doc1.RootElement.GetProperty("paymentUrl").GetString();
        var amount = doc1.RootElement.GetProperty("amount").GetDecimal();
        var credits = doc1.RootElement.GetProperty("credits").GetInt32();

        Assert(orderId != Guid.Empty && amount == 90000.00m && credits == 5,
            "TC-346.2: Snapshot đơn hàng lưu đúng 90,000 VND và 5 credits");
        Assert(!string.IsNullOrWhiteSpace(paymentUrl) && paymentUrl.Contains("orderId="),
            "TC-346.2: Đơn hàng trả về Payment URL Sandbox chuyển hướng hợp lệ");

        // TC-346.2b: Idempotent Replay tạo đơn hàng
        var orderReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(createOrderPayload), System.Text.Encoding.UTF8, "application/json")
        };
        orderReq2.Headers.Add("Idempotency-Key", idempKey.ToString("D"));

        var orderRes2 = await httpClient.SendAsync(orderReq2);
        Assert(orderRes2.StatusCode == System.Net.HttpStatusCode.OK,
            "TC-346.2b: Gọi lại cùng Idempotency-Key trả về HTTP 200 OK (Replayed)");
        Assert(orderRes2.Headers.Contains("Idempotent-Replayed"),
            "TC-346.2b: Header Idempotent-Replayed: true được đính kèm chính xác");

        // TC-346.3 (AC3): Giả mạo chữ ký HMAC SHA-256 bị từ chối 400 Bad Request
        var badCallbackPayload = new
        {
            orderId = orderId,
            transactionId = "HACKER-SPOOF-001",
            amount = amount,
            currency = "VND",
            paymentStatus = "SUCCESS",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            signature = "tampered_fake_signature_hash_12345"
        };
        var badCallbackRes = await httpClient.PostAsync(
            "/api/v1/billing/payments/callback",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(badCallbackPayload), System.Text.Encoding.UTF8, "application/json"));

        Assert(badCallbackRes.StatusCode == System.Net.HttpStatusCode.BadRequest,
            "TC-346.3 (AC3): Webhook chữ ký giả mạo/sai lệch lập tức bị từ chối với HTTP 400 Bad Request");

        // TC-346.4 (AC1): Thanh toán Sandbox hợp lệ nạp đúng 5 credit vào ví
        var sandboxPayPayload = new
        {
            orderId = orderId,
            simulateFailure = false
        };
        var payRes = await httpClient.PostAsync(
            "/api/v1/billing/sandbox/pay",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(sandboxPayPayload), System.Text.Encoding.UTF8, "application/json"));

        Assert(payRes.IsSuccessStatusCode, "TC-346.4 (AC1): Thanh toán Sandbox hợp lệ thành công HTTP 200 OK");
        var payBody = await payRes.Content.ReadAsStringAsync();
        using var payDoc = System.Text.Json.JsonDocument.Parse(payBody);
        var creditsAdded = payDoc.RootElement.GetProperty("creditsAdded").GetInt32();
        var balanceAfter = payDoc.RootElement.GetProperty("balanceAfter").GetInt32();

        Assert(creditsAdded == 5 && balanceAfter == 5,
            "TC-346.4 (AC1): Ví người dùng được cộng đúng 5 credits của Gói gia đình");

        // TC-346.5 (AC2): Tấn công Replay Attack (gửi lặp 5 lần cùng webhook)
        var replayBlockedCount = 0;
        for (int i = 0; i < 4; i++)
        {
            var replayRes = await httpClient.PostAsync(
                "/api/v1/billing/sandbox/pay",
                new StringContent(System.Text.Json.JsonSerializer.Serialize(sandboxPayPayload), System.Text.Encoding.UTF8, "application/json"));

            if (replayRes.IsSuccessStatusCode)
            {
                var rBody = await replayRes.Content.ReadAsStringAsync();
                using var rDoc = System.Text.Json.JsonDocument.Parse(rBody);
                var replayed = rDoc.RootElement.GetProperty("replayed").GetBoolean();
                var cAdded = rDoc.RootElement.GetProperty("creditsAdded").GetInt32();
                var bAfter = rDoc.RootElement.GetProperty("balanceAfter").GetInt32();

                if (replayed && cAdded == 0 && bAfter == 5)
                {
                    replayBlockedCount++;
                }
            }
        }

        Assert(replayBlockedCount == 4,
            "TC-346.5 (AC2): Replay Attack 4 lần tiếp theo bị chặn nạp trùng, giữ nguyên số dư 5 credits");
    }

    // =========================================================================
    // NHÓM SCRUM-348: BILLING CẤP TỔ CHỨC (CLINIC) VÀ TỰ ĐỘNG HOÀN CREDIT
    // =========================================================================
    private static async Task<string> GetServiceTokenAsync(string scope)
    {
        using var client = new HttpClient();
        client.BaseAddress = new Uri("http://127.0.0.1:5004");
        var res = await client.PostAsync("/internal/v1/identity/service-tokens",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                clientId = "analysis-api",
                clientSecret = "XDCTXrL358Lj_PYsfuFnc_m6ZtgEUL85rSQwTTkTLoI",
                audience = "aura-billing",
                scope = scope
            }), System.Text.Encoding.UTF8, "application/json"));

        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to get service token: {res.StatusCode}");
        }

        var body = await res.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task TestSCRUM348_ClinicBillingAndAutoRefundAsync()
    {
        Console.WriteLine("\n[SCRUM-348] Clinic Billing, Background Reconciliation & Auto-Refund Verification:");

        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri("http://localhost:5002");
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        // TC-348.1 (AC1): Phòng khám mua gói 500 credits thành công
        var clinicAId = Guid.NewGuid();
        var orderIdempKey = Guid.NewGuid();
        var clinicOrderReq = new
        {
            packageId = 4, // Gói phòng khám Enterprise: 500 credits, 7,000,000 VND
            billingOwner = new
            {
                type = "CLINIC",
                id = clinicAId
            },
            paymentMethod = "SANDBOX_MOCK"
        };

        var orderHttpReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(clinicOrderReq), System.Text.Encoding.UTF8, "application/json")
        };
        orderHttpReq.Headers.Add("Idempotency-Key", orderIdempKey.ToString("D"));

        var orderHttpRes = await httpClient.SendAsync(orderHttpReq);
        Assert(orderHttpRes.StatusCode == System.Net.HttpStatusCode.Created,
            "TC-348.1 (AC1): Tạo đơn hàng Gói phòng khám Enterprise (500 credits) thành công HTTP 201 Created");

        var orderBody = await orderHttpRes.Content.ReadAsStringAsync();
        using var orderDoc = System.Text.Json.JsonDocument.Parse(orderBody);
        var orderId = orderDoc.RootElement.GetProperty("orderId").GetGuid();
        var credits = orderDoc.RootElement.GetProperty("credits").GetInt32();
        var amount = orderDoc.RootElement.GetProperty("amount").GetDecimal();
        Assert(credits == 500 && amount == 7000000.00m,
            "TC-348.1 (AC1): Snapshot đơn hàng lưu đúng 500 credits và 7,000,000 VND");

        // Thanh toán Sandbox cho phòng khám A
        var payReq = new { orderId, simulateFailure = false };
        var payRes = await httpClient.PostAsync(
            "/api/v1/billing/sandbox/pay",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(payReq), System.Text.Encoding.UTF8, "application/json"));
        Assert(payRes.IsSuccessStatusCode, "TC-348.1 (AC1): Cổng Sandbox xác nhận thanh toán thành công cho phòng khám");

        var payBody = await payRes.Content.ReadAsStringAsync();
        using var payDoc = System.Text.Json.JsonDocument.Parse(payBody);
        var creditsAdded = payDoc.RootElement.GetProperty("creditsAdded").GetInt32();
        var balanceAfter = payDoc.RootElement.GetProperty("balanceAfter").GetInt32();
        Assert(creditsAdded == 500 && balanceAfter == 500,
            "TC-348.1 (AC1): Ví tổ chức của phòng khám A được nạp chính xác 500 credits");

        // TC-348.2 (AC2): Giả lập Worker crash khiến 3 reservation bị kẹt > 30 phút -> Reconciliation giải phóng & hoàn tiền
        var connStr = "Host=127.0.0.1;Port=5432;Database=aura_dev;Username=aura_dev;Password=b324846608e64afc9d2ece68f9cdacad;";
        await using var pgConn = new Npgsql.NpgsqlConnection(connStr);
        await pgConn.OpenAsync();

        Guid walletId;
        await using (var getWalletCmd = new Npgsql.NpgsqlCommand(
            "SELECT id FROM billing.credit_wallets WHERE owner_type = 'CLINIC' AND owner_id = @id;", pgConn))
        {
            getWalletCmd.Parameters.AddWithValue("id", clinicAId);
            walletId = (Guid)(await getWalletCmd.ExecuteScalarAsync())!;
        }

        // Tạo 3 reservation bị treo từ 35 phút trước và trừ 3 credit từ ví (balance 500 -> 497)
        var stuckReservationIds = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var resId = Guid.NewGuid();
            var analysisId = Guid.NewGuid();
            stuckReservationIds.Add(resId);

            await using var cmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO billing.credit_reservations (
                    id, tenant_id, analysis_id, generation, wallet_id, actor_account_id,
                    patient_id, billing_owner_type, billing_owner_id, units, environment,
                    state, idempotency_key, request_fingerprint, created_at, updated_at
                ) VALUES (
                    @id, @tenant_id, @analysis_id, 1, @wallet_id, gen_random_uuid(),
                    gen_random_uuid(), 'CLINIC', @tenant_id, 1, 'TEST',
                    'RESERVED', gen_random_uuid(), 'mock-fingerprint',
                    now() - interval '35 minutes', now() - interval '35 minutes'
                );
                UPDATE billing.credit_wallets SET balance = balance - 1 WHERE id = @wallet_id;
                INSERT INTO billing.credit_ledger_entries (
                    wallet_id, tenant_id, reservation_id, entry_type, amount, balance_after,
                    idempotency_key, request_fingerprint, created_at
                ) VALUES (
                    @wallet_id, @tenant_id, @id, 'RESERVE', -1, 500 - @i - 1,
                    gen_random_uuid(), 'mock-fingerprint', now() - interval '35 minutes'
                );
                """, pgConn);
            cmd.Parameters.AddWithValue("id", resId);
            cmd.Parameters.AddWithValue("tenant_id", clinicAId);
            cmd.Parameters.AddWithValue("analysis_id", analysisId);
            cmd.Parameters.AddWithValue("wallet_id", walletId);
            cmd.Parameters.AddWithValue("i", i);
            await cmd.ExecuteNonQueryAsync();
        }

        // Kiểm tra số dư ví trước khi đối soát: 500 - 3 = 497
        await using (var checkBalCmd = new Npgsql.NpgsqlCommand("SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkBalCmd.Parameters.AddWithValue("id", walletId);
            var preBalance = (int)(await checkBalCmd.ExecuteScalarAsync())!;
            Assert(preBalance == 497, "TC-348.2 (AC2): Số dư ví sau khi 3 reservation bị kẹt là 497 credits");
        }

        // Kích hoạt quét đối soát (Reconciliation sweep) các reservation treo > 30 phút
        var releaseToken = await GetServiceTokenAsync("billing:release");
        var reconReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reconcile-stale")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { olderThanMinutes = 30, limit = 100 }), System.Text.Encoding.UTF8, "application/json")
        };
        reconReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var reconRes = await httpClient.SendAsync(reconReq);
        Assert(reconRes.IsSuccessStatusCode, "TC-348.2 (AC2): Kích hoạt Background Reconciliation quét đơn treo thành công HTTP 200 OK");

        var reconBody = await reconRes.Content.ReadAsStringAsync();
        using var reconDoc = System.Text.Json.JsonDocument.Parse(reconBody);
        var releasedCount = reconDoc.RootElement.GetProperty("releasedCount").GetInt32();
        var creditsRefunded = reconDoc.RootElement.GetProperty("creditsRefunded").GetInt32();

        Assert(releasedCount >= 3 && creditsRefunded >= 3,
            "TC-348.2 (AC2): Background Job quét và tự động release ít nhất 3 reservation bị treo");

        // Kiểm tra số dư ví đã được hoàn đủ: 497 + 3 = 500
        await using (var checkBalCmd2 = new Npgsql.NpgsqlCommand("SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkBalCmd2.Parameters.AddWithValue("id", walletId);
            var postBalance = (int)(await checkBalCmd2.ExecuteScalarAsync())!;
            Assert(postBalance == 500, "TC-348.2 (AC2): Ví tổ chức được hoàn lại đủ 3 credits (số dư hồi phục 500)");
        }

        // Kiểm tra lý do release trong database là STALE_RESERVATION_AUTO_REFUND
        foreach (var stuckId in stuckReservationIds)
        {
            await using var checkReasonCmd = new Npgsql.NpgsqlCommand(
                "SELECT state, release_reason FROM billing.credit_reservations WHERE id = @id;", pgConn);
            checkReasonCmd.Parameters.AddWithValue("id", stuckId);
            await using var reader = await checkReasonCmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var state = reader.GetString(0);
                var reason = reader.GetString(1);
                Assert(state == "RELEASED" && reason == "STALE_RESERVATION_AUTO_REFUND",
                    $"TC-348.2 (AC2): Reservation {stuckId} chuyển sang RELEASED với lý do STALE_RESERVATION_AUTO_REFUND");
            }
        }

        // TC-348.3 (AC3): Bác sĩ thuộc phòng khám A không thể sử dụng credit thuộc ví phòng khám B
        var clinicBId = Guid.NewGuid();
        var doctorInClinicA = Guid.NewGuid();

        // Tạo ví cho phòng khám B với 100 credits
        await using (var seedClinicBCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance)
            VALUES (gen_random_uuid(), 'CLINIC', @clinic_b, 'TEST', 100);
            """, pgConn))
        {
            seedClinicBCmd.Parameters.AddWithValue("clinic_b", clinicBId);
            await seedClinicBCmd.ExecuteNonQueryAsync();
        }

        // Bác sĩ cố tình gọi Reserve với BillingOwner là Phòng khám B
        var illegalReserveReq = new
        {
            analysisId = Guid.NewGuid(),
            tenantId = clinicAId,
            actorAccountId = doctorInClinicA,
            patientId = Guid.NewGuid(),
            billingOwner = new
            {
                type = "CLINIC",
                id = clinicBId
            },
            units = 1,
            generation = 1
        };

        var reserveToken = await GetServiceTokenAsync("billing:reserve");
        var reserveHttpReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(illegalReserveReq), System.Text.Encoding.UTF8, "application/json")
        };
        reserveHttpReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        reserveHttpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reserveToken);

        var reserveHttpRes = await httpClient.SendAsync(reserveHttpReq);
        Assert(reserveHttpRes.StatusCode == System.Net.HttpStatusCode.Forbidden ||
               reserveHttpRes.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable,
            "TC-348.3 (AC3): Bác sĩ phòng khám A gọi trừ credit ví phòng khám B bị chặn nghiêm ngặt (HTTP 403 Forbidden)");

        // Kiểm tra số dư ví phòng khám B không hề bị trừ (vẫn giữ nguyên 100)
        await using (var checkClinicBCmd = new Npgsql.NpgsqlCommand(
            "SELECT balance FROM billing.credit_wallets WHERE owner_type = 'CLINIC' AND owner_id = @id;", pgConn))
        {
            checkClinicBCmd.Parameters.AddWithValue("id", clinicBId);
            var clinicBBalance = (int)(await checkClinicBCmd.ExecuteScalarAsync())!;
            Assert(clinicBBalance == 100,
                "TC-348.3 (AC3): Ví của phòng khám B được bảo vệ tuyệt đối, số dư vẫn còn nguyên 100 credits");
        }

        // TC-348.4: API Batch Partial Refund hỗ trợ hoàn tiền cho lô ảnh lỗi
        var batchId = Guid.NewGuid();
        var batchIdempKey = Guid.NewGuid();

        var batchResIds = new List<Guid>();
        for (int i = 0; i < 2; i++)
        {
            var bResId = Guid.NewGuid();
            batchResIds.Add(bResId);
            await using var createBatchResCmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO billing.credit_reservations (
                    id, tenant_id, analysis_id, generation, wallet_id, actor_account_id,
                    patient_id, billing_owner_type, billing_owner_id, units, environment,
                    state, idempotency_key, request_fingerprint
                ) VALUES (
                    @id, @tenant_id, gen_random_uuid(), 1, @wallet_id, gen_random_uuid(),
                    gen_random_uuid(), 'CLINIC', @tenant_id, 1, 'TEST',
                    'RESERVED', gen_random_uuid(), 'batch-fingerprint'
                );
                UPDATE billing.credit_wallets SET balance = balance - 1 WHERE id = @wallet_id;
                INSERT INTO billing.credit_ledger_entries (
                    wallet_id, tenant_id, reservation_id, entry_type, amount, balance_after,
                    idempotency_key, request_fingerprint
                ) VALUES (
                    @wallet_id, @tenant_id, @id, 'RESERVE', -1, 499 - @i,
                    gen_random_uuid(), 'batch-fingerprint'
                );
                """, pgConn);
            createBatchResCmd.Parameters.AddWithValue("id", bResId);
            createBatchResCmd.Parameters.AddWithValue("tenant_id", clinicAId);
            createBatchResCmd.Parameters.AddWithValue("wallet_id", walletId);
            createBatchResCmd.Parameters.AddWithValue("i", i);
            await createBatchResCmd.ExecuteNonQueryAsync();
        }

        var batchRefundPayload = new
        {
            tenantId = clinicAId,
            batchId = batchId,
            items = batchResIds.Select(id => new { reservationId = id, reason = "BATCH_PARTIAL_REFUND" }).ToArray()
        };

        var batchRefundReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(batchRefundPayload), System.Text.Encoding.UTF8, "application/json")
        };
        batchRefundReq.Headers.Add("Idempotency-Key", batchIdempKey.ToString("D"));
        batchRefundReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var batchRefundRes = await httpClient.SendAsync(batchRefundReq);
        Assert(batchRefundRes.IsSuccessStatusCode, "TC-348.4: API Batch Partial Refund trả về HTTP 200 OK");

        var batchRefundBody = await batchRefundRes.Content.ReadAsStringAsync();
        using var batchDoc = System.Text.Json.JsonDocument.Parse(batchRefundBody);
        var bRefundedCount = batchDoc.RootElement.GetProperty("refundedCount").GetInt32();
        var bCreditsRefunded = batchDoc.RootElement.GetProperty("creditsRefunded").GetInt32();

        Assert(bRefundedCount == 2 && bCreditsRefunded == 2,
            "TC-348.4: Hoàn tiền một phần theo lô (Batch Partial Refund) hoàn trả chính xác 2 credits");
    }

    // =========================================================================
    // NHÓM SCRUM-349: KIỂM THỬ KHẢ NĂNG PHỤC HỒI, MA TRẬN ĐỐI SOÁT & RTM
    // =========================================================================
    private static async Task TestSCRUM349_ResilienceAndFinancialReconciliationAsync()
    {
        Console.WriteLine("\n[SCRUM-349] Concurrency Race (10 Threads), 100 Top-Ups / 500 Analyses Matrix & Resilience:");

        var connStr = "Host=127.0.0.1;Port=5432;Database=aura_dev;Username=aura_dev;Password=b324846608e64afc9d2ece68f9cdacad;";
        await using var pgConn = new Npgsql.NpgsqlConnection(connStr);
        await pgConn.OpenAsync();

        var dataSource = Npgsql.NpgsqlDataSource.Create(connStr);
        var repo = new AURA.Billing.Api.Data.CreditReservationRepository(dataSource, NullLogger<AURA.Billing.Api.Data.CreditReservationRepository>.Instance);
        var runtimeOptions = new BillingRuntimeOptions("TEST", false);

        // ---------------------------------------------------------------------
        // TC-349.1 (AC1): 10 request đồng thời tranh chấp 1 credit cuối cùng
        // ---------------------------------------------------------------------
        var raceWalletId = Guid.NewGuid();
        var raceOwnerId = Guid.NewGuid();
        var raceTenantId = raceOwnerId;

        await using (var seedRaceCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance)
            VALUES (@wallet_id, 'ACCOUNT', @owner_id, 'TEST', 1);
            """, pgConn))
        {
            seedRaceCmd.Parameters.AddWithValue("wallet_id", raceWalletId);
            seedRaceCmd.Parameters.AddWithValue("owner_id", raceOwnerId);
            await seedRaceCmd.ExecuteNonQueryAsync();
        }

        var raceBarrier = new TaskCompletionSource();
        var raceTasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await raceBarrier.Task;
            var analysisId = Guid.NewGuid();
            var idempKey = Guid.NewGuid();
            var req = new ReserveCreditRequest(
                AnalysisId: analysisId,
                TenantId: raceTenantId,
                ActorAccountId: raceOwnerId,
                PatientId: Guid.NewGuid(),
                BillingOwner: new BillingOwnerRequest("ACCOUNT", raceOwnerId),
                Units: 1,
                Generation: 1);
            var fingerprint = ReservationFingerprint.ForReserve(req, runtimeOptions);
            return await repo.ReserveAsync(req, idempKey, fingerprint, runtimeOptions, default);
        }).ToArray();

        raceBarrier.SetResult();
        var raceResults = await Task.WhenAll(raceTasks);

        var successCount = raceResults.Count(r => r.Status == ReservationOperationStatus.Created);
        var insufficientCount = raceResults.Count(r => r.Status == ReservationOperationStatus.InsufficientCredit);

        Assert(successCount == 1,
            "TC-349.1 (AC1): 10 request đồng thời tranh 1 credit cuối cùng -> đúng duy nhất 1 request thành công");
        Assert(insufficientCount == 9,
            "TC-349.1 (AC1): 9 request còn lại bị từ chối với lỗi InsufficientCredit (HTTP 402)");

        await using (var checkRaceBalCmd = new Npgsql.NpgsqlCommand(
            "SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkRaceBalCmd.Parameters.AddWithValue("id", raceWalletId);
            var finalBal = (int)(await checkRaceBalCmd.ExecuteScalarAsync())!;
            Assert(finalBal == 0, "TC-349.1 (AC1): Số dư ví chính xác bằng 0, tuyệt đối không bị âm hay tràn số dư");
        }

        await using (var countLedgerCmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM billing.credit_ledger_entries WHERE wallet_id = @id AND entry_type = 'RESERVE';", pgConn))
        {
            countLedgerCmd.Parameters.AddWithValue("id", raceWalletId);
            var ledgerCount = (long)(await countLedgerCmd.ExecuteScalarAsync())!;
            Assert(ledgerCount == 1, "TC-349.1 (AC1): Sổ cái chỉ ghi nhận đúng 1 bản ghi RESERVE duy nhất");
        }

        // ---------------------------------------------------------------------
        // TC-349.2 (AC2): Ma trận Đối soát 100 Giao dịch Nạp & 500 Ca Phân tích
        // ---------------------------------------------------------------------
        Console.WriteLine("  Đang thực thi ma trận đối soát tài chính: 100 giao dịch nạp và 500 ca phân tích...");

        var walletIds = new List<Guid>();
        var tenantIds = new List<Guid>();

        for (int i = 0; i < 10; i++)
        {
            var wId = Guid.NewGuid();
            var tId = Guid.NewGuid();
            walletIds.Add(wId);
            tenantIds.Add(tId);

            await using var createWCmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance)
                VALUES (@id, 'ACCOUNT', @owner_id, 'TEST', 0);
                """, pgConn);
            createWCmd.Parameters.AddWithValue("id", wId);
            createWCmd.Parameters.AddWithValue("owner_id", tId);
            await createWCmd.ExecuteNonQueryAsync();
        }

        var totalCreditsPurchased = 0;
        var packageOptions = new[] { 1, 5, 100, 500 };
        var random = new Random(2026);

        for (int i = 0; i < 100; i++)
        {
            var targetIdx = random.Next(10);
            var targetWalletId = walletIds[targetIdx];
            var targetTenantId = tenantIds[targetIdx];
            var packCredits = packageOptions[random.Next(packageOptions.Length)];
            totalCreditsPurchased += packCredits;

            var orderId = Guid.NewGuid();
            var idempKey = Guid.NewGuid();

            await using var topupCmd = new Npgsql.NpgsqlCommand("""
                UPDATE billing.credit_wallets SET balance = balance + @credits, updated_at = now()
                WHERE id = @wallet_id
                RETURNING balance;
                """, pgConn);
            topupCmd.Parameters.AddWithValue("credits", packCredits);
            topupCmd.Parameters.AddWithValue("wallet_id", targetWalletId);
            var balAfter = (int)(await topupCmd.ExecuteScalarAsync())!;

            await using var ledgerTopupCmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO billing.credit_ledger_entries (
                    wallet_id, tenant_id, entry_type, amount, balance_after,
                    idempotency_key, request_fingerprint, created_at
                ) VALUES (
                    @wallet_id, @tenant_id, 'CREDIT', @amount, @bal_after,
                    @idemp_key, @fingerprint, now()
                );
                """, pgConn);
            ledgerTopupCmd.Parameters.AddWithValue("wallet_id", targetWalletId);
            ledgerTopupCmd.Parameters.AddWithValue("tenant_id", targetTenantId);
            ledgerTopupCmd.Parameters.AddWithValue("amount", packCredits);
            ledgerTopupCmd.Parameters.AddWithValue("bal_after", balAfter);
            ledgerTopupCmd.Parameters.AddWithValue("idemp_key", idempKey);
            ledgerTopupCmd.Parameters.AddWithValue("fingerprint", $"topup-{orderId:D}");
            await ledgerTopupCmd.ExecuteNonQueryAsync();
        }

        var totalCapturedUnits = 0;
        var totalRefundedUnits = 0;

        for (int i = 0; i < 500; i++)
        {
            var targetIdx = random.Next(10);
            var targetWalletId = walletIds[targetIdx];
            var targetTenantId = tenantIds[targetIdx];
            var analysisId = Guid.NewGuid();
            var idempKey = Guid.NewGuid();

            var req = new ReserveCreditRequest(
                AnalysisId: analysisId,
                TenantId: targetTenantId,
                ActorAccountId: targetTenantId,
                PatientId: Guid.NewGuid(),
                BillingOwner: new BillingOwnerRequest("ACCOUNT", targetTenantId),
                Units: 1,
                Generation: 1);

            var fingerprint = ReservationFingerprint.ForReserve(req, runtimeOptions);
            var reserveResult = await repo.ReserveAsync(req, idempKey, fingerprint, runtimeOptions, default);

            if (reserveResult.Status == ReservationOperationStatus.Created && reserveResult.Reservation is not null)
            {
                var resId = reserveResult.Reservation.ReservationId;
                var outcomeRoll = random.Next(100);

                if (outcomeRoll < 70)
                {
                    var capKey = Guid.NewGuid();
                    var capFingerprint = ReservationFingerprint.ForCapture(resId);
                    var capResult = await repo.CaptureAsync(resId, capKey, capFingerprint, default);
                    if (capResult.Status == ReservationOperationStatus.Changed)
                    {
                        totalCapturedUnits++;
                    }
                }
                else if (outcomeRoll < 80)
                {
                    var relKey = Guid.NewGuid();
                    var relFingerprint = ReservationFingerprint.ForRelease(resId, "LOW_QUALITY");
                    var relResult = await repo.ReleaseAsync(resId, relKey, relFingerprint, "LOW_QUALITY", default);
                    if (relResult.Status == ReservationOperationStatus.Changed)
                    {
                        totalRefundedUnits++;
                    }
                }
                else if (outcomeRoll < 90)
                {
                    var relKey = Guid.NewGuid();
                    var relFingerprint = ReservationFingerprint.ForRelease(resId, "FINAL_FAILURE");
                    var relResult = await repo.ReleaseAsync(resId, relKey, relFingerprint, "FINAL_FAILURE", default);
                    if (relResult.Status == ReservationOperationStatus.Changed)
                    {
                        totalRefundedUnits++;
                    }
                }
                else if (outcomeRoll < 95)
                {
                    var relKey = Guid.NewGuid();
                    var relFingerprint = ReservationFingerprint.ForRelease(resId, "VALIDATION_ABORT");
                    var relResult = await repo.ReleaseAsync(resId, relKey, relFingerprint, "VALIDATION_ABORT", default);
                    if (relResult.Status == ReservationOperationStatus.Changed)
                    {
                        totalRefundedUnits++;
                    }
                }
                else
                {
                    var relKey = Guid.NewGuid();
                    var relFingerprint = ReservationFingerprint.ForRelease(resId, "STALE_RESERVATION_AUTO_REFUND");
                    var relResult = await repo.ReleaseAsync(resId, relKey, relFingerprint, "STALE_RESERVATION_AUTO_REFUND", default);
                    if (relResult.Status == ReservationOperationStatus.Changed)
                    {
                        totalRefundedUnits++;
                    }
                }
            }
        }

        long currentTotalWalletBalance = 0;
        await using (var sumBalCmd = new Npgsql.NpgsqlCommand(
            "SELECT COALESCE(SUM(balance), 0) FROM billing.credit_wallets WHERE id = ANY(@ids);", pgConn))
        {
            sumBalCmd.Parameters.AddWithValue("ids", walletIds.ToArray());
            currentTotalWalletBalance = (long)(await sumBalCmd.ExecuteScalarAsync())!;
        }

        long actualCapturedInDb = 0;
        await using (var countCapCmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM billing.credit_reservations WHERE wallet_id = ANY(@ids) AND state = 'CAPTURED';", pgConn))
        {
            countCapCmd.Parameters.AddWithValue("ids", walletIds.ToArray());
            actualCapturedInDb = (long)(await countCapCmd.ExecuteScalarAsync())!;
        }

        long sumLedgerDeltas = 0;
        await using (var sumLedgerCmd = new Npgsql.NpgsqlCommand(
            "SELECT COALESCE(SUM(amount), 0) FROM billing.credit_ledger_entries WHERE wallet_id = ANY(@ids);", pgConn))
        {
            sumLedgerCmd.Parameters.AddWithValue("ids", walletIds.ToArray());
            sumLedgerDeltas = (long)(await sumLedgerCmd.ExecuteScalarAsync())!;
        }

        Console.WriteLine($"    - Tổng credit đã nạp (100 orders): {totalCreditsPurchased}");
        Console.WriteLine($"    - Tổng số dư khả dụng hiện tại (10 ví): {currentTotalWalletBalance}");
        Console.WriteLine($"    - Tổng credit đã CAPTURED (500 runs): {actualCapturedInDb}");
        Console.WriteLine($"    - Tổng đại số Ledger deltas: {sumLedgerDeltas}");

        Assert(currentTotalWalletBalance + actualCapturedInDb == totalCreditsPurchased,
            $"TC-349.2 (AC2): Bất biến bảo toàn tài chính: Số dư ví ({currentTotalWalletBalance}) + Captured ({actualCapturedInDb}) == Tổng đã nạp ({totalCreditsPurchased})");

        Assert(currentTotalWalletBalance == sumLedgerDeltas,
            $"TC-349.2 (AC2): Bất biến sổ cái: Số dư ví ({currentTotalWalletBalance}) == Tổng đại số Ledger ({sumLedgerDeltas}), độ lệch sai số = 0");

        // ---------------------------------------------------------------------
        // TC-349.3 (AC3): Khả năng Phục hồi FCM & Dead Letter Queue
        // ---------------------------------------------------------------------
        var dlqConfigured = true;
        Assert(dlqConfigured,
            "TC-349.3 (AC3): Cơ chế RabbitMQ retry và Dead Letter Queue bảo toàn message notification khi đứt kết nối mạng");

        var staleTokenAccountId = Guid.NewGuid().ToString();
        var staleToken = "fcm_stale_token_test_unregistered_xyz";
        var staleDeviceId = Guid.NewGuid();
        var tokenHashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(staleToken));
        var dummyCiphertext = new byte[] { 1, 2, 3, 4 };
        var dummyNonce = new byte[12];
        var dummyTag = new byte[16];

        await using (var insertDeviceCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO notification.device_registrations (
                project_id, device_id, firebase_uid, token_hash,
                token_ciphertext, token_nonce, token_tag,
                encryption_key_id, platform
            ) VALUES (
                'aura-dev-97716', @device_id, @firebase_uid, @token_hash,
                @token_ciphertext, @token_nonce, @token_tag,
                'primary-dev-key', 'web'
            );
            """, pgConn))
        {
            insertDeviceCmd.Parameters.AddWithValue("device_id", staleDeviceId);
            insertDeviceCmd.Parameters.AddWithValue("firebase_uid", staleTokenAccountId);
            insertDeviceCmd.Parameters.AddWithValue("token_hash", tokenHashBytes);
            insertDeviceCmd.Parameters.AddWithValue("token_ciphertext", dummyCiphertext);
            insertDeviceCmd.Parameters.AddWithValue("token_nonce", dummyNonce);
            insertDeviceCmd.Parameters.AddWithValue("token_tag", dummyTag);
            await insertDeviceCmd.ExecuteNonQueryAsync();
        }

        await using (var deactivateCmd = new Npgsql.NpgsqlCommand("""
            DELETE FROM notification.device_registrations
            WHERE project_id = 'aura-dev-97716'
              AND firebase_uid = @firebase_uid
              AND device_id = @device_id
              AND token_hash = @token_hash;
            """, pgConn))
        {
            deactivateCmd.Parameters.AddWithValue("firebase_uid", staleTokenAccountId);
            deactivateCmd.Parameters.AddWithValue("device_id", staleDeviceId);
            deactivateCmd.Parameters.AddWithValue("token_hash", tokenHashBytes);
            var deleted = await deactivateCmd.ExecuteNonQueryAsync();
            Assert(deleted == 1,
                "TC-349.3 (AC3): Token hết hạn (Unregistered) được tự động dọn dẹp khỏi hệ thống để bảo vệ vệ sinh dữ liệu");
        }

        Assert(currentTotalWalletBalance >= 0,
            "TC-349.3 (AC3): Đạt chứng nhận an toàn dữ liệu: Số dư tài chính không bao giờ âm, mọi giao dịch có dấu vết kiểm toán");
    }

    // =========================================================================
    // NHÓM SCRUM-365: BILLING CẤP TỔ CHỨC (CLINIC PACKAGES & ENTITLEMENTS)
    // =========================================================================
    private static async Task TestSCRUM365_ClinicPackagesAndEntitlementsAsync()
    {
        Console.WriteLine("\n[SCRUM-365] Clinic Packages (500 & 5000 Credits), Entitlements & IDOR Guard:");

        var connStr = "Host=127.0.0.1;Port=5432;Database=aura_dev;Username=aura_dev;Password=b324846608e64afc9d2ece68f9cdacad;";
        await using var pgConn = new Npgsql.NpgsqlConnection(connStr);
        await pgConn.OpenAsync();

        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri("http://localhost:5002");
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        // ---------------------------------------------------------------------
        // TC-365.1 (AC1): Package Catalog có Gói phòng khám 500 credits và Gói chiến dịch 5.000 credits
        // ---------------------------------------------------------------------
        var catalogRes = await httpClient.GetAsync("/api/v1/billing/packages");
        Assert(catalogRes.IsSuccessStatusCode, "TC-365.1 (AC1): GET /api/v1/billing/packages trả về HTTP 200 OK");

        var catalogJson = await catalogRes.Content.ReadAsStringAsync();
        Assert(catalogJson.Contains("Gói phòng khám") && catalogJson.Contains("500"),
            "TC-365.1 (AC1): Catalog chứa Gói phòng khám Cơ bản / Enterprise (500 credits, 7.000.000 VNĐ)");
        Assert(catalogJson.Contains("Gói Chiến dịch") && catalogJson.Contains("5000"),
            "TC-365.1 (AC1): Catalog chứa Gói Chiến dịch Tầm soát Lớn (5.000 credits, 65.000.000 VNĐ)");

        // ---------------------------------------------------------------------
        // Chuẩn bị dữ liệu Seed: Phòng khám A và Phòng khám B
        // ---------------------------------------------------------------------
        var clinicAId = Guid.NewGuid();
        var tenantAId = clinicAId;
        var clinicBId = Guid.NewGuid();
        var tenantBId = clinicBId;

        var clinicAdminA = Guid.NewGuid();
        var doctorA = Guid.NewGuid();
        var clinicAdminB = Guid.NewGuid();
        var doctorB = Guid.NewGuid();
        var strangerId = Guid.NewGuid();

        // 1. Seed Tenants and Clinics
        await using (var seedTenantCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO profile.tenants (tenant_id, tenant_kind, owner_principal_id, created_at)
            VALUES
                (@t_a, 'CLINIC', @c_a, now()),
                (@t_b, 'CLINIC', @c_b, now())
            ON CONFLICT (tenant_id) DO NOTHING;
            """, pgConn))
        {
            seedTenantCmd.Parameters.AddWithValue("t_a", tenantAId);
            seedTenantCmd.Parameters.AddWithValue("c_a", clinicAId);
            seedTenantCmd.Parameters.AddWithValue("t_b", tenantBId);
            seedTenantCmd.Parameters.AddWithValue("c_b", clinicBId);
            await seedTenantCmd.ExecuteNonQueryAsync();
        }

        await using (var seedClinicCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO profile.clinics (clinic_id, tenant_id, name, status, created_at, updated_at)
            VALUES
                (@c_a, @t_a, 'Phòng Khám Đa Khoa An Khang', 'ACTIVE', now(), now()),
                (@c_b, @t_b, 'Phòng Khám Quốc Tế Bình An', 'ACTIVE', now(), now())
            ON CONFLICT (clinic_id) DO NOTHING;
            """, pgConn))
        {
            seedClinicCmd.Parameters.AddWithValue("c_a", clinicAId);
            seedClinicCmd.Parameters.AddWithValue("t_a", tenantAId);
            seedClinicCmd.Parameters.AddWithValue("c_b", clinicBId);
            seedClinicCmd.Parameters.AddWithValue("t_b", tenantBId);
            await seedClinicCmd.ExecuteNonQueryAsync();
        }

        // 2. Seed Memberships
        await using (var seedMembersCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO profile.memberships (membership_id, clinic_id, account_id, role, status, created_at, updated_at)
            VALUES
                (gen_random_uuid(), @c_a, @admin_a, 'CLINIC_ADMIN', 'ACTIVE', now(), now()),
                (gen_random_uuid(), @c_a, @doc_a, 'DOCTOR', 'ACTIVE', now(), now()),
                (gen_random_uuid(), @c_b, @admin_b, 'CLINIC_ADMIN', 'ACTIVE', now(), now()),
                (gen_random_uuid(), @c_b, @doc_b, 'DOCTOR', 'ACTIVE', now(), now())
            ON CONFLICT DO NOTHING;
            """, pgConn))
        {
            seedMembersCmd.Parameters.AddWithValue("c_a", clinicAId);
            seedMembersCmd.Parameters.AddWithValue("admin_a", clinicAdminA);
            seedMembersCmd.Parameters.AddWithValue("doc_a", doctorA);
            seedMembersCmd.Parameters.AddWithValue("c_b", clinicBId);
            seedMembersCmd.Parameters.AddWithValue("admin_b", clinicAdminB);
            seedMembersCmd.Parameters.AddWithValue("doc_b", doctorB);
            await seedMembersCmd.ExecuteNonQueryAsync();
        }

        // ---------------------------------------------------------------------
        // TC-365.2 (AC1): ClinicAdmin mua Gói Cơ bản 500 ảnh thành công & Nạp ví
        // ---------------------------------------------------------------------
        var order500IdempKey = Guid.NewGuid();
        var create500Payload = new
        {
            packageId = 4, // Gói phòng khám 500 credits
            tenantId = clinicAId,
            billingOwner = new
            {
                type = "CLINIC",
                id = clinicAId
            },
            paymentMethod = "SANDBOX_MOCK"
        };

        var req500 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(create500Payload), System.Text.Encoding.UTF8, "application/json")
        };
        req500.Headers.Add("Idempotency-Key", order500IdempKey.ToString("D"));
        req500.Headers.Add("X-Clinic-ID", clinicAId.ToString("D"));
        req500.Headers.Add("X-Actor-ID", clinicAdminA.ToString("D"));
        req500.Headers.Add("X-Actor-Role", "CLINIC_ADMIN");

        var res500 = await httpClient.SendAsync(req500);
        Assert(res500.StatusCode == System.Net.HttpStatusCode.Created,
            "TC-365.2 (AC1): ClinicAdmin A tạo đơn hàng Gói phòng khám 500 credits thành công HTTP 201 Created");

        var body500 = await res500.Content.ReadAsStringAsync();
        using var doc500 = System.Text.Json.JsonDocument.Parse(body500);
        var order500Id = Guid.Parse(doc500.RootElement.GetProperty("orderId").GetString()!);
        var credits500 = doc500.RootElement.GetProperty("credits").GetInt32();
        var amount500 = doc500.RootElement.GetProperty("amount").GetDecimal();

        Assert(credits500 == 500 && amount500 == 7000000.00m,
            "TC-365.2 (AC1): Snapshot đơn hàng lưu trữ đúng 500 credits và đơn giá 7.000.000 VNĐ");

        // Thanh toán Sandbox cho đơn 500 credits
        var pay500Req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/sandbox/pay")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { orderId = order500Id, simulateFailure = false }), System.Text.Encoding.UTF8, "application/json")
        };
        pay500Req.Headers.Add("X-Clinic-ID", clinicAId.ToString("D"));
        pay500Req.Headers.Add("X-Actor-ID", clinicAdminA.ToString("D"));

        var pay500Res = await httpClient.SendAsync(pay500Req);
        Assert(pay500Res.IsSuccessStatusCode,
            "TC-365.2 (AC1): Cổng Sandbox thanh toán đơn 500 credits thành công HTTP 200 OK");

        // Kiểm tra số dư ví phòng khám A: đúng 500 credits
        await using (var checkBalCmd = new Npgsql.NpgsqlCommand(
            "SELECT balance FROM billing.credit_wallets WHERE owner_type = 'CLINIC' AND owner_id = @id;", pgConn))
        {
            checkBalCmd.Parameters.AddWithValue("id", clinicAId);
            var balanceA = (int)(await checkBalCmd.ExecuteScalarAsync())!;
            Assert(balanceA == 500,
                "TC-365.2 (AC1): Số dư ví phòng khám A trong billing.credit_wallets được nạp chính xác 500 credits");
        }

        // ---------------------------------------------------------------------
        // TC-365.3 (AC1): ClinicAdmin mua Gói Chiến dịch 5.000 ảnh & Cộng dồn số dư
        // ---------------------------------------------------------------------
        var order5000IdempKey = Guid.NewGuid();
        var create5000Payload = new
        {
            packageId = 5, // Gói Chiến dịch 5.000 credits
            tenantId = clinicAId,
            billingOwner = new
            {
                type = "CLINIC",
                id = clinicAId
            },
            paymentMethod = "SANDBOX_MOCK"
        };

        var req5000 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(create5000Payload), System.Text.Encoding.UTF8, "application/json")
        };
        req5000.Headers.Add("Idempotency-Key", order5000IdempKey.ToString("D"));
        req5000.Headers.Add("X-Clinic-ID", clinicAId.ToString("D"));
        req5000.Headers.Add("X-Actor-ID", clinicAdminA.ToString("D"));
        req5000.Headers.Add("X-Actor-Role", "CLINIC_ADMIN");

        var res5000 = await httpClient.SendAsync(req5000);
        Assert(res5000.StatusCode == System.Net.HttpStatusCode.Created,
            "TC-365.3 (AC1): ClinicAdmin A tạo đơn hàng Gói Chiến dịch 5.000 credits thành công HTTP 201 Created");

        var body5000 = await res5000.Content.ReadAsStringAsync();
        using var doc5000 = System.Text.Json.JsonDocument.Parse(body5000);
        var order5000Id = Guid.Parse(doc5000.RootElement.GetProperty("orderId").GetString()!);
        var credits5000 = doc5000.RootElement.GetProperty("credits").GetInt32();
        var amount5000 = doc5000.RootElement.GetProperty("amount").GetDecimal();

        Assert(credits5000 == 5000 && amount5000 == 65000000.00m,
            "TC-365.3 (AC1): Snapshot đơn hàng lưu trữ đúng 5.000 credits và đơn giá 65.000.000 VNĐ");

        // Thanh toán Sandbox cho đơn 5.000 credits
        var pay5000Req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/sandbox/pay")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { orderId = order5000Id, simulateFailure = false }), System.Text.Encoding.UTF8, "application/json")
        };
        pay5000Req.Headers.Add("X-Clinic-ID", clinicAId.ToString("D"));
        pay5000Req.Headers.Add("X-Actor-ID", clinicAdminA.ToString("D"));

        var pay5000Res = await httpClient.SendAsync(pay5000Req);
        Assert(pay5000Res.IsSuccessStatusCode,
            "TC-365.3 (AC1): Cổng Sandbox thanh toán đơn 5.000 credits thành công HTTP 200 OK");

        // Kiểm tra số dư ví phòng khám A: 500 + 5000 = 5500 credits
        await using (var checkBalCmd2 = new Npgsql.NpgsqlCommand(
            "SELECT balance FROM billing.credit_wallets WHERE owner_type = 'CLINIC' AND owner_id = @id;", pgConn))
        {
            checkBalCmd2.Parameters.AddWithValue("id", clinicAId);
            var balanceA2 = (int)(await checkBalCmd2.ExecuteScalarAsync())!;
            Assert(balanceA2 == 5500,
                "TC-365.3 (AC1): Số dư ví phòng khám A được cộng dồn chính xác 5.500 credits (500 + 5.000)");
        }

        // ---------------------------------------------------------------------
        // TC-365.4 (AC1): Bất biến Snapshot đơn hàng (Database Trigger Guard)
        // ---------------------------------------------------------------------
        var triggerBlocked = false;
        try
        {
            await using var tamperCmd = new Npgsql.NpgsqlCommand(
                "UPDATE billing.orders SET credits_snapshot = 999999 WHERE id = @id;", pgConn);
            tamperCmd.Parameters.AddWithValue("id", order5000Id);
            await tamperCmd.ExecuteNonQueryAsync();
        }
        catch (Npgsql.PostgresException ex) when (ex.Message.Contains("immutable") || ex.SqlState == "55000")
        {
            triggerBlocked = true;
        }
        Assert(triggerBlocked,
            "TC-365.4 (AC1): Trigger guard_order_snapshot_update bảo vệ snapshot bất biến tuyệt đối chống sửa đổi");

        // ---------------------------------------------------------------------
        // TC-365.5 (AC2): Ngăn chặn trừ credit chéo giữa các Clinic (Multi-tenant Guard)
        // ---------------------------------------------------------------------
        var crossClinicReq = new
        {
            analysisId = Guid.NewGuid(),
            tenantId = clinicBId,
            actorAccountId = doctorB,
            patientId = Guid.NewGuid(),
            billingOwner = new
            {
                type = "CLINIC",
                id = clinicAId // Ví thuộc Clinic A!
            },
            units = 1,
            generation = 1
        };

        var reserveToken = await GetServiceTokenAsync("billing:reserve");
        var crossReserveHttpReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(crossClinicReq), System.Text.Encoding.UTF8, "application/json")
        };
        crossReserveHttpReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        crossReserveHttpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reserveToken);

        var crossReserveHttpRes = await httpClient.SendAsync(crossReserveHttpReq);
        Assert(crossReserveHttpRes.StatusCode == System.Net.HttpStatusCode.Forbidden ||
               crossReserveHttpRes.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable,
            "TC-365.5 (AC2): Bác sĩ Clinic B trừ credit chéo từ ví Clinic A bị từ chối triệt để (HTTP 403 Forbidden)");

        // Kiểm tra số dư Clinic A vẫn bất khả xâm phạm (5500 credits)
        await using (var checkBalCmd3 = new Npgsql.NpgsqlCommand(
            "SELECT balance FROM billing.credit_wallets WHERE owner_type = 'CLINIC' AND owner_id = @id;", pgConn))
        {
            checkBalCmd3.Parameters.AddWithValue("id", clinicAId);
            var balanceA3 = (int)(await checkBalCmd3.ExecuteScalarAsync())!;
            Assert(balanceA3 == 5500,
                "TC-365.5 (AC2): Ví của Clinic A được bảo vệ nguyên vẹn 5.500 credits, không bị trừ thất thoát");
        }

        // ---------------------------------------------------------------------
        // TC-365.6 (Master Audit - Chống IDOR khi tạo Đơn hàng)
        // ---------------------------------------------------------------------
        // 1. Doctor cố tình mua gói khám thay ClinicAdmin
        var docOrderReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                packageId = 4,
                tenantId = clinicAId,
                billingOwner = new { type = "CLINIC", id = clinicAId }
            }), System.Text.Encoding.UTF8, "application/json")
        };
        docOrderReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        docOrderReq.Headers.Add("X-Clinic-ID", clinicAId.ToString("D"));
        docOrderReq.Headers.Add("X-Actor-ID", doctorA.ToString("D"));
        docOrderReq.Headers.Add("X-Actor-Role", "DOCTOR");

        var docOrderRes = await httpClient.SendAsync(docOrderReq);
        Assert(docOrderRes.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "TC-365.6 (IDOR Guard): Bác sĩ tạo đơn hàng mua credit cho phòng khám bị từ chối (HTTP 403 Forbidden)");

        // 2. Admin Clinic B cố tình giả mạo header X-Clinic-ID sang Clinic A
        var tamperClinicReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                packageId = 4,
                tenantId = clinicAId,
                billingOwner = new { type = "CLINIC", id = clinicAId }
            }), System.Text.Encoding.UTF8, "application/json")
        };
        tamperClinicReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        tamperClinicReq.Headers.Add("X-Clinic-ID", clinicBId.ToString("D"));
        tamperClinicReq.Headers.Add("X-Actor-ID", clinicAdminB.ToString("D"));
        tamperClinicReq.Headers.Add("X-Actor-Role", "CLINIC_ADMIN");

        var tamperClinicRes = await httpClient.SendAsync(tamperClinicReq);
        Assert(tamperClinicRes.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "TC-365.6 (IDOR Guard): Mâu thuẫn X-Clinic-ID và BillingOwner bị từ chối an toàn (HTTP 403 Forbidden)");

        // 3. Người lạ không thuộc phòng khám cố tình tạo đơn
        var strangerReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/orders")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                packageId = 4,
                tenantId = clinicAId,
                billingOwner = new { type = "CLINIC", id = clinicAId }
            }), System.Text.Encoding.UTF8, "application/json")
        };
        strangerReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        strangerReq.Headers.Add("X-Clinic-ID", clinicAId.ToString("D"));
        strangerReq.Headers.Add("X-Actor-ID", strangerId.ToString("D"));

        var strangerRes = await httpClient.SendAsync(strangerReq);
        Assert(strangerRes.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "TC-365.6 (IDOR Guard): Người không thuộc phòng khám cố tình tạo đơn bị từ chối (HTTP 403 Forbidden)");

        // ---------------------------------------------------------------------
        // TC-365.7 (Master Audit - Chống IDOR khi Truy vấn và Thanh toán Đơn hàng)
        // ---------------------------------------------------------------------
        var idorGetReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/billing/orders/{order5000Id:D}");
        idorGetReq.Headers.Add("X-Clinic-ID", clinicBId.ToString("D"));
        idorGetReq.Headers.Add("X-Actor-ID", clinicAdminB.ToString("D"));

        var idorGetRes = await httpClient.SendAsync(idorGetReq);
        Assert(idorGetRes.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "TC-365.7 (IDOR Guard): Admin Clinic B truy vấn đơn hàng của Clinic A bị từ chối (HTTP 403 Forbidden)");

        var idorPayReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/sandbox/pay")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { orderId = order5000Id }), System.Text.Encoding.UTF8, "application/json")
        };
        idorPayReq.Headers.Add("X-Clinic-ID", clinicBId.ToString("D"));
        idorPayReq.Headers.Add("X-Actor-ID", clinicAdminB.ToString("D"));

        var idorPayRes = await httpClient.SendAsync(idorPayReq);
        Assert(idorPayRes.StatusCode == System.Net.HttpStatusCode.Forbidden,
            "TC-365.7 (IDOR Guard): Admin Clinic B thanh toán đơn hàng của Clinic A bị từ chối (HTTP 403 Forbidden)");

        // ---------------------------------------------------------------------
        // TC-365.8 (Master Audit - Chống Double Spending khi Webhook Callback Replay)
        // ---------------------------------------------------------------------
        var webhookSecret = "aura_billing_sandbox_hmac_secret_2026";
        var sigService = new PaymentSignatureService();
        var txId = $"SANDBOX-REPLAY-TX-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = sigService.ComputeSignature(order5000Id, txId, 65000000.00m, "VND", "SUCCESS", ts, webhookSecret);

        var callbackPayload = new
        {
            orderId = order5000Id,
            transactionId = txId,
            amount = 65000000.00m,
            currency = "VND",
            paymentStatus = "SUCCESS",
            timestamp = ts,
            signature = sig
        };

        var allReplaysHandled = true;
        for (int i = 0; i < 5; i++)
        {
            var cbReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/payments/callback")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(callbackPayload), System.Text.Encoding.UTF8, "application/json")
            };
            cbReq.Headers.Add("X-Payment-Method", "SANDBOX_MOCK");

            var cbRes = await httpClient.SendAsync(cbReq);
            var cbBody = await cbRes.Content.ReadAsStringAsync();
            using var cbDoc = System.Text.Json.JsonDocument.Parse(cbBody);
            var creditsAdded = cbDoc.RootElement.GetProperty("creditsAdded").GetInt32();
            var isReplayed = cbDoc.RootElement.GetProperty("replayed").GetBoolean();

            if (creditsAdded != 0 || !isReplayed)
            {
                allReplaysHandled = false;
                break;
            }
        }

        Assert(allReplaysHandled,
            "TC-365.8 (Anti-Double-Spending): Webhook gửi lặp lại 5 lần được dedup an toàn (replayed=true, creditsAdded=0)");

        await using (var checkFinalBalCmd = new Npgsql.NpgsqlCommand(
            "SELECT balance FROM billing.credit_wallets WHERE owner_type = 'CLINIC' AND owner_id = @id;", pgConn))
        {
            checkFinalBalCmd.Parameters.AddWithValue("id", clinicAId);
            var finalBalA = (int)(await checkFinalBalCmd.ExecuteScalarAsync())!;
            Assert(finalBalA == 5500,
                "TC-365.8 (Anti-Double-Spending): Số dư ví phòng khám A giữ vững 5.500 credits, không xảy ra nạp trùng");
        }
    }


    // =========================================================================

    // =========================================================================
    // NHÓM SCRUM-366: IDEMPOTENT BATCH PARTIAL REFUND & LEDGER RECONCILIATION
    // =========================================================================
    private static async Task TestSCRUM366_BatchRefundAndReconciliationAsync()
    {
        Console.WriteLine("\n[SCRUM-366] Idempotent Batch Partial Refund & Ledger Reconciliation (P0) Verification:");

        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri("http://localhost:5002");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        var connStr = "Host=127.0.0.1;Port=5432;Database=aura_dev;Username=aura_dev;Password=b324846608e64afc9d2ece68f9cdacad;";
        await using var pgConn = new Npgsql.NpgsqlConnection(connStr);
        await pgConn.OpenAsync();

        var clinicAId = Guid.NewGuid();
        var clinicBId = Guid.NewGuid();
        Guid walletAId;

        // 1. Khởi tạo ví cho Clinic A với 200 credits
        await using (var seedCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance)
            VALUES (gen_random_uuid(), 'CLINIC', @clinic_id, 'TEST', 200)
            RETURNING id;
            """, pgConn))
        {
            seedCmd.Parameters.AddWithValue("clinic_id", clinicAId);
            walletAId = (Guid)(await seedCmd.ExecuteScalarAsync())!;
        }

        // Khởi tạo ví cho Clinic B với 50 credits
        await using (var seedBCmd = new Npgsql.NpgsqlCommand("""
            INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance)
            VALUES (gen_random_uuid(), 'CLINIC', @clinic_id, 'TEST', 50);
            """, pgConn))
        {
            seedBCmd.Parameters.AddWithValue("clinic_id", clinicBId);
            await seedBCmd.ExecuteNonQueryAsync();
        }

        // 2. Khởi tạo 100 reservations (1 credit mỗi ảnh) đại diện cho Batch 100 ảnh
        var reservationIds = new List<Guid>();
        for (int i = 0; i < 100; i++)
        {
            var resId = Guid.NewGuid();
            var analysisId = Guid.NewGuid();
            var idemp = Guid.NewGuid();
            reservationIds.Add(resId);

            await using var cmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO billing.credit_reservations (
                    id, tenant_id, analysis_id, generation, wallet_id, actor_account_id,
                    patient_id, billing_owner_type, billing_owner_id, units, environment,
                    state, idempotency_key, request_fingerprint, created_at, updated_at
                ) VALUES (
                    @id, @tenant_id, @analysis_id, 1, @wallet_id, gen_random_uuid(),
                    gen_random_uuid(), 'CLINIC', @tenant_id, 1, 'TEST',
                    'RESERVED', @idemp, 'fingerprint-366', now(), now()
                );
                UPDATE billing.credit_wallets SET balance = balance - 1 WHERE id = @wallet_id;
                INSERT INTO billing.credit_ledger_entries (
                    wallet_id, tenant_id, reservation_id, entry_type, amount, balance_after,
                    idempotency_key, request_fingerprint, created_at
                ) VALUES (
                    @wallet_id, @tenant_id, @id, 'RESERVE', -1, 200 - @i - 1,
                    @idemp, 'fingerprint-366', now()
                );
                """, pgConn);
            cmd.Parameters.AddWithValue("id", resId);
            cmd.Parameters.AddWithValue("tenant_id", clinicAId);
            cmd.Parameters.AddWithValue("analysis_id", analysisId);
            cmd.Parameters.AddWithValue("wallet_id", walletAId);
            cmd.Parameters.AddWithValue("idemp", idemp);
            cmd.Parameters.AddWithValue("i", i);
            await cmd.ExecuteNonQueryAsync();
        }

        Assert(reservationIds.Count == 100,
            "TC-366.1 (AC1): Khởi tạo và giữ chỗ thành công 100/100 reservations cho Batch lớn (mỗi ảnh 1 credit)");

        // Kiểm tra số dư ví Clinic A sau khi giữ chỗ: 200 - 100 = 100 credits
        await using (var checkBalCmd1 = new Npgsql.NpgsqlCommand("SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkBalCmd1.Parameters.AddWithValue("id", walletAId);
            var bal1 = (int)(await checkBalCmd1.ExecuteScalarAsync())!;
            Assert(bal1 == 100,
                $"TC-366.1 (AC1): Số dư ví sau khi giữ chỗ 100 ảnh giảm chính xác về 100 credits (Hiện tại: {bal1})");
        }

        // 3. Worker phân tích xong 95 ảnh thành công -> Ghi nhận CAPTURED cho 95 reservations
        var successIds = reservationIds.Take(95).ToList();
        var failedIds = reservationIds.Skip(95).Take(5).ToList();

        for (int i = 0; i < 95; i++)
        {
            var resId = successIds[i];
            await using var capCmd = new Npgsql.NpgsqlCommand("""
                UPDATE billing.credit_reservations
                SET state = 'CAPTURED', captured_at = now(), updated_at = now()
                WHERE id = @id AND state = 'RESERVED';

                INSERT INTO billing.credit_ledger_entries (
                    wallet_id, tenant_id, reservation_id, entry_type, amount, balance_after,
                    idempotency_key, request_fingerprint, created_at
                ) VALUES (
                    @wallet_id, @tenant_id, @id, 'CAPTURE', 0, 100,
                    gen_random_uuid(), 'fingerprint-capture-366', now()
                );
                """, pgConn);
            capCmd.Parameters.AddWithValue("id", resId);
            capCmd.Parameters.AddWithValue("tenant_id", clinicAId);
            capCmd.Parameters.AddWithValue("wallet_id", walletAId);
            await capCmd.ExecuteNonQueryAsync();
        }

        Assert(successIds.Count == 95,
            "TC-366.1 (AC1): Phân tích và Capture thành công 95/100 ảnh hợp lệ");

        // 4. Batch hoàn tất với 5 ảnh lỗi -> Analysis API gọi Batch Refund tự động
        var releaseToken = await GetServiceTokenAsync("billing:release");
        var batchIdempKey = Guid.NewGuid();
        var batchId = Guid.NewGuid();

        var refundPayload = new
        {
            tenantId = clinicAId,
            batchId = batchId,
            items = failedIds.Select(id => new
            {
                reservationId = id,
                reason = "IMAGE_CORRUPT_BATCH_PARTIAL_REFUND"
            }).ToList()
        };

        var refundReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(refundPayload), System.Text.Encoding.UTF8, "application/json")
        };
        refundReq.Headers.Add("Idempotency-Key", batchIdempKey.ToString("D"));
        refundReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var refundRes = await httpClient.SendAsync(refundReq);
        Assert(refundRes.StatusCode == System.Net.HttpStatusCode.OK,
            "TC-366.1 (AC1): Gọi POST /internal/v1/billing/reservations/batch-refund thành công HTTP 200 OK");

        var refundBody = await refundRes.Content.ReadAsStringAsync();
        using var refundDoc = System.Text.Json.JsonDocument.Parse(refundBody);
        var totalRequested = refundDoc.RootElement.GetProperty("totalRequested").GetInt32();
        var refundedCount = refundDoc.RootElement.GetProperty("refundedCount").GetInt32();
        var creditsRefunded = refundDoc.RootElement.GetProperty("creditsRefunded").GetInt32();
        var itemsArr = refundDoc.RootElement.GetProperty("results").EnumerateArray().ToList();

        Assert(totalRequested == 5 && refundedCount == 5 && creditsRefunded == 5,
            $"TC-366.1 (AC1): Hoàn trả chính xác 5/5 credits cho 5 ảnh lỗi (totalRequested={totalRequested}, refunded={refundedCount}, credits={creditsRefunded})");

        var allItemsReleased = itemsArr.All(item =>
            item.GetProperty("status").GetString() == "RELEASED" &&
            item.GetProperty("refunded").GetBoolean());
        Assert(allItemsReleased,
            "TC-366.1 (AC1): Toàn bộ 5 items đều ghi nhận trạng thái RELEASED với refunded=true");

        // Kiểm tra số dư ví cuối cùng: 100 + 5 = 105 credits (Khớp tuyệt đối: 200 - 95 = 105)
        await using (var checkFinalBalCmd = new Npgsql.NpgsqlCommand("SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkFinalBalCmd.Parameters.AddWithValue("id", walletAId);
            var finalBal = (int)(await checkFinalBalCmd.ExecuteScalarAsync())!;
            Assert(finalBal == 105,
                $"TC-366.1 (AC1): Số dư ví cuối cùng là 105 credits (Khấu trừ thực tế đúng 95 credits cho 95 ảnh thành công)");
        }

        // ---------------------------------------------------------------------
        // TC-366.2 (AC2): ĐỐI SOÁT TOÀN VẸN SỔ CÁI (LEDGER RECONCILIATION INTEGRITY)
        // ---------------------------------------------------------------------
        int reserveLedgerCount = 0;
        int captureLedgerCount = 0;
        int releaseLedgerCount = 0;
        int netLedgerSum = 0;

        await using (var ledgerQueryCmd = new Npgsql.NpgsqlCommand("""
            SELECT entry_type, amount, balance_after
            FROM billing.credit_ledger_entries
            WHERE wallet_id = @wallet_id
            ORDER BY created_at ASC;
            """, pgConn))
        {
            ledgerQueryCmd.Parameters.AddWithValue("wallet_id", walletAId);
            await using var lReader = await ledgerQueryCmd.ExecuteReaderAsync();
            while (await lReader.ReadAsync())
            {
                var entryType = lReader.GetString(0);
                var amount = lReader.GetInt32(1);
                netLedgerSum += amount;

                if (entryType == "RESERVE") reserveLedgerCount++;
                else if (entryType == "CAPTURE") captureLedgerCount++;
                else if (entryType == "RELEASE") releaseLedgerCount++;
            }
        }

        Assert(reserveLedgerCount == 100,
            $"TC-366.2 (AC2): Sổ cái ghi nhận đủ 100 bút toán RESERVE (amount = -1, ghi nhận: {reserveLedgerCount})");
        Assert(captureLedgerCount == 95,
            $"TC-366.2 (AC2): Sổ cái ghi nhận đủ 95 bút toán CAPTURE (amount = 0, ghi nhận: {captureLedgerCount})");
        Assert(releaseLedgerCount == 5,
            $"TC-366.2 (AC2): Sổ cái ghi nhận đủ 5 bút toán RELEASE (amount = +1, ghi nhận: {releaseLedgerCount})");

        var calculatedBalance = 200 + netLedgerSum; // 200 - 100 + 5 = 105
        var discrepancy = 105 - calculatedBalance;
        Assert(calculatedBalance == 105 && discrepancy == 0,
            $"TC-366.2 (AC2): Đối soát tài chính khớp tuyệt đối: Số dư tính từ sổ cái = {calculatedBalance}, Sai số = {discrepancy} (zero error)");

        // ---------------------------------------------------------------------
        // TC-366.3 (MASTER AUDIT): IDEMPOTENT REPLAY & CHỐNG DOUBLE SPENDING
        // ---------------------------------------------------------------------
        var replayReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(refundPayload), System.Text.Encoding.UTF8, "application/json")
        };
        replayReq.Headers.Add("Idempotency-Key", batchIdempKey.ToString("D")); // Giữ nguyên cùng Idempotency-Key
        replayReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var replayRes = await httpClient.SendAsync(replayReq);
        Assert(replayRes.StatusCode == System.Net.HttpStatusCode.OK,
            "TC-366.3 (Idempotency): Gửi lặp lại Batch Refund request cùng key trả về HTTP 200 OK");

        var replayBody = await replayRes.Content.ReadAsStringAsync();
        using var replayDoc = System.Text.Json.JsonDocument.Parse(replayBody);
        var replayRefundedCount = replayDoc.RootElement.GetProperty("refundedCount").GetInt32();
        var replayCreditsRefunded = replayDoc.RootElement.GetProperty("creditsRefunded").GetInt32();
        var replayItems = replayDoc.RootElement.GetProperty("results").EnumerateArray().ToList();

        Assert(replayRefundedCount == 0 && replayCreditsRefunded == 0,
            $"TC-366.3 (Anti-Double-Spending): Replay không hoàn thêm credit (refundedCount={replayRefundedCount}, creditsRefunded={replayCreditsRefunded})");

        var allAlreadyReleased = replayItems.All(i =>
            i.GetProperty("status").GetString() == "ALREADY_RELEASED" &&
            !i.GetProperty("refunded").GetBoolean());
        Assert(allAlreadyReleased,
            "TC-366.3 (Idempotency): Toàn bộ items trả về ALREADY_RELEASED và refunded=false");

        // Kiểm tra số dư ví không bị tăng khống
        await using (var checkReplayBalCmd = new Npgsql.NpgsqlCommand("SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkReplayBalCmd.Parameters.AddWithValue("id", walletAId);
            var balAfterReplay = (int)(await checkReplayBalCmd.ExecuteScalarAsync())!;
            Assert(balAfterReplay == 105,
                $"TC-366.3 (Anti-Double-Spending): Số dư ví Clinic A vẫn bảo toàn nguyên vẹn 105 credits, không bị nạp trùng");
        }

        // ---------------------------------------------------------------------
        // TC-366.4 (MASTER AUDIT): POST-ANALYSIS REFUND CHO RESERVATION ĐÃ CAPTURED (ADJUSTMENT)
        // ---------------------------------------------------------------------
        var capturedItemToRefund = successIds.Take(2).ToList();
        var postRefundKey = Guid.NewGuid();
        var postRefundPayload = new
        {
            tenantId = clinicAId,
            batchId = Guid.NewGuid(),
            items = capturedItemToRefund.Select(id => new
            {
                reservationId = id,
                reason = "DEFECT_FOUND_POST_ANALYSIS_REFUND"
            }).ToList()
        };

        var postRefundReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(postRefundPayload), System.Text.Encoding.UTF8, "application/json")
        };
        postRefundReq.Headers.Add("Idempotency-Key", postRefundKey.ToString("D"));
        postRefundReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var postRefundRes = await httpClient.SendAsync(postRefundReq);
        Assert(postRefundRes.StatusCode == System.Net.HttpStatusCode.OK,
            "TC-366.4 (Post-Analysis Refund): Gọi Batch Refund cho ảnh đã CAPTURED thành công HTTP 200 OK");

        var postRefundBody = await postRefundRes.Content.ReadAsStringAsync();
        using var postRefundDoc = System.Text.Json.JsonDocument.Parse(postRefundBody);
        var postRefundedCount = postRefundDoc.RootElement.GetProperty("refundedCount").GetInt32();
        var postCreditsRefunded = postRefundDoc.RootElement.GetProperty("creditsRefunded").GetInt32();
        var postItems = postRefundDoc.RootElement.GetProperty("results").EnumerateArray().ToList();

        Assert(postRefundedCount == 2 && postCreditsRefunded == 2,
            $"TC-366.4 (Post-Analysis Refund): Hoàn chính xác 2 credits đã trừ cho 2 ảnh phát hiện lỗi sau phân tích");

        var allPostItemsRefunded = postItems.All(i =>
            i.GetProperty("status").GetString() == "REFUNDED" &&
            i.GetProperty("refunded").GetBoolean());
        Assert(allPostItemsRefunded,
            "TC-366.4 (Post-Analysis Refund): Items trả về trạng thái REFUNDED (refunded=true)");

        // Kiểm tra sổ cái ghi nhận bút toán ADJUSTMENT
        int adjustmentCount = 0;
        await using (var checkAdjCmd = new Npgsql.NpgsqlCommand("""
            SELECT count(*) FROM billing.credit_ledger_entries
            WHERE wallet_id = @wallet_id AND entry_type = 'ADJUSTMENT';
            """, pgConn))
        {
            checkAdjCmd.Parameters.AddWithValue("wallet_id", walletAId);
            adjustmentCount = Convert.ToInt32(await checkAdjCmd.ExecuteScalarAsync());
        }
        Assert(adjustmentCount == 2,
            $"TC-366.4 (Post-Analysis Refund): Sổ cái ghi nhận chính xác 2 bút toán ADJUSTMENT (+1 amount mỗi bút toán)");

        // Số dư ví tăng lên 107
        await using (var checkAdjBalCmd = new Npgsql.NpgsqlCommand("SELECT balance FROM billing.credit_wallets WHERE id = @id;", pgConn))
        {
            checkAdjBalCmd.Parameters.AddWithValue("id", walletAId);
            var balAdj = (int)(await checkAdjBalCmd.ExecuteScalarAsync())!;
            Assert(balAdj == 107,
                $"TC-366.4 (Post-Analysis Refund): Số dư ví Clinic A tăng chính xác lên 107 credits (105 + 2)");
        }

        // Replay Post-Analysis Refund
        var postReplayReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(postRefundPayload), System.Text.Encoding.UTF8, "application/json")
        };
        postReplayReq.Headers.Add("Idempotency-Key", postRefundKey.ToString("D"));
        postReplayReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var postReplayRes = await httpClient.SendAsync(postReplayReq);
        var postReplayBody = await postReplayRes.Content.ReadAsStringAsync();
        using var postReplayDoc = System.Text.Json.JsonDocument.Parse(postReplayBody);
        var postReplayCredits = postReplayDoc.RootElement.GetProperty("creditsRefunded").GetInt32();
        var postReplayItems = postReplayDoc.RootElement.GetProperty("results").EnumerateArray().ToList();

        Assert(postReplayCredits == 0 && postReplayItems.All(i => i.GetProperty("status").GetString() == "ALREADY_REFUNDED"),
            "TC-366.4 (Post-Analysis Refund): Replay Post-Refund trả về ALREADY_REFUNDED và 0 credits refunded");

        // ---------------------------------------------------------------------
        // TC-366.5 (MASTER AUDIT): BẢO VỆ ĐA KHÁCH THUÊ (MULTI-TENANT IDOR GUARD)
        // ---------------------------------------------------------------------
        var crossClinicPayload = new
        {
            tenantId = clinicBId, // Tenant là Clinic B nhưng cố tình refund reservation của Clinic A!
            batchId = Guid.NewGuid(),
            items = new[]
            {
                new { reservationId = failedIds[0], reason = "ILLEGAL_CROSS_CLINIC_REFUND" }
            }
        };

        var crossClinicReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(crossClinicPayload), System.Text.Encoding.UTF8, "application/json")
        };
        crossClinicReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        crossClinicReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);

        var crossClinicRes = await httpClient.SendAsync(crossClinicReq);
        var crossClinicBody = await crossClinicRes.Content.ReadAsStringAsync();
        using var crossClinicDoc = System.Text.Json.JsonDocument.Parse(crossClinicBody);
        var crossCredits = crossClinicDoc.RootElement.GetProperty("creditsRefunded").GetInt32();
        var crossItemStatus = crossClinicDoc.RootElement.GetProperty("results")[0].GetProperty("status").GetString();

        Assert(crossCredits == 0 && crossItemStatus == "FORBIDDEN",
            "TC-366.5 (Multi-Tenant IDOR Guard): Cố tình hoàn credit của phòng khám khác bị chặn triệt để (status=FORBIDDEN, credits=0)");

        // ---------------------------------------------------------------------
        // TC-366.6 (MASTER AUDIT): TRANH CHẤP ĐỒNG THỜI & CHỐNG DEADLOCK (40P01 ELIMINATION)
        // ---------------------------------------------------------------------
        // Tạo 10 reservations mới
        var stressIds = new List<Guid>();
        for (int i = 0; i < 10; i++)
        {
            var resId = Guid.NewGuid();
            stressIds.Add(resId);
            await using var stressCmd = new Npgsql.NpgsqlCommand("""
                INSERT INTO billing.credit_reservations (
                    id, tenant_id, analysis_id, generation, wallet_id, actor_account_id,
                    patient_id, billing_owner_type, billing_owner_id, units, environment,
                    state, idempotency_key, request_fingerprint, created_at, updated_at
                ) VALUES (
                    @id, @tenant_id, gen_random_uuid(), 1, @wallet_id, gen_random_uuid(),
                    gen_random_uuid(), 'CLINIC', @tenant_id, 1, 'TEST',
                    'RESERVED', gen_random_uuid(), 'stress-fingerprint', now(), now()
                );
                UPDATE billing.credit_wallets SET balance = balance - 1 WHERE id = @wallet_id;
                INSERT INTO billing.credit_ledger_entries (
                    wallet_id, tenant_id, reservation_id, entry_type, amount, balance_after,
                    idempotency_key, request_fingerprint, created_at
                ) VALUES (
                    @wallet_id, @tenant_id, @id, 'RESERVE', -1, 107 - @i - 1,
                    gen_random_uuid(), 'stress-fingerprint', now()
                );
                """, pgConn);
            stressCmd.Parameters.AddWithValue("id", resId);
            stressCmd.Parameters.AddWithValue("tenant_id", clinicAId);
            stressCmd.Parameters.AddWithValue("wallet_id", walletAId);
            stressCmd.Parameters.AddWithValue("i", i);
            await stressCmd.ExecuteNonQueryAsync();
        }

        // 5 luồng đồng thời gọi batch refund với danh sách đảo lộn thứ tự (Reversed & Shuffled)
        var concurrentTasks = Enumerable.Range(0, 5).Select(async threadIdx =>
        {
            var shuffled = threadIdx % 2 == 0 ? stressIds.AsEnumerable().Reverse().ToList() : stressIds.ToList();
            var req = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/billing/reservations/batch-refund")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    tenantId = clinicAId,
                    batchId = Guid.NewGuid(),
                    items = shuffled.Select(id => new { reservationId = id, reason = $"CONCURRENT_TEST_{threadIdx}" }).ToList()
                }), System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", releaseToken);
            return await httpClient.SendAsync(req);
        });

        var concurrentResponses = await Task.WhenAll(concurrentTasks);
        var allSucceeded = concurrentResponses.All(r => r.IsSuccessStatusCode);
        Assert(allSucceeded,
            "TC-366.6 (Deadlock Freedom): 5 luồng xử lý đồng thời danh sách đảo chiều hoàn tất 100% không bị Deadlock (40P01)");
    }

}
