using AURA.Profile.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AURA.Profile.Api;

public static class ProfilePatientEndpoints
{
    public static void MapProfilePatientEndpoints(this WebApplication app)
    {
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

            var registryPolicy = await repository.ResolveClinicAccessAsync(identity.AccountId,clinicId,"REGISTRY_MANAGE",ct);
            allowed = allowed && registryPolicy.Allowed;
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

            if (registryPolicy.Role == "STAFF" && body.MedicalHistory is not null) return Results.StatusCode(403);

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

            return Results.Created($"/api/v1/clinics/{clinicId}/patients/{patient!.PatientId}", registryPolicy.Role == "STAFF" ? patient with { MedicalHistory = null } : patient);
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

            var (success, response, errCode, errMsg) = await repository.GetClinicPatientsForActorAsync(
                identity.AccountId, clinicId, search, status, limit, offset, ct);

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

            allowed = allowed && await repository.CanAccessRegistryPatientAsync(identity.AccountId,clinicId,patientId,false,ct);
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

            var registryPolicy=await repository.ResolveClinicAccessAsync(identity.AccountId,clinicId,"REGISTRY_READ",ct);
            return Results.Ok(registryPolicy.Role=="STAFF" ? patient! with {MedicalHistory=null} : patient);
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

            allowed = allowed && await repository.CanAccessRegistryPatientAsync(identity.AccountId,clinicId,patientId,true,ct);
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

            var registryPolicy = await repository.ResolveClinicAccessAsync(identity.AccountId,clinicId,"REGISTRY_READ",ct);
            if(registryPolicy.Role=="STAFF" && body.MedicalHistory is not null)return Results.StatusCode(403);
            var (success, updated, errCode, errMsg) = await repository.UpdateClinicPatientAsync(clinicId, patientId, body, ct);
            if (!success)
            {
                var statusCode = errCode == "ERR_PATIENT_NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                return Results.Json(
                    new ErrorEnvelope(new ErrorDetails(errCode ?? "ERR_INTERNAL_DB", errMsg ?? "Không thể cập nhật hồ sơ bệnh nhân.", correlationId, false)),
                    statusCode: statusCode);
            }

            return Results.Ok(registryPolicy.Role == "STAFF" ? updated! with { MedicalHistory = null } : updated);
        })
        .WithName("UpdateClinicPatient")
        .WithTags("ClinicPatients");

    }
}
