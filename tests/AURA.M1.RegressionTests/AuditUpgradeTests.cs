using AURA.Common.Audit;
using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditUpgradeTests
{
    [M1PostgresFact]
    public async Task Release_migration_revokes_legacy_audit_mutation_and_preserves_analysis_and_ledger_rows()
    {
        await using var db=await Database.CreateAsync();
        var tenant=Guid.NewGuid();var owner=Guid.NewGuid();var requester=Guid.NewGuid();var patient=Guid.NewGuid();
        var batch=Guid.NewGuid();var request=Guid.NewGuid();var image=Guid.NewGuid();var job=Guid.NewGuid();
        var result=Guid.NewGuid();var review=Guid.NewGuid();var report=Guid.NewGuid();var wallet=Guid.NewGuid();var ledger=Guid.NewGuid();
        await db.SqlAsync("""
            INSERT INTO analysis.analysis_requests(id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,request_fingerprint,request_schema_version,accepted_response,correlation_id)
            VALUES($1,$2,$3,$4,$5,$6,repeat('a',64),'fixture-v1','{}'::jsonb,'scrum418-upgrade');
            """,request,tenant,owner,requester,patient,Guid.NewGuid());
        await db.SqlAsync("""
            INSERT INTO analysis.batches(id,tenant_id,owner_id,requester_id,patient_id,total_images,completed_images,status)
            VALUES($1,$2,$3,$4,$5,1,1,'COMPLETED');
            """,batch,tenant,owner,requester,patient);
        await db.SqlAsync("""
            INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,batch_id,modality,eye_side,object_key,mime_type,size_bytes,width,height,sha256,processing_status,review_status)
            VALUES($1,$2,$3,$4,$5,'FUNDUS','left','scrum418/upgrade.jpg','image/jpeg',1,1,1,repeat('b',64),'COMPLETED','REVIEWED');
            """,image,tenant,request,Guid.NewGuid(),batch);
        await db.SqlAsync("""
            INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,correlation_id)
            VALUES($1,$2,$3,$4,1,'SUCCEEDED','scrum418-upgrade');
            """,job,tenant,image,Guid.NewGuid());
        await db.SqlAsync("""
            INSERT INTO analysis.analysis_results(id,tenant_id,image_id,job_id,version,result_schema_version,risk_assessment,metrics,is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations)
            VALUES($1,$2,$3,$4,1,'fixture-v1','{}'::jsonb,'{}'::jsonb,false,'model-fixture','config-fixture','threshold-fixture',1,'migration fixture');
            """,result,tenant,image,job);
        await db.SqlAsync("""
            INSERT INTO analysis.doctor_reviews(id,tenant_id,image_id,analysis_result_id,doctor_id,version,base_version,is_approved)
            VALUES($1,$2,$3,$4,$5,1,0,true);
            """,review,tenant,image,result,Guid.NewGuid());
        await db.SqlAsync("""
            INSERT INTO analysis.report_snapshots(id,tenant_id,batch_id,analysis_id,requester_id,format,object_key,content_type,snapshot,snapshot_sha256,expires_at)
            VALUES($1,$2,$3,$4,$5,'pdf','scrum418/report.pdf','application/pdf','{}'::jsonb,repeat('c',64),now()+interval '1 day');
            """,report,tenant,batch,image,requester);
        await db.SqlAsync("""
            INSERT INTO billing.credit_wallets(id,owner_type,owner_id,environment,balance)
            VALUES($1,'ACCOUNT',$2,'TEST',10);
            """,wallet,owner);
        await db.SqlAsync("""
            INSERT INTO billing.credit_ledger_entries(id,wallet_id,tenant_id,entry_type,amount,balance_after,idempotency_key,request_fingerprint)
            VALUES($1,$2,$3,'CREDIT',10,10,$4,repeat('d',64));
            """,ledger,wallet,tenant,Guid.NewGuid());

        // This is the permission state of a database that ran the historical
        // grant migration and already recorded 015 as applied.
        await db.SqlAsync("GRANT UPDATE, DELETE ON profile.clinic_audit_logs TO aura_profile_app;");
        var upgrade=await File.ReadAllTextAsync(MigrationPath("022_release_runtime_audit_hardening.sql"));
        await db.SqlAsync(upgrade);
        await db.SqlAsync(upgrade);

        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('aura_profile_app','profile.clinic_audit_logs','UPDATE');"));
        Assert.False(await db.ScalarAsync<bool>("SELECT has_table_privilege('aura_profile_app','profile.clinic_audit_logs','DELETE');"));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM analysis.analysis_results WHERE id=$1",result));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM analysis.doctor_reviews WHERE id=$1",review));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM analysis.report_snapshots WHERE id=$1",report));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM billing.credit_ledger_entries WHERE id=$1",ledger));
    }

    [M1PostgresFact]
    public async Task M6_history_survives_shared_audit_upgrade_and_reapply_without_schema_collision()
    {
        await using var db=await Database.CreateAsync();var legacy=Guid.NewGuid();
        // Only this uniquely owned clone is changed into the pre-M1 M6 schema.
        var name=new NpgsqlConnectionStringBuilder(db.Admin.ConnectionString).Database!;
        Assert.StartsWith("aura_m1_test_",name);
        await db.SqlAsync("""
            DROP TABLE profile.audit_outbox;
            DROP TABLE profile.audit_events;
            ALTER TABLE profile.clinic_lifecycle_audit_events RENAME TO audit_events;
            """);
        await db.SqlAsync("""
            INSERT INTO profile.audit_events(event_id,source_service,action,resource_type,resource_id_hash,correlation_id,outcome)
            VALUES($1,'AURA.Profile.Api','CLINIC_STATUS_UPDATE','CLINIC',repeat('a',64),'legacy-fixture','ALLOW');
            """,legacy);
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"infra/postgres/migrations/019_shared_audit.sql"))) root=root.Parent;
        Assert.NotNull(root);
        async Task Apply(string file)
        {
            var sql=await File.ReadAllTextAsync(Path.Combine(root!.FullName,"infra/postgres/migrations",file));
            var isolated=sql.Replace("current_database() <> 'aura_dev'",$"current_database() <> '{name}'",StringComparison.Ordinal);
            Assert.NotEqual(sql,isolated);
            await db.SqlAsync(isolated);
        }
        await Apply("019_admin_lifecycle_and_chat_authority.sql");
        await Apply("019_shared_audit.sql");
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.clinic_lifecycle_audit_events WHERE event_id=$1",legacy));
        var e=AuditEvent.Create(AuditSource.Profile,Guid.NewGuid(),AuditAction.ClinicStatusChanged,AuditResource.Clinic,Guid.NewGuid().ToString("D"),"upgrade",resourceVersion:2);
        await new AuditStore(db.Profile,AuditSource.Profile).AppendAsync(e,default);
        await Apply("019_admin_lifecycle_and_chat_authority.sql");
        await Apply("019_shared_audit.sql");
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.clinic_lifecycle_audit_events WHERE event_id=$1",legacy));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_events WHERE event_id=$1",e.EventId));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_outbox WHERE event_id=$1",e.EventId));
    }

    private static string MigrationPath(string name)
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"infra/postgres/migrations/019_shared_audit.sql"))) root=root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName,"infra/postgres/migrations",name);
    }
}
