using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Identity.Api;
using AURA.Profile.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditMutationTests
{
    [M1PostgresFact]
    public async Task Clinic_membership_grant_and_revoke_cannot_commit_without_their_audit()
    {
        await using var db=await Database.CreateAsync();var actor=Guid.NewGuid();var member=Guid.NewGuid();
        await db.SqlAsync("INSERT INTO profile.profiles(account_id,display_name) VALUES($1,'fixture owner'),($2,'fixture member')",actor,member);
        var repository=new ProfileRepository(db.Profile);
        var clinic=await repository.RegisterClinicAsync(actor,new("Audit clinic",null,null,null,null));
        Assert.True((await repository.AddOrReactivateClinicMemberAsync(clinic.ClinicId,member,"STAFF",actor)).Success);
        Assert.True((await repository.RevokeClinicMemberAsync(clinic.ClinicId,member,actor)).Success);
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_events"));
        await db.SqlAsync("""
            CREATE FUNCTION profile.audit_membership_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-only membership audit failure'; END $$;
            CREATE TRIGGER audit_membership_failure BEFORE INSERT ON profile.audit_outbox FOR EACH ROW EXECUTE FUNCTION profile.audit_membership_failure();
            """);
        await Assert.ThrowsAsync<PostgresException>(()=>repository.AddOrReactivateClinicMemberAsync(clinic.ClinicId,member,"STAFF",actor));
        Assert.Equal("REVOKED",await db.ScalarAsync<string>("SELECT status FROM profile.memberships WHERE clinic_id=$1 AND account_id=$2",clinic.ClinicId,member));
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_events"));
    }
    [M1PostgresFact]
    public async Task Role_change_and_price_change_commit_owner_audit_and_rollback_when_outbox_fails()
    {
        await using var db = await Database.CreateAsync();
        var identities = new IdentityRepository(db.Identity);
        var actor = await identities.CreateAccountAsync("audit-fixture-actor-" + Guid.NewGuid());
        var target = await identities.CreateAccountAsync("audit-fixture-target-" + Guid.NewGuid());
        Assert.True(await identities.SetDoctorCapabilityAsync(actor.AccountId, target.AccountId, true, "clinical notes must not enter shared audit"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM identity.audit_events"));
        Assert.DoesNotContain("clinical notes", await db.ScalarAsync<string>("SELECT payload::text FROM identity.audit_events"));
        var packages = new PackageRepository(db.Billing);
        Assert.NotNull(await packages.UpdateAsync(1, new UpdatePackageRequest("Audit fixture", 100, 10), default, actor.AccountId, "Bearer private-token"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events"));
        await db.SqlAsync("""
            CREATE FUNCTION identity.audit_fixture_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-only storage failure'; END $$;
            CREATE TRIGGER audit_fixture_failure BEFORE INSERT ON identity.audit_outbox FOR EACH ROW EXECUTE FUNCTION identity.audit_fixture_failure();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => identities.SetDoctorCapabilityAsync(actor.AccountId, target.AccountId, false, "test"));
        Assert.Contains("DOCTOR", (await identities.GetAccountByIdAsync(target.AccountId))!.Roles);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM identity.audit_events"));
        await db.SqlAsync("""
            CREATE TRIGGER audit_fixture_failure BEFORE INSERT ON billing.audit_outbox FOR EACH ROW EXECUTE FUNCTION identity.audit_fixture_failure();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => packages.UpdateAsync(1, new("must rollback", 999, 9), default, actor.AccountId, "test"));
        Assert.Equal("Audit fixture", await db.ScalarAsync<string>("SELECT name FROM billing_poc.service_packages WHERE id=1"));
    }
}
