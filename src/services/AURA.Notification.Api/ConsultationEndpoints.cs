using System.Security.Claims;
using Npgsql;

namespace AURA.Notification.Api;

public static class ConsultationEndpoints
{
    public static IEndpointRouteBuilder MapConsultationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/consultations").WithTags("Consultations");

        group.MapPost("", async Task<IResult> (
            ConsultationCreateRequest request,
            HttpContext context,
            ConsultationService service,
            CancellationToken ct) =>
        {
            try
            {
                var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(uid)) return Error(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập.");
                var result = await service.CreateAsync(uid, request, context.Request.Headers["Idempotency-Key"].ToString(), ct);
                if (result.Outcome == CreateConsultationOutcome.PayloadConflict)
                    return Error(context, 409, "ERR_CONFLICT", "Idempotency-Key đã được dùng với nội dung khác.");
                var conversation = result.Conversation;
                var response = new ConsultationConversationResponse(conversation.ConversationId, conversation.AnalysisId,
                    conversation.ClinicContextId, conversation.Status, 0);
                return Results.Json(response, statusCode: result.Outcome == CreateConsultationOutcome.Created ? 201 : 200);
            }
            catch (ConsultationException ex) { return Error(context, ex); }
            catch (NpgsqlException) { return Error(context, 503, "ERR_STORAGE_UNAVAILABLE", "Chưa lưu được cuộc hội thoại.", true); }
        }).WithName("CreateConsultation");

        group.MapGet("", async Task<IResult> (
            int? limit,
            string? cursor,
            HttpContext context,
            ConsultationService service,
            CancellationToken ct) =>
        {
            try
            {
                var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(uid)) return Error(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập.");
                return Results.Ok(await service.ListAsync(uid, limit, cursor, ct));
            }
            catch (ConsultationException ex) { return Error(context, ex); }
            catch (NpgsqlException) { return Error(context, 503, "ERR_STORAGE_UNAVAILABLE", "Dịch vụ tư vấn tạm thời không khả dụng.", true); }
        }).WithName("ListConsultations");

        group.MapGet("/{conversationId:guid}/messages", async Task<IResult> (
            Guid conversationId,
            int? limit,
            Guid? beforeId,
            HttpContext context,
            ConsultationService service,
            CancellationToken ct) =>
        {
            try
            {
                var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(uid)) return Error(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập.");
                return Results.Ok(await service.GetMessagesAsync(uid, conversationId, limit, beforeId, ct));
            }
            catch (ConsultationException ex) { return Error(context, ex); }
            catch (NpgsqlException) { return Error(context, 503, "ERR_STORAGE_UNAVAILABLE", "Dịch vụ tư vấn tạm thời không khả dụng.", true); }
        }).WithName("GetConsultationMessages");

        group.MapPost("/{conversationId:guid}/messages", async Task<IResult> (
            Guid conversationId,
            ConsultationMessageCreateRequest request,
            HttpContext context,
            ConsultationService service,
            CancellationToken ct) =>
        {
            try
            {
                var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(uid)) return Error(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập.");
                var result = await service.SendAsync(uid, conversationId, request, ct);
                return Results.Json(result.Message, statusCode: result.Outcome == PersistConsultationMessageOutcome.Created ? 201 : 200);
            }
            catch (ConsultationException ex) { return Error(context, ex); }
            catch (NpgsqlException) { return Error(context, 503, "ERR_STORAGE_UNAVAILABLE", "Chưa lưu được tin nhắn.", true); }
        }).WithName("SendConsultationMessage");

        group.MapPost("/{conversationId:guid}/read", async Task<IResult> (
            Guid conversationId,
            ConsultationReadRequest request,
            HttpContext context,
            ConsultationService service,
            CancellationToken ct) =>
        {
            try
            {
                var uid = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(uid)) return Error(context, 401, "ERR_UNAUTHORIZED", "Cần đăng nhập.");
                var result = await service.MarkReadAsync(uid, conversationId, request, ct);
                return Results.Ok(new { id = result.Cursor!.LastReadMessageId, status = "READ", version = result.Cursor.Version });
            }
            catch (ConsultationException ex) { return Error(context, ex); }
            catch (NpgsqlException) { return Error(context, 503, "ERR_STORAGE_UNAVAILABLE", "Chưa lưu được trạng thái đã đọc.", true); }
        }).WithName("AdvanceConsultationReadCursor");

        return app;
    }

    private static IResult Error(HttpContext context, ConsultationException exception) =>
        Error(context, exception.StatusCode, exception.Code, exception.Message, exception.Retryable);

    private static IResult Error(HttpContext context, int status, string code, string message, bool retryable = false) =>
        ErrorResult(context, status, code, message, retryable);

    private static IResult ErrorResult(HttpContext context, int status, string code, string message, bool retryable)
    {
        if (status == 429) context.Response.Headers["Retry-After"] = "60";
        return Results.Json(new
        {
            error = new { code, message, correlationId = context.TraceIdentifier, retryable }
        }, statusCode: status);
    }
}
