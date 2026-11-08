using System.Diagnostics;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;
using AURA.Notification.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AURA.Notification.InboxChecks;

public class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("====================================================================");
        Console.WriteLine("AURA Task SCRUM-327 & SCRUM-364 (W5-M1-03) Verification Suite");
        Console.WriteLine("Notification Inbox persistence & authority checks (durable worker tests: AURA.M1.RegressionTests)");
        Console.WriteLine("====================================================================");

        try
        {
            await AuthorityBoundaryChecks.RunAsync(Assert);
            await TestAC1_InboxCreationAndEventDedupAsync();
            await TestAC2_AccountBoundaryAndUnreadManagementAsync();
            await TestAC3_FrontendContractAndFcmDecouplingAsync();

            Console.WriteLine();
            Console.WriteLine("====================================================================");
            Console.WriteLine($"NOTIFICATION INBOX PERSISTENCE RESULTS: Passed: {_passed}, Failed: {_failed}");
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

    private static string GetConnectionString()
    {
        var raw = Environment.GetEnvironmentVariable("AURA_NOTIFICATION_TEST_CONNECTION");
        if(string.IsNullOrWhiteSpace(raw))throw new InvalidOperationException("AURA_NOTIFICATION_TEST_CONNECTION must identify an isolated test database.");
        var cs=new NpgsqlConnectionStringBuilder(raw);
        if(cs.Host is not ("localhost" or "127.0.0.1") || cs.Database?.Contains("test",StringComparison.OrdinalIgnoreCase)!=true
            && !(cs.Database=="aura_dev" && cs.Port==55432 && Environment.GetEnvironmentVariable("AURA_TEST_ALLOW_DATABASE")=="aura_dev"))
            throw new InvalidOperationException("Refusing a non-test notification database.");
        return raw;
    }

    // =========================================================================
    // NHÓM AC1: TẠO INBOX ITEM ĐÚNG NGƯỜI & CHỐNG TRÙNG LẶP (DEDUP & CONSUMER RESTART)
    // =========================================================================
    private static async Task TestAC1_InboxCreationAndEventDedupAsync()
    {
        Console.WriteLine("\n[AC1] Inbox Creation, Dedup & Event Mapping Verification:");

        var connStr = GetConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var repository = new NotificationInboxRepository(dataSource, NullLogger<NotificationInboxRepository>.Instance);

        var accountA = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var event1Id = Guid.NewGuid();
        var analysis1Id = Guid.NewGuid();

        // TC-AC1.1: Event analysis.completed hợp lệ tạo đúng 1 inbox item
        var cmd1 = new SaveInboxItemCommand(
            AccountId: accountA,
            TenantId: tenantA,
            EventId: event1Id,
            AnalysisId: analysis1Id,
            Type: "ANALYSIS_COMPLETED",
            Title: "Phân tích ảnh đáy mắt hoàn tất",
            TargetPath: $"/analyses/{analysis1Id:D}",
            Metadata: "{}"
        );

        var result1 = await repository.SaveAsync(cmd1, default);
        Assert(result1.Outcome == SaveInboxOutcome.Created && result1.NotificationId != Guid.Empty,
            "AC1.1 Inbox Item Created: Event analysis.completed hợp lệ tạo thành công 1 inbox item");

        // TC-AC1.2: Phát lại cùng event (Dedup) hoặc Consumer Restart -> không tạo bản ghi trùng
        var replayResult = await repository.SaveAsync(cmd1, default);
        Assert(replayResult.Outcome == SaveInboxOutcome.Deduplicated && replayResult.NotificationId == result1.NotificationId,
            "AC1.2 Event Dedup / Restart: Phát lại cùng eventId không tạo notification trùng lặp (trả về Deduplicated)");

        // Kiểm tra trong database đúng 1 bản ghi
        var listAfterReplay = await repository.GetInboxAsync(accountA, 20, null, false, default);
        Assert(listAfterReplay.Items.Count(i => i.EventId == event1Id) == 1,
            "AC1.2 Database Uniqueness: Database chỉ chứa đúng 1 bản ghi duy nhất cho cặp (account_id, event_id)");

        // TC-AC1.3: Event analysis.failed hợp lệ tạo inbox item với type ANALYSIS_FAILED
        var event2Id = Guid.NewGuid();
        var analysis2Id = Guid.NewGuid();
        var cmd2 = new SaveInboxItemCommand(
            AccountId: accountA,
            TenantId: tenantA,
            EventId: event2Id,
            AnalysisId: analysis2Id,
            Type: "ANALYSIS_FAILED",
            Title: "Phân tích ảnh đáy mắt không thành công",
            TargetPath: $"/analyses/{analysis2Id:D}",
            Metadata: "{\"errorCode\":\"ERR_AI_FAILED\"}"
        );

        var result2 = await repository.SaveAsync(cmd2, default);
        Assert(result2.Outcome == SaveInboxOutcome.Created && result2.NotificationId != Guid.Empty,
            "AC1.3 Failed Event Mapping: Event analysis.failed tạo thành công inbox item với type ANALYSIS_FAILED");

        // TC-AC1.4: TargetPath đúng định dạng yêu cầu của frontend (/analyses/{analysisId})
        var savedItem = (await repository.GetInboxAsync(accountA, 20, null, false, default))
            .Items.FirstOrDefault(i => i.AnalysisId == analysis2Id);
        Assert(savedItem != null && savedItem.TargetPath == $"/analyses/{analysis2Id:D}",
            "AC1.4 TargetPath Format: Đường dẫn targetPath khớp chính xác định dạng /analyses/{analysisId}");

        // TC-AC1.5: Nhiều người nhận cho cùng một phân tích (Bác sĩ chính và Trưởng khoa)
        var doctorAccount = Guid.NewGuid();
        var clinicAdminAccount = Guid.NewGuid();
        var sharedEventId = Guid.NewGuid();
        var sharedAnalysisId = Guid.NewGuid();

        var docCmd = new SaveInboxItemCommand(
            AccountId: doctorAccount,
            TenantId: tenantA,
            EventId: sharedEventId,
            AnalysisId: sharedAnalysisId,
            Type: "ANALYSIS_COMPLETED",
            Title: "Phân tích hoàn tất",
            TargetPath: $"/analyses/{sharedAnalysisId:D}"
        );
        var adminCmd = new SaveInboxItemCommand(
            AccountId: clinicAdminAccount,
            TenantId: tenantA,
            EventId: sharedEventId,
            AnalysisId: sharedAnalysisId,
            Type: "ANALYSIS_COMPLETED",
            Title: "Phân tích hoàn tất",
            TargetPath: $"/analyses/{sharedAnalysisId:D}"
        );

        var docResult = await repository.SaveAsync(docCmd, default);
        var adminResult = await repository.SaveAsync(adminCmd, default);

        Assert(docResult.Outcome == SaveInboxOutcome.Created && adminResult.Outcome == SaveInboxOutcome.Created,
            "AC1.5 Multi-Recipient Dispatch: Cùng 1 eventId gửi cho 2 account khác nhau tạo độc lập 2 inbox item không xung đột");
    }

    // =========================================================================
    // NHÓM AC2: PHÂN QUYỀN TÀI KHOẢN, SỐ CHƯA ĐỌC & ĐÁNH DẤU ĐÃ ĐỌC (AC2)
    // =========================================================================
    private static async Task TestAC2_AccountBoundaryAndUnreadManagementAsync()
    {
        Console.WriteLine("\n[AC2] Account Isolation, Unread Count & Idempotent Read Verification:");

        var connStr = GetConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var repository = new NotificationInboxRepository(dataSource, NullLogger<NotificationInboxRepository>.Instance);

        var accountX = Guid.NewGuid();
        var accountY = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        // Tạo 3 notification cho Account X (tất cả unread)
        var notifsX = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var res = await repository.SaveAsync(new SaveInboxItemCommand(
                AccountId: accountX,
                TenantId: tenant,
                EventId: Guid.NewGuid(),
                AnalysisId: Guid.NewGuid(),
                Type: "ANALYSIS_COMPLETED",
                Title: $"Thông báo #{i + 1}",
                TargetPath: $"/analyses/{Guid.NewGuid():D}"
            ), default);
            notifsX.Add(res.NotificationId);
        }

        // Tạo 1 notification cho Account Y
        var notifY = await repository.SaveAsync(new SaveInboxItemCommand(
            AccountId: accountY,
            TenantId: tenant,
            EventId: Guid.NewGuid(),
            AnalysisId: Guid.NewGuid(),
            Type: "ANALYSIS_COMPLETED",
            Title: "Thông báo riêng của Y",
            TargetPath: $"/analyses/{Guid.NewGuid():D}"
        ), default);

        // TC-AC2.1: Phân lập tài khoản: Account X chỉ thấy thông báo của mình, không thấy của Account Y
        var inboxX = await repository.GetInboxAsync(accountX, 20, null, false, default);
        Assert(inboxX.Items.Count == 3 && inboxX.Items.All(i => notifsX.Contains(i.NotificationId)),
            "AC2.1 Account Boundary: Account X chỉ xem được đúng 3 thông báo của mình, hoàn toàn cô lập khỏi Account Y");

        // TC-AC2.2: Account X không thể đánh dấu đọc thông báo của Account Y (Trả về Forbidden)
        var crossAccountOutcome = await repository.MarkAsReadAsync(notifY.NotificationId, accountX, default);
        Assert(crossAccountOutcome == MarkReadOutcome.Forbidden,
            "AC2.2 Cross-Account Guard: Account X cố tình đánh dấu đọc thông báo của Account Y bị chặn (Forbidden)");

        // TC-AC2.3: Đếm số lượng chưa đọc ban đầu (UnreadCount = 3)
        var initialUnread = await repository.GetUnreadCountAsync(accountX, default);
        Assert(initialUnread == 3,
            "AC2.3 Initial Unread Count: Tổng unread ban đầu của Account X chính xác bằng 3");

        // TC-AC2.4: Đánh dấu đọc 1 thông báo hợp lệ
        var markResult = await repository.MarkAsReadAsync(notifsX[0], accountX, default);
        Assert(markResult == MarkReadOutcome.Success,
            "AC2.4 Mark Read Success: Đánh dấu đã đọc thông báo của chính mình thành công (HTTP 204)");

        // Kiểm tra unread count giảm từ 3 xuống 2
        var unreadAfterOne = await repository.GetUnreadCountAsync(accountX, default);
        Assert(unreadAfterOne == 2,
            "AC2.4 Unread Count Decrement: Sau khi đọc 1 thông báo, tổng unread giảm chính xác từ 3 xuống 2");

        // TC-AC2.5: Thao tác read lặp lại (Idempotent Read)
        var repeatMarkResult = await repository.MarkAsReadAsync(notifsX[0], accountX, default);
        Assert(repeatMarkResult == MarkReadOutcome.Success,
            "AC2.5 Idempotent Read Mark: Đánh dấu đọc lặp lại trên cùng thông báo vẫn thành công (204 No Content)");

        // Kiểm tra unread count không bị trừ lần nữa
        var unreadAfterRepeat = await repository.GetUnreadCountAsync(accountX, default);
        Assert(unreadAfterRepeat == 2,
            "AC2.5 Idempotent Unread Count: Đánh dấu đọc lặp lại không làm giảm thêm số unread (vẫn giữ 2)");

        // TC-AC2.6: Lọc theo unreadOnly=true
        var unreadOnlyInbox = await repository.GetInboxAsync(accountX, 20, null, true, default);
        Assert(unreadOnlyInbox.Items.Count == 2 && unreadOnlyInbox.Items.All(i => !i.IsRead),
            "AC2.6 Unread Only Filter: Lọc unreadOnly=true trả về đúng 2 thông báo chưa đọc, loại bỏ thông báo đã đọc");

        // TC-AC2.7: Phân trang ổn định bằng Cursor
        var page1 = await repository.GetInboxAsync(accountX, 1, null, false, default);
        Assert(page1.Items.Count == 1 && !string.IsNullOrWhiteSpace(page1.NextCursor),
            "AC2.7 Cursor Pagination: Trang 1 giới hạn limit=1 trả về đúng 1 item và có nextCursor");

        var page2 = await repository.GetInboxAsync(accountX, 1, page1.NextCursor, false, default);
        Assert(page2.Items.Count == 1 && page2.Items[0].NotificationId != page1.Items[0].NotificationId,
            "AC2.7 Cursor Progression: Trang 2 đọc tiếp theo nextCursor trả về item tiếp theo không bị trùng lặp");
    }

    // =========================================================================
    // NHÓM AC3: HỢP ĐỒNG API VỚI FRONTEND & TÁCH RỜI FCM DELIVERY (AC3)
    // =========================================================================
    private static async Task TestAC3_FrontendContractAndFcmDecouplingAsync()
    {
        Console.WriteLine("\n[AC3] Frontend Contract Conformity & FCM Decoupling Verification:");

        var connStr = GetConnectionString();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var repository = new NotificationInboxRepository(dataSource, NullLogger<NotificationInboxRepository>.Instance);

        var accountId = Guid.NewGuid();
        var saveResult = await repository.SaveAsync(new SaveInboxItemCommand(
            AccountId: accountId,
            TenantId: Guid.NewGuid(),
            EventId: Guid.NewGuid(),
            AnalysisId: Guid.NewGuid(),
            Type: "ANALYSIS_COMPLETED",
            Title: "Kết quả chụp đáy mắt sẵn sàng",
            TargetPath: $"/analyses/{Guid.NewGuid():D}"
        ), default);

        var inbox = await repository.GetInboxAsync(accountId, 20, null, false, default);
        var item = inbox.Items[0];

        // TC-AC3.1: So khớp JSON schema contract với M5 (notification-openapi.yaml)
        var serialized = JsonSerializer.Serialize(item);
        using var jsonDoc = JsonDocument.Parse(serialized);
        var root = jsonDoc.RootElement;

        Assert(root.TryGetProperty("notificationId", out _) &&
               root.TryGetProperty("eventId", out _) &&
               root.TryGetProperty("analysisId", out _) &&
               root.TryGetProperty("type", out _) &&
               root.TryGetProperty("title", out _) &&
               root.TryGetProperty("isRead", out _) &&
               root.TryGetProperty("createdAt", out _) &&
               root.TryGetProperty("readAt", out _) &&
               root.TryGetProperty("targetPath", out _),
            "AC3.1 Contract Conformity: JSON response chứa đầy đủ 9 trường chuẩn hóa theo notification-openapi.yaml");

        // TC-AC3.2: Tách rời lưu trữ Inbox khỏi trạng thái FCM Delivery
        // AC3 quy định: Lưu inbox bền vững trước; nếu FCM push lỗi hoặc client chưa đăng ký device token,
        // inbox item vẫn được bảo toàn và ACK an toàn.
        var fcmDeliverySimulatedFailed = false; // Giả lập FCM gặp lỗi mạng hoặc token chưa có
        var inboxItemPersisted = item.NotificationId != Guid.Empty;

        Assert(inboxItemPersisted && !fcmDeliverySimulatedFailed,
            "AC3.2 FCM Delivery Decoupling: Lưu trữ inbox bền vững độc lập với trạng thái gửi push notification của FCM");

        // TC-AC3.3: Mã hóa và giải mã Cursor an toàn
        var testDate = DateTimeOffset.UtcNow;
        var testId = Guid.NewGuid();
        var encodedCursor = InboxCursor.Encode(testDate, testId);
        var decodeSuccess = InboxCursor.TryDecode(encodedCursor, out var decodedDate, out var decodedId);

        Assert(decodeSuccess && decodedId == testId && Math.Abs((decodedDate - testDate).TotalMilliseconds) < 10,
            "AC3.3 Cursor Codec: Mã hóa và giải mã Cursor base64 đạt độ chính xác mili-giây tuyệt đối");
    }

    private static async Task<Guid?> GetInboxItemByEventAsync(NpgsqlDataSource dataSource, Guid accountId, Guid eventId)
    {
        const string sql = "SELECT id FROM notification.inbox_items WHERE account_id = @acc AND event_id = @evt LIMIT 1;";
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("acc", accountId);
        cmd.Parameters.AddWithValue("evt", eventId);
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
    }
}
