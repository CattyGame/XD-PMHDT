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
        var read = action is "CAMPAIGN_READ" or "BILLING_READ" or "REGISTRY_READ";
        if (status != "ACTIVE" && !(status == "SUSPENDED" && read)) return new(false, Guid.Empty, role, "CLINIC_INACTIVE");
        var doctor = role == "DOCTOR" && account.Roles.Contains("DOCTOR", StringComparer.Ordinal);
        var allowed = action switch
        {
            "CAMPAIGN_READ" => role is "CLINIC_ADMIN" or "STAFF" || doctor,
            "CAMPAIGN_MANAGE" or "CAMPAIGN_EXPORT" or "BILLING_MANAGE" or "BILLING_READ" => role == "CLINIC_ADMIN",
            "ASSIGNMENT_READ" => role == "CLINIC_ADMIN" || doctor,
            "REGISTRY_READ" => role is "CLINIC_ADMIN" or "STAFF" || doctor,
            "REGISTRY_MANAGE" => role is "CLINIC_ADMIN" or "STAFF",
            _ => false
        };
        return new(allowed, allowed ? tenant : Guid.Empty, role, allowed ? null : "ROLE_DENIED");
    }

    private async Task<AccessResolveResponse> ResolvePatientAccessCoreAsync(Guid actor, Guid patient, Guid? clinic, string action, Guid? analysis, CancellationToken ct)
    {
        AccessResolveResponse Deny(string reason) => new(false, null, patient, null, null, null, null, 1, reason);
        if (actor == Guid.Empty || patient == Guid.Empty || action is not ("ANALYSIS_CREATE" or "ANALYSIS_READ" or "ANALYSIS_FILE_READ" or "DOCTOR_REVIEW")) return Deny("INVALID_REQUEST");
        var account=await AccountAsync(actor,ct);
        if (account?.AccountStatus != "ACTIVE") return Deny("ACCOUNT_DISABLED");
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
        Guid tenant,owner; Guid? linked;
        Guid? clinicTenant; string? status,role; bool assigned;
        await using(var reader=await p.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))return Deny("PATIENT_NOT_FOUND");
            tenant=reader.GetGuid(0);linked=reader.IsDBNull(1)?null:reader.GetGuid(1);owner=reader.GetGuid(2);
            clinicTenant=reader.IsDBNull(3)?null:reader.GetGuid(3);
            status=reader.IsDBNull(4)?null:reader.GetString(4);
            role=reader.IsDBNull(5)?null:reader.GetString(5);
            assigned=reader.GetBoolean(6);
        }
        if(clinic is null)
        {
            if(action=="DOCTOR_REVIEW" || owner!=actor || linked!=actor)return Deny("NOT_OWNER");
            return new(true,"ACTIVE",patient,tenant,"PERSONAL",actor,new BillingOwnerDto("ACCOUNT",actor),1);
        }
        if(clinicTenant is null || role is null)return Deny("NOT_A_MEMBER");
        if(tenant!=clinicTenant || owner!=clinic.Value)return Deny("WRONG_CLINIC");
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
