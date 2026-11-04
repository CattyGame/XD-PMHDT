using AURA.Profile.Api;
using Npgsql;

internal static class DatabaseChecks
{
    public static async Task RunAsync(string connectionString)
    {
        var builder=new NpgsqlConnectionStringBuilder(connectionString);
        if(!builder.Database!.Contains("test",StringComparison.OrdinalIgnoreCase) && !(builder.Database=="aura_dev" && builder.Host=="127.0.0.1" && builder.Port==55432 && Environment.GetEnvironmentVariable("AURA_TEST_ALLOW_DATABASE")=="aura_dev"))throw new InvalidOperationException("Database checks require an isolated test database.");
        await using var admin=NpgsqlDataSource.Create(connectionString);
        var tenant=Guid.NewGuid();var clinic=Guid.NewGuid();var otherTenant=Guid.NewGuid();var otherClinic=Guid.NewGuid();
        var owner=Guid.NewGuid();var doctor=Guid.NewGuid();var doctor2=Guid.NewGuid();var pendingDoctor=Guid.NewGuid();var staff=Guid.NewGuid();var stranger=Guid.NewGuid();
        var patient=Guid.NewGuid();var unassigned=Guid.NewGuid();var caseId=Guid.NewGuid();
        var authority=new Accounts(new Dictionary<Guid,AccountAuthoritySnapshot>
        {
            [owner]=new(owner,"ACTIVE",new[]{"PATIENT"}),[doctor]=new(doctor,"ACTIVE",new[]{"DOCTOR"}),
            [doctor2]=new(doctor2,"ACTIVE",new[]{"DOCTOR"}),[pendingDoctor]=new(pendingDoctor,"ACTIVE",new[]{"PATIENT"}),
            [staff]=new(staff,"ACTIVE",new[]{"PATIENT"}),[stranger]=new(stranger,"ACTIVE",new[]{"SYSTEM_ADMIN"})
        });
        var bindings=new Bindings(new(caseId,tenant,patient));
        async Task Sql(string sql,params object[] values)
        {
            await using var cmd=admin.CreateCommand(sql);foreach(var v in values)cmd.Parameters.AddWithValue(v);await cmd.ExecuteNonQueryAsync();
        }
        await Sql("INSERT INTO profile.tenants(tenant_id,tenant_kind,owner_principal_id) VALUES($1,'CLINIC',$2),($3,'CLINIC',$4)",tenant,clinic,otherTenant,otherClinic);
        await Sql("INSERT INTO profile.clinics(clinic_id,tenant_id,name,status) VALUES($1,$2,'security fixture','ACTIVE'),($3,$4,'other fixture','ACTIVE')",clinic,tenant,otherClinic,otherTenant);
        foreach(var (id,role) in new[]{(owner,"CLINIC_ADMIN"),(doctor,"DOCTOR"),(doctor2,"DOCTOR"),(pendingDoctor,"DOCTOR"),(staff,"STAFF")})
            await Sql("INSERT INTO profile.memberships(clinic_id,account_id,role,status) VALUES($1,$2,$3,'ACTIVE')",clinic,id,role);
        await Sql("INSERT INTO profile.patients(patient_id,tenant_id,owner_principal_id,full_name,medical_history) VALUES($1,$3,$4,'assigned','{\"diabetes\":true}'),($2,$3,$4,'unassigned','{\"diabetes\":true}')",patient,unassigned,tenant,clinic);
        builder.Options="-c role=aura_profile_app";
        await using var runtime=NpgsqlDataSource.Create(builder.ConnectionString);
        var repository=new ProfileRepository(runtime,authority,bindings);
        var passed=0;
        void Verify(bool condition,string name){if(!condition)throw new InvalidOperationException("DB FAIL: "+name);Console.WriteLine("DB PASS: "+name);passed++;}

        Verify((await repository.ListActorClinicsAsync(owner,default)).Single().ClinicId==clinic && (await repository.ListActorClinicsAsync(stranger,default)).Count==0,"Clinic discovery returns only actor active memberships");
        Verify((await repository.ResolveClinicAccessAsync(owner,clinic,"CAMPAIGN_MANAGE")).Allowed,"ClinicAdmin can manage own campaign with global PATIENT");
        Verify(!(await repository.ResolveClinicAccessAsync(owner,otherClinic,"CAMPAIGN_MANAGE")).Allowed,"Cross-clinic campaign denied");
        Verify(!(await repository.ResolveClinicAccessAsync(doctor,clinic,"CAMPAIGN_MANAGE")).Allowed,"Doctor cannot manage campaigns");
        Verify(!(await repository.ResolveClinicAccessAsync(stranger,clinic,"REGISTRY_READ")).Allowed,"Global SYSTEM_ADMIN has no automatic PHI grant");
        Verify(!(await repository.ResolveAccessAsync(doctor,patient,clinic,"ANALYSIS_READ")).Allowed,"Unassigned Doctor cannot read patient");
        var assignment=await repository.CreateOrReactivateDoctorAssignmentAsync(clinic,doctor,patient,caseId);
        Verify(assignment.Success,"Create case assignment using authoritative Analysis binding");
        Verify((await repository.ResolveAccessAsync(doctor,patient,clinic,"ANALYSIS_READ",caseId)).Allowed,"Case-only assignment permits exactly that case");
        Verify(!(await repository.ResolveAccessAsync(doctor,patient,clinic,"ANALYSIS_READ",Guid.NewGuid())).Allowed,"Case-only assignment does not grant another case");
        Verify(!(await repository.ResolveAccessAsync(doctor,unassigned,clinic,"ANALYSIS_READ",caseId)).Allowed,"Analysis ID cannot bypass patient binding");
        Verify(!(await repository.CanAccessRegistryPatientAsync(doctor,clinic,patient,false,default)),"Case-only assignment does not expose longitudinal registry detail");
        var list=await repository.GetDoctorAssignmentsAsync(clinic,doctor,new[]{"DOCTOR"},null,null,null);
        Verify(list.Success && list.Items!.Single().AnalysisId==caseId,"Assignment response retains analysisId");
        var invalid=await repository.CreateOrReactivateDoctorAssignmentAsync(clinic,doctor,unassigned,caseId);
        Verify(!invalid.Success,"Assignment rejects case belonging to another patient");
        var unapproved=await repository.CreateOrReactivateDoctorAssignmentAsync(clinic,pendingDoctor,unassigned);
        Verify(!unapproved.Success,"Membership invitation does not grant global Doctor capability");
        var transfer=await repository.TransferDoctorAssignmentAsync(clinic,assignment.Assignment!.AssignmentId,Guid.NewGuid());
        Verify(!transfer.Success && (await repository.ResolveAccessAsync(doctor,patient,clinic,"ANALYSIS_READ",caseId)).Allowed,"Invalid transfer rolls back revocation");
        var moved=await repository.TransferDoctorAssignmentAsync(clinic,assignment.Assignment.AssignmentId,doctor2);
        Verify(moved.Success && !(await repository.ResolveAccessAsync(doctor,patient,clinic,"ANALYSIS_READ",caseId)).Allowed && (await repository.ResolveAccessAsync(doctor2,patient,clinic,"ANALYSIS_READ",caseId)).Allowed,"Atomic transfer revokes old and grants new Doctor");
        var patientWide=await repository.CreateOrReactivateDoctorAssignmentAsync(clinic,doctor,patient);
        Verify(patientWide.Success,"Patient-wide assignment positive control");
        var registry=await repository.GetClinicPatientsForActorAsync(doctor,clinic,null,null,20,0,default);
        Verify(registry.Success && registry.Response!.TotalCount==1 && registry.Response.Items.Single().PatientId==patient,"Registry count and rows filtered before pagination");
        var staffRegistry=await repository.GetClinicPatientsForActorAsync(staff,clinic,null,null,20,0,default);
        Verify(staffRegistry.Success && staffRegistry.Response!.Items.All(x=>x.MedicalHistory is null),"Staff administrative registry excludes medical history");
        await repository.RevokeDoctorAssignmentAsync(clinic,patientWide.Assignment!.AssignmentId);
        Verify(!(await repository.ResolveAccessAsync(doctor,patient,clinic,"ANALYSIS_READ")).Allowed,"Revocation immediately invalidates next request");
        await Sql("UPDATE profile.clinics SET status='SUSPENDED' WHERE clinic_id=$1",clinic);
        Verify(!(await repository.ResolveAccessAsync(owner,patient,clinic,"ANALYSIS_CREATE")).Allowed && !(await repository.ResolveAccessAsync(doctor2,patient,clinic,"DOCTOR_REVIEW",caseId)).Allowed,"Suspended clinic denies create and review");
        Verify((await repository.ResolveAccessAsync(doctor2,patient,clinic,"ANALYSIS_READ",caseId)).Allowed,"Suspended historical read still requires active assignment");
        await Sql("UPDATE profile.clinics SET status='ACTIVE' WHERE clinic_id=$1",clinic);
        var email=$"invite-{Guid.NewGuid():N}@example.test";
        var invite=await repository.InviteClinicMemberAsync(clinic,email,"DOCTOR");
        Verify(invite.Result?.InvitationToken is {Length:64},"Invite issues one-time random token");
        var member=invite.Result!;var invitedAccount=Guid.NewGuid();
        Verify(!(await repository.AcceptClinicInvitationExplicitAsync(invitedAccount,email,member.MembershipId,"wrong")).Succeeded,"Wrong invite token denied");
        Verify(!(await repository.AcceptClinicInvitationExplicitAsync(invitedAccount,"other@example.test",member.MembershipId,member.InvitationToken!)).Succeeded,"Invite email mismatch denied");
        Verify((await repository.AcceptClinicInvitationExplicitAsync(invitedAccount,email,member.MembershipId,member.InvitationToken!)).Succeeded,"Matching email and one-time token accept invitation");
        Verify(!(await repository.AcceptClinicInvitationExplicitAsync(invitedAccount,email,member.MembershipId,member.InvitationToken!)).Succeeded,"Consumed invite token replay denied");
        var second=await repository.InviteClinicMemberAsync(clinic,$"expiry-{Guid.NewGuid():N}@example.test","STAFF");
        await Sql("UPDATE profile.clinic_invitations SET issued_at=now()-interval '74 hours',expires_at=now()-interval '2 hours' WHERE membership_id=$1",second.Result!.MembershipId);
        Verify(!(await repository.AcceptClinicInvitationExplicitAsync(Guid.NewGuid(),second.Result.InvitedEmail,second.Result.MembershipId,second.Result.InvitationToken!)).Succeeded,"Expired invitation denied");
        var reissued=await repository.InviteClinicMemberAsync(clinic,second.Result.InvitedEmail!,"STAFF");
        Verify(reissued.Result!.InvitationToken!=second.Result.InvitationToken && !(await repository.AcceptClinicInvitationExplicitAsync(Guid.NewGuid(),second.Result.InvitedEmail,second.Result.MembershipId,second.Result.InvitationToken!)).Succeeded,"Reinvite rotates hash and invalidates old token");
        await HttpChecks.RunAsync(repository,owner,stranger,clinic);
        Console.WriteLine($"Profile PostgreSQL security: {passed}/{passed} PASS under aura_profile_app; Identity/Analysis authority boundaries are explicit test doubles.");
    }
    private sealed class Accounts(Dictionary<Guid,AccountAuthoritySnapshot> rows):IAccountAuthority
    {
        public Task<AccountAuthoritySnapshot?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult(rows.GetValueOrDefault(id));
    }
    private sealed class Bindings(AnalysisBinding binding):IAnalysisBindingAuthority
    {
        public Task<AnalysisBinding?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult<AnalysisBinding?>(id==binding.AnalysisId?binding:null);
    }
}
