using AURA.Common.Audit;
using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditUpgradeTests
{
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
}
