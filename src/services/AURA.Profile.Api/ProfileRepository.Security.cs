using AURA.Profile.Api.Models;
using Npgsql;

namespace AURA.Profile.Api;

public sealed record ClinicAccessRequest(Guid ActorAccountId, Guid ClinicId, string Action);
public sealed record ClinicAccessDecision(bool Allowed, Guid TenantId, string? Role, string? Reason = null);

public partial class ProfileRepository
{
    private Task<AccountAuthoritySnapshot?> AccountAsync(Guid id, CancellationToken ct) =>
        (_accountAuthority ?? throw new InvalidOperationException("Identity authority not configured.")).GetAsync(id, ct);

    public async Task<ClinicAccessDecision> ResolveClinicAccessAsync(Guid actor, Guid clinic, string action, CancellationToken ct = default)
    {
        var account = await AccountAsync(actor, ct);
        if (account?.AccountStatus != "ACTIVE") return new(false, Guid.Empty, null, "ACCOUNT_DISABLED");
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT c.tenant_id,c.status,m.role FROM profile.clinics c
            JOIN profile.memberships m ON m.clinic_id=c.clinic_id AND m.account_id=$2 AND m.status='ACTIVE'
            WHERE c.clinic_id=$1;
            """, connection);
        cmd.Parameters.AddWithValue(clinic); cmd.Parameters.AddWithValue(actor);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(false, Guid.Empty, null, "NOT_A_MEMBER");
        var tenant=reader.GetGuid(0); var status=reader.GetString(1); var role=reader.GetString(2);
        var read = action is "CAMPAIGN_READ" or "BILLING_READ" or "REGISTRY_READ" or "ANALYTICS_READ";
        if (status != "ACTIVE" && !(status == "SUSPENDED" && read)) return new(false, Guid.Empty, role, "CLINIC_INACTIVE");
        var doctor = role == "DOCTOR" && account.Roles.Contains("DOCTOR", StringComparer.Ordinal);
        var (allowed, deniedReason) = action switch
        {
            "CAMPAIGN_READ" => (role is "CLINIC_ADMIN" or "STAFF" || doctor, "ROLE_DENIED"),
            "CAMPAIGN_MANAGE" or "CAMPAIGN_EXPORT" or "BILLING_MANAGE" or "BILLING_READ" => (role == "CLINIC_ADMIN", "ROLE_DENIED"),
            "ASSIGNMENT_READ" => (role == "CLINIC_ADMIN" || doctor, "ROLE_DENIED"),
            "REGISTRY_READ" => (role is "CLINIC_ADMIN" or "STAFF" || doctor, "ROLE_DENIED"),
            "REGISTRY_MANAGE" => (role is "CLINIC_ADMIN" or "STAFF", "ROLE_DENIED"),
            "ANALYTICS_READ" or "RESEARCH_EXPORT" => (role == "CLINIC_ADMIN" || doctor, "ROLE_DENIED"),
            _ => (false, "UNKNOWN_OPERATION")
        };
        return new(allowed, allowed ? tenant : Guid.Empty, role, allowed ? null : deniedReason);
    }

    private sealed record PatientScope(Guid Tenant, Guid? Linked, Guid Owner, Guid? ClinicTenant,
        string? Status, string? Role, bool Assigned);

    private async Task<PatientScope?> ReadPatientScopeAsync(Guid actor, Guid patient, Guid? clinic, Guid? analysis, CancellationToken ct)
    {
        await using var connection=await _dataSource.OpenConnectionAsync(ct);
        await using var p=new NpgsqlCommand("""
            SELECT p.tenant_id,p.linked_account_id,p.owner_principal_id,c.tenant_id,c.status,m.role,
                   EXISTS(SELECT 1 FROM profile.doctor_assignments a WHERE a.clinic_id=$2::uuid
                     AND a.doctor_account_id=$3 AND a.patient_id=$1 AND a.status='ACTIVE'
                     AND (a.analysis_id IS NULL OR ($4::uuid IS NOT NULL AND a.analysis_id=$4)))
            FROM profile.patients p
            LEFT JOIN profile.clinics c ON c.clinic_id=$2::uuid
            LEFT JOIN profile.memberships m ON m.clinic_id=c.clinic_id AND m.account_id=$3 AND m.status='ACTIVE'
            WHERE p.patient_id=$1 AND p.status='ACTIVE'
            """,connection);
        p.Parameters.AddWithValue(patient);
        p.Parameters.AddWithValue((object?)clinic??DBNull.Value);
        p.Parameters.AddWithValue(actor);
        p.Parameters.AddWithValue((object?)analysis??DBNull.Value);
        await using var reader=await p.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        return new(reader.GetGuid(0),reader.IsDBNull(1)?null:reader.GetGuid(1),reader.GetGuid(2),
            reader.IsDBNull(3)?null:reader.GetGuid(3),reader.IsDBNull(4)?null:reader.GetString(4),
            reader.IsDBNull(5)?null:reader.GetString(5),reader.GetBoolean(6));
    }

    private async Task<AccessResolveResponse> ResolvePatientAccessCoreAsync(Guid actor, Guid patient, Guid? clinic, string action, Guid? analysis, CancellationToken ct)
    {
        AccessResolveResponse Deny(string reason) => new(false, null, patient, null, null, null, null, 1, reason);
        var isConsultation = action is "CONSULTATION_CREATE" or "CONSULTATION_JOIN" or "CONSULTATION_READ" or "CONSULTATION_SEND" or "CHAT_PARTICIPATE";
        if (actor == Guid.Empty || patient == Guid.Empty || (!isConsultation && action is not ("ANALYSIS_CREATE" or "ANALYSIS_READ" or "ANALYSIS_FILE_READ" or "DOCTOR_REVIEW")))
            return Deny("INVALID_REQUEST");
        // These independent reads overlap; both live authorities must complete before any grant.
        var accountRead=AccountAsync(actor,ct);
        var scopeRead=ReadPatientScopeAsync(actor,patient,clinic,analysis,ct);
        await Task.WhenAll(accountRead,scopeRead);
        var account=await accountRead;
        if (account?.AccountStatus != "ACTIVE") return Deny("ACCOUNT_DISABLED");
        var scope=await scopeRead;
        if(scope is null)return Deny("PATIENT_NOT_FOUND");
        var (tenant,linked,owner,clinicTenant,status,role,assigned)=scope;
        if(clinic is null)
        {
            if(isConsultation || action=="DOCTOR_REVIEW" || owner!=actor || linked!=actor)return Deny("NOT_OWNER");
            return new(true,"ACTIVE",patient,tenant,"PERSONAL",actor,new BillingOwnerDto("ACCOUNT",actor),1);
        }
        if (clinicTenant is null) return Deny("NOT_A_MEMBER");
        var isPatientParticipant = linked.HasValue && linked.Value == actor && account.Roles.Contains("PATIENT", StringComparer.Ordinal);
        if (!isConsultation && role is null) return Deny("NOT_A_MEMBER");
        if (isConsultation && !isPatientParticipant && role is null) return Deny("NOT_ASSIGNED");
        if (tenant != clinicTenant || owner != clinic.Value) return Deny("WRONG_CLINIC");
        if (isConsultation)
        {
            if (status != "ACTIVE") return Deny(status == "SUSPENDED" ? "CLINIC_SUSPENDED" : "CLINIC_INACTIVE");
            var isAssignedDoctor = role == "DOCTOR" && account.Roles.Contains("DOCTOR", StringComparer.Ordinal) && assigned;
            if (!isPatientParticipant && !isAssignedDoctor) return Deny("NOT_ASSIGNED");
            return new(true, "ACTIVE", patient, tenant, "CLINIC", clinic.Value, new BillingOwnerDto("CLINIC", clinic.Value), 1);
        }
        var reading=action is "ANALYSIS_READ" or "ANALYSIS_FILE_READ";
        if(status!="ACTIVE" && !(status=="SUSPENDED" && reading))return Deny("CLINIC_INACTIVE");
        var allowed=role=="CLINIC_ADMIN" && action!="DOCTOR_REVIEW";
        if(role=="STAFF")allowed=action=="ANALYSIS_CREATE";
        if(role=="DOCTOR" && account.Roles.Contains("DOCTOR",StringComparer.Ordinal))
        {
            allowed=action=="ANALYSIS_CREATE" || assigned;
        }
        return allowed?new(true,"ACTIVE",patient,tenant,"CLINIC",clinic.Value,new BillingOwnerDto("CLINIC",clinic.Value),1):Deny("NOT_ASSIGNED");
    }

    public async Task<ConsultationAccessResolveResponse> ResolveConsultationAccessAsync(
        ConsultationAccessResolveRequest request,
        CancellationToken ct = default)
    {
        ConsultationAccessResolveResponse Deny(string reason, bool revoked) => new(
            Allowed: false,
            Revoked: revoked,
            Code: revoked ? "ACCESS_REVOKED" : reason,
            Reason: reason,
            TenantId: null,
            ClinicContextId: request.ClinicContextId == Guid.Empty ? null : request.ClinicContextId,
            PatientId: request.PatientId,
            PatientAccountId: null,
            AnalysisId: request.AnalysisId == Guid.Empty ? null : request.AnalysisId,
            ParticipantRole: null,
            DoctorAccountId: request.DoctorAccountId,
            AssignmentId: null,
            PermissionVersion: 1,
            EligibleDoctors: Array.Empty<ConsultationDoctorDto>());

        if (request.ActorAccountId == Guid.Empty
            || request.ClinicContextId == Guid.Empty
            || request.AnalysisId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Action)
            || !ConsultationActions.All.Contains(request.Action.Trim()))
        {
            return Deny("INVALID_REQUEST", false);
        }

        var bindingTask = (_analysisBindingAuthority ?? throw new InvalidOperationException("Analysis binding authority not configured."))
            .GetAsync(request.AnalysisId, ct);
        var actorTask = AccountAsync(request.ActorAccountId, ct);
        await Task.WhenAll(bindingTask, actorTask);

        var actorAccount = await actorTask;
        if (actorAccount?.AccountStatus != "ACTIVE")
        {
            return Deny("ACCOUNT_DISABLED", true);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        Guid clinicTenantId;
        string clinicStatus;
        string? actorClinicRole = null;
        const string clinicSql = @"
            SELECT c.tenant_id, c.status, m.role
            FROM profile.clinics c
            LEFT JOIN profile.memberships m
              ON m.clinic_id = c.clinic_id
             AND m.account_id = $2
             AND m.status = 'ACTIVE'
            WHERE c.clinic_id = $1
            LIMIT 1;";

        await using (var clinicCmd = new NpgsqlCommand(clinicSql, connection))
        {
            clinicCmd.Parameters.AddWithValue(request.ClinicContextId);
            clinicCmd.Parameters.AddWithValue(request.ActorAccountId);
            await using var reader = await clinicCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return Deny("CLINIC_NOT_FOUND", false);
            }

            clinicTenantId = reader.GetGuid(0);
            clinicStatus = reader.GetString(1);
            actorClinicRole = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var binding = await bindingTask;
        if (binding is null)
        {
            // Non-members must receive a generic authorization denial rather than probing analysis existence.
            if (actorClinicRole is null)
            {
                return Deny("NOT_AUTHORIZED_PARTICIPANT", true);
            }
            return Deny("ANALYSIS_NOT_FOUND", false);
        }

        if (request.PatientId.HasValue && request.PatientId.Value != binding.PatientId)
        {
            return Deny("PATIENT_MISMATCH", false);
        }

        Guid patientTenantId;
        Guid patientOwnerId;
        Guid? patientLinkedAccountId;

        const string patientSql = @"
            SELECT tenant_id, owner_principal_id, linked_account_id
            FROM profile.patients
            WHERE patient_id = $1 AND status = 'ACTIVE'
            LIMIT 1;";

        await using (var patientCmd = new NpgsqlCommand(patientSql, connection))
        {
            patientCmd.Parameters.AddWithValue(binding.PatientId);
            await using var reader = await patientCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return actorClinicRole is null
                    ? Deny("NOT_AUTHORIZED_PARTICIPANT", true)
                    : Deny("PATIENT_OR_CLINIC_NOT_FOUND", false);
            }

            patientTenantId = reader.GetGuid(0);
            patientOwnerId = reader.GetGuid(1);
            patientLinkedAccountId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
        }

        var isLinkedPatient = patientLinkedAccountId.HasValue
            && patientLinkedAccountId.Value == request.ActorAccountId
            && actorAccount.Roles.Contains("PATIENT", StringComparer.Ordinal);

        if (!isLinkedPatient && actorClinicRole is null)
        {
            return Deny("NOT_AUTHORIZED_PARTICIPANT", true);
        }

        if (clinicTenantId != binding.TenantId || patientTenantId != clinicTenantId || patientOwnerId != request.ClinicContextId)
        {
            return Deny("WRONG_CLINIC", false);
        }

        if (!string.Equals(clinicStatus, ClinicStatuses.Active, StringComparison.Ordinal))
        {
            return Deny(clinicStatus == ClinicStatuses.Suspended ? "CLINIC_SUSPENDED" : "CLINIC_INACTIVE", true);
        }

        const string assignSql = @"
            SELECT da.assignment_id, da.doctor_account_id, COALESCE(pr.display_name, 'Bác sĩ')
            FROM profile.doctor_assignments da
            JOIN profile.memberships m
              ON m.clinic_id = da.clinic_id
             AND m.account_id = da.doctor_account_id
             AND m.role = 'DOCTOR'
             AND m.status = 'ACTIVE'
            LEFT JOIN profile.profiles pr ON pr.account_id = da.doctor_account_id
            WHERE da.clinic_id = $1
              AND da.patient_id = $2
              AND da.status = 'ACTIVE'
              AND (da.analysis_id IS NULL OR da.analysis_id = $3)
            ORDER BY da.assigned_at DESC, da.doctor_account_id ASC;";

        var candidates = new List<(Guid AssignmentId, Guid DoctorId, string DisplayName)>();
        await using (var assignCmd = new NpgsqlCommand(assignSql, connection))
        {
            assignCmd.Parameters.AddWithValue(request.ClinicContextId);
            assignCmd.Parameters.AddWithValue(binding.PatientId);
            assignCmd.Parameters.AddWithValue(binding.AnalysisId);
            await using var reader = await assignCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidates.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
            }
        }

        var eligibleDoctors = new List<ConsultationDoctorDto>();
        var assignmentIdByDoctor = new Dictionary<Guid, Guid>();
        var doctorAccountChecks = new Dictionary<Guid, bool>();

        foreach (var (assignmentId, doctorId, displayName) in candidates)
        {
            if (assignmentIdByDoctor.ContainsKey(doctorId))
            {
                continue;
            }

            if (!doctorAccountChecks.TryGetValue(doctorId, out var doctorCapable))
            {
                var docSnapshot = doctorId == request.ActorAccountId
                    ? actorAccount
                    : await AccountAsync(doctorId, ct);
                doctorCapable = docSnapshot?.AccountStatus == "ACTIVE"
                    && docSnapshot.Roles.Contains("DOCTOR", StringComparer.Ordinal);
                doctorAccountChecks[doctorId] = doctorCapable;
            }

            if (!doctorCapable)
            {
                continue;
            }

            assignmentIdByDoctor[doctorId] = assignmentId;
            eligibleDoctors.Add(new ConsultationDoctorDto(doctorId, displayName));
        }

        var isPatient = patientLinkedAccountId.HasValue
            && patientLinkedAccountId.Value == request.ActorAccountId
            && actorAccount.Roles.Contains("PATIENT", StringComparer.Ordinal);
        var isAssignedDoctor = assignmentIdByDoctor.ContainsKey(request.ActorAccountId);

        if (!isPatient && !isAssignedDoctor)
        {
            return Deny("NOT_AUTHORIZED_PARTICIPANT", true);
        }

        if (!string.Equals(request.Action.Trim(), ConsultationActions.Doctors, StringComparison.OrdinalIgnoreCase)
            && eligibleDoctors.Count == 0)
        {
            return Deny("DOCTOR_ASSIGNMENT_REVOKED", true);
        }

        var participantRole = isAssignedDoctor ? "DOCTOR" : "PATIENT";
        Guid? resolvedDoctorId = isAssignedDoctor ? request.ActorAccountId : null;
        Guid? resolvedAssignmentId = isAssignedDoctor ? assignmentIdByDoctor[request.ActorAccountId] : null;

        if (request.DoctorAccountId.HasValue)
        {
            if (request.DoctorAccountId.Value == Guid.Empty)
            {
                return Deny("INVALID_DOCTOR", false);
            }

            if (isAssignedDoctor && request.DoctorAccountId.Value != request.ActorAccountId)
            {
                return Deny("DOCTOR_MISMATCH", true);
            }

            if (!assignmentIdByDoctor.TryGetValue(request.DoctorAccountId.Value, out var matchedAssignmentId))
            {
                return Deny("DOCTOR_ASSIGNMENT_REVOKED", true);
            }

            resolvedDoctorId = request.DoctorAccountId.Value;
            resolvedAssignmentId = matchedAssignmentId;
        }

        return new ConsultationAccessResolveResponse(
            Allowed: true,
            Revoked: false,
            Code: null,
            Reason: null,
            TenantId: binding.TenantId,
            ClinicContextId: request.ClinicContextId,
            PatientId: binding.PatientId,
            PatientAccountId: patientLinkedAccountId,
            AnalysisId: binding.AnalysisId,
            ParticipantRole: participantRole,
            DoctorAccountId: resolvedDoctorId,
            AssignmentId: resolvedAssignmentId,
            PermissionVersion: 1,
            EligibleDoctors: eligibleDoctors);
    }

    public async Task<OperationAuthorityResponse> ResolveOperationAuthorityAsync(
        OperationAuthorityRequest request,
        CancellationToken ct = default)
    {
        OperationAuthorityResponse DenyOp(string reason, bool revoked = false) => new(
            PermissionVersion: 1,
            Allowed: false,
            Revoked: revoked,
            Code: revoked ? "ACCESS_REVOKED" : reason,
            Reason: reason,
            TenantId: null,
            ClinicId: request.ClinicId,
            PatientId: request.PatientId,
            ParticipantRole: null,
            AuthorizedPatientIds: null,
            IncludeMasksAllowed: false);

        var op = request.Operation?.Trim() ?? string.Empty;
        if (!CanonicalOperations.All.Contains(op))
        {
            return DenyOp("UNKNOWN_OPERATION");
        }

        if (request.ActorAccountId == Guid.Empty)
        {
            return DenyOp("INVALID_REQUEST");
        }

        var actorAccount = await AccountAsync(request.ActorAccountId, ct);
        if (actorAccount?.AccountStatus != "ACTIVE")
        {
            return DenyOp("ACCOUNT_DISABLED", revoked: true);
        }

        if (op == CanonicalOperations.ChatParticipate)
        {
            if (request.ClinicId is not { } chatClinicId || chatClinicId == Guid.Empty
                || request.AnalysisId is not { } chatAnalysisId || chatAnalysisId == Guid.Empty)
            {
                return DenyOp("INVALID_REQUEST");
            }

            var chatDecision = await ResolveConsultationAccessAsync(
                new ConsultationAccessResolveRequest(
                    request.ActorAccountId,
                    chatClinicId,
                    chatAnalysisId,
                    ConsultationActions.ChatParticipate,
                    request.DoctorAccountId,
                    request.PatientId),
                ct);

            return new OperationAuthorityResponse(
                PermissionVersion: chatDecision.PermissionVersion,
                Allowed: chatDecision.Allowed,
                Revoked: chatDecision.Revoked,
                Code: chatDecision.Code,
                Reason: chatDecision.Reason,
                TenantId: chatDecision.TenantId,
                ClinicId: chatDecision.ClinicContextId,
                PatientId: chatDecision.PatientId,
                ParticipantRole: chatDecision.ParticipantRole,
                AuthorizedPatientIds: null,
                IncludeMasksAllowed: false);
        }

        var scope = string.IsNullOrWhiteSpace(request.ResourceScope)
            ? (request.ClinicId.HasValue ? (request.PatientId.HasValue ? "PATIENT" : "CLINIC") : "PLATFORM")
            : request.ResourceScope.Trim().ToUpperInvariant();

        if (op == CanonicalOperations.AnalyticsRead && scope == "PLATFORM")
        {
            if (request.ClinicId.HasValue || request.PatientId.HasValue)
            {
                return DenyOp("INVALID_REQUEST");
            }

            if (!actorAccount.Roles.Contains("SYSTEM_ADMIN", StringComparer.Ordinal))
            {
                return DenyOp("ROLE_DENIED");
            }

            return new OperationAuthorityResponse(
                PermissionVersion: 1,
                Allowed: true,
                Revoked: false,
                Code: null,
                Reason: null,
                TenantId: null,
                ClinicId: null,
                PatientId: null,
                ParticipantRole: "SYSTEM_ADMIN",
                AuthorizedPatientIds: null,
                IncludeMasksAllowed: false);
        }

        if (op == CanonicalOperations.AnalyticsRead && scope is not ("CLINIC" or "DOCTOR_ASSIGNMENT" or "PATIENT"))
        {
            return DenyOp("INVALID_REQUEST");
        }

        if (request.ClinicId is not { } clinicId || clinicId == Guid.Empty)
        {
            return DenyOp("INVALID_REQUEST");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        Guid clinicTenantId;
        string clinicStatus;
        string? memberRole;
        const string clinicMemberSql = @"
            SELECT c.tenant_id, c.status, m.role
            FROM profile.clinics c
            LEFT JOIN profile.memberships m
              ON m.clinic_id = c.clinic_id
             AND m.account_id = $2
             AND m.status = 'ACTIVE'
            WHERE c.clinic_id = $1
            LIMIT 1;";

        await using (var cmd = new NpgsqlCommand(clinicMemberSql, connection))
        {
            cmd.Parameters.AddWithValue(clinicId);
            cmd.Parameters.AddWithValue(request.ActorAccountId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return DenyOp("CLINIC_NOT_FOUND");
            }

            clinicTenantId = reader.GetGuid(0);
            clinicStatus = reader.GetString(1);
            memberRole = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (memberRole is null)
        {
            return actorAccount.Roles.Contains("SYSTEM_ADMIN", StringComparer.Ordinal)
                ? DenyOp("ADMIN_NO_PHI_WILDCARD")
                : DenyOp("NOT_A_MEMBER", revoked: true);
        }

        var isClinicalDoctor = memberRole == "DOCTOR"
            && actorAccount.Roles.Contains("DOCTOR", StringComparer.Ordinal);

        if (op == CanonicalOperations.AnalyticsRead)
        {
            if (clinicStatus is not ("ACTIVE" or "SUSPENDED"))
            {
                return DenyOp("CLINIC_INACTIVE");
            }

            if (scope == "CLINIC")
            {
                if (request.PatientId.HasValue) return DenyOp("INVALID_REQUEST");
                if (memberRole != "CLINIC_ADMIN") return DenyOp("ROLE_DENIED");

                return new OperationAuthorityResponse(
                    PermissionVersion: 1,
                    Allowed: true,
                    Revoked: false,
                    Code: null,
                    Reason: null,
                    TenantId: clinicTenantId,
                    ClinicId: clinicId,
                    PatientId: null,
                    ParticipantRole: "CLINIC_ADMIN",
                    AuthorizedPatientIds: null,
                    IncludeMasksAllowed: false);
            }

            if (scope == "DOCTOR_ASSIGNMENT")
            {
                if (request.PatientId.HasValue) return DenyOp("INVALID_REQUEST");
                if (!isClinicalDoctor) return DenyOp("ROLE_DENIED");

                var assignedPatients = await ReadAssignedPatientIdsAsync(
                    connection, clinicId, clinicTenantId, request.ActorAccountId, ct);

                return new OperationAuthorityResponse(
                    PermissionVersion: 1,
                    Allowed: true,
                    Revoked: false,
                    Code: null,
                    Reason: null,
                    TenantId: clinicTenantId,
                    ClinicId: clinicId,
                    PatientId: null,
                    ParticipantRole: "DOCTOR",
                    AuthorizedPatientIds: assignedPatients,
                    IncludeMasksAllowed: false);
            }

            if (request.PatientId is not { } analyticsPatientId || analyticsPatientId == Guid.Empty)
            {
                return DenyOp("INVALID_REQUEST");
            }

            if (memberRole != "CLINIC_ADMIN" && !isClinicalDoctor)
            {
                return DenyOp("ROLE_DENIED");
            }

            var (found, wrongClinic, assigned) = await ReadPatientScopeAssignmentAsync(
                connection, clinicId, clinicTenantId, analyticsPatientId, request.ActorAccountId, ct);
            if (!found) return DenyOp("PATIENT_NOT_FOUND");
            if (wrongClinic) return DenyOp("WRONG_CLINIC");
            if (memberRole != "CLINIC_ADMIN" && !assigned) return DenyOp("NOT_ASSIGNED", revoked: true);

            return new OperationAuthorityResponse(
                PermissionVersion: 1,
                Allowed: true,
                Revoked: false,
                Code: null,
                Reason: null,
                TenantId: clinicTenantId,
                ClinicId: clinicId,
                PatientId: analyticsPatientId,
                ParticipantRole: memberRole,
                AuthorizedPatientIds: null,
                IncludeMasksAllowed: false);
        }

        // RESEARCH_EXPORT
        if (!string.Equals(clinicStatus, ClinicStatuses.Active, StringComparison.Ordinal))
        {
            return DenyOp(
                clinicStatus == ClinicStatuses.Suspended ? "CLINIC_SUSPENDED" : "CLINIC_INACTIVE",
                revoked: clinicStatus == ClinicStatuses.Suspended);
        }

        if (memberRole != "CLINIC_ADMIN" && !isClinicalDoctor)
        {
            return DenyOp("ROLE_DENIED");
        }

        var cleanPurpose = request.Purpose?.Trim();
        if (string.IsNullOrWhiteSpace(cleanPurpose) || cleanPurpose.Length > 500)
        {
            return DenyOp("PURPOSE_REQUIRED");
        }

        if (request.IncludeMasks && !request.MasksConsentGranted)
        {
            return DenyOp("MASK_POLICY_CONSENT_REQUIRED");
        }

        IReadOnlyList<Guid>? exportAuthorizedPatients = null;
        if (request.PatientId.HasValue)
        {
            if (request.PatientId.Value == Guid.Empty) return DenyOp("INVALID_REQUEST");

            var (found, wrongClinic, assigned) = await ReadPatientScopeAssignmentAsync(
                connection, clinicId, clinicTenantId, request.PatientId.Value, request.ActorAccountId, ct);
            if (!found) return DenyOp("PATIENT_NOT_FOUND");
            if (wrongClinic) return DenyOp("WRONG_CLINIC");
            if (memberRole != "CLINIC_ADMIN" && !assigned) return DenyOp("NOT_ASSIGNED", revoked: true);
        }
        else if (memberRole != "CLINIC_ADMIN")
        {
            exportAuthorizedPatients = await ReadAssignedPatientIdsAsync(
                connection, clinicId, clinicTenantId, request.ActorAccountId, ct);
            if (exportAuthorizedPatients.Count == 0)
            {
                return DenyOp("NOT_ASSIGNED", revoked: true);
            }
        }

        return new OperationAuthorityResponse(
            PermissionVersion: 1,
            Allowed: true,
            Revoked: false,
            Code: null,
            Reason: null,
            TenantId: clinicTenantId,
            ClinicId: clinicId,
            PatientId: request.PatientId,
            ParticipantRole: memberRole,
            AuthorizedPatientIds: exportAuthorizedPatients,
            IncludeMasksAllowed: request.IncludeMasks && request.MasksConsentGranted);
    }

    private static async Task<IReadOnlyList<Guid>> ReadAssignedPatientIdsAsync(
        NpgsqlConnection connection,
        Guid clinicId,
        Guid clinicTenantId,
        Guid doctorAccountId,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT da.patient_id
            FROM profile.doctor_assignments da
            JOIN profile.patients p ON p.patient_id = da.patient_id
            WHERE da.clinic_id = $1
              AND da.doctor_account_id = $2
              AND da.status = 'ACTIVE'
              AND p.status = 'ACTIVE'
              AND p.tenant_id = $3
              AND p.owner_principal_id = $1
            ORDER BY da.patient_id ASC
            LIMIT 500;";

        var ids = new List<Guid>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.AddWithValue(doctorAccountId);
        cmd.Parameters.AddWithValue(clinicTenantId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    private static async Task<(bool Found, bool WrongClinic, bool Assigned)> ReadPatientScopeAssignmentAsync(
        NpgsqlConnection connection,
        Guid clinicId,
        Guid clinicTenantId,
        Guid patientId,
        Guid doctorAccountId,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT p.tenant_id, p.owner_principal_id,
                   EXISTS(
                       SELECT 1 FROM profile.doctor_assignments da
                       WHERE da.clinic_id = $2
                         AND da.doctor_account_id = $3
                         AND da.patient_id = $1
                         AND da.status = 'ACTIVE'
                   )
            FROM profile.patients p
            WHERE p.patient_id = $1 AND p.status = 'ACTIVE'
            LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.AddWithValue(doctorAccountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (false, false, false);
        }

        var patientTenantId = reader.GetGuid(0);
        var patientOwnerId = reader.GetGuid(1);
        var assigned = reader.GetBoolean(2);
        return (true, patientTenantId != clinicTenantId || patientOwnerId != clinicId, assigned);
    }

    public async Task<(bool Success,ClinicPatientListResponse? Response,string? ErrorCode,string? ErrorMessage)> GetClinicPatientsForActorAsync(
        Guid actor,Guid clinic,string? search,string? status,int limit,int offset,CancellationToken ct)
    {
        var policy=await ResolveClinicAccessAsync(actor,clinic,"REGISTRY_READ",ct);
        if(!policy.Allowed)return(false,null,"ERR_FORBIDDEN","Patient registry access denied.");
        var result=await GetClinicPatientsAsync(clinic,search,status,limit,offset,ct,policy.Role=="DOCTOR"?actor:null);
        if(policy.Role=="STAFF" && result.Response is {} response)
            result=(result.Success,response with { Items=response.Items.Select(p=>p with {MedicalHistory=null}).ToArray() },result.ErrorCode,result.ErrorMessage);
        return result;
    }
    public async Task<bool> CanAccessRegistryPatientAsync(Guid actor,Guid clinic,Guid patient,bool mutate,CancellationToken ct)
    {
        var policy=await ResolveClinicAccessAsync(actor,clinic,"REGISTRY_READ",ct);
        if(!policy.Allowed)return false;
        if(mutate)
        {
            await using var c=await _dataSource.OpenConnectionAsync(ct);
            await using var cmd=new NpgsqlCommand("SELECT status FROM profile.clinics WHERE clinic_id=$1",c);cmd.Parameters.AddWithValue(clinic);
            if((string?)await cmd.ExecuteScalarAsync(ct)!="ACTIVE")return false;
        }
        if(policy.Role=="DOCTOR")return(await ResolvePatientAccessCoreAsync(actor,patient,clinic,"ANALYSIS_READ",null,ct)).Allowed;
        return policy.Role is "CLINIC_ADMIN" or "STAFF";
    }

    public async Task<ClinicAccessDecision> ResolveBillingOwnerAsync(Guid actor,string ownerType,Guid ownerId,string action,CancellationToken ct)
    {
        if(action is not ("BILLING_READ" or "BILLING_MANAGE"))return new(false,Guid.Empty,null);
        if(ownerType=="CLINIC")return await ResolveClinicAccessAsync(actor,ownerId,action,ct);
        var account=await AccountAsync(actor,ct);
        if(ownerType!="ACCOUNT" || actor!=ownerId || account?.AccountStatus!="ACTIVE")return new(false,Guid.Empty,null);
        await using var connection=await _dataSource.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("SELECT tenant_id FROM profile.tenants WHERE tenant_kind='PERSONAL' AND owner_principal_id=$1",connection);
        cmd.Parameters.AddWithValue(actor);var tenant=await cmd.ExecuteScalarAsync(ct);
        return tenant is Guid id?new(true,id,null):new(false,Guid.Empty,null,"PROFILE_NOT_READY");
    }

    public async Task<IReadOnlyList<ActorClinic>> ListActorClinicsAsync(Guid actor,CancellationToken ct)
    {
        var account=await AccountAsync(actor,ct);
        if(account?.AccountStatus!="ACTIVE")return Array.Empty<ActorClinic>();
        await using var connection=await _dataSource.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("""
            SELECT c.clinic_id,c.name,m.role,m.status FROM profile.memberships m
            JOIN profile.clinics c ON c.clinic_id=m.clinic_id
            WHERE m.account_id=$1 AND m.status='ACTIVE' AND c.status='ACTIVE' ORDER BY c.name,c.clinic_id
            """,connection);
        cmd.Parameters.AddWithValue(actor);await using var reader=await cmd.ExecuteReaderAsync(ct);
        var items=new List<ActorClinic>();
        while(await reader.ReadAsync(ct))items.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)));
        return items;
    }
}

public sealed record BillingOwnerResolveRequest(Guid ActorAccountId,string OwnerType,Guid OwnerId,string Action);

public sealed record ActorClinic(Guid ClinicId,string ClinicName,string Role,string Status);
