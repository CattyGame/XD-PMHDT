using AURA.Identity.Api;
using Npgsql;

internal static class IdentityConnectionChecks
{
    public static async Task RunAsync(NpgsqlDataSource admin,string connectionString)
    {
        var id=Guid.NewGuid();var uid="m6-pool-"+id.ToString("N");
        async Task Sql(string text, params object[] values)
        {
            await using var cmd=admin.CreateCommand(text);
            foreach(var value in values)cmd.Parameters.AddWithValue(value);
            await cmd.ExecuteNonQueryAsync();
        }
        await Sql("INSERT INTO identity.accounts(account_id,firebase_uid,account_status,profile_provisioning_status) VALUES($1,$2,'ACTIVE','READY')",id,uid);
        await Sql("INSERT INTO identity.account_roles(account_id,role_name) VALUES($1,'PATIENT'),($1,'DOCTOR')",id);
        var connection=new NpgsqlConnectionStringBuilder(connectionString){Options="-c role=aura_identity_app",MaxPoolSize=1,Timeout=2};
        await using var runtime=NpgsqlDataSource.Create(connection.ConnectionString);
        var repository=new IdentityRepository(runtime);
        var first=await repository.GetAccountByIdAsync(id);
        if(first?.FirebaseUid!=uid || !first.Roles.Contains("DOCTOR"))throw new Exception("Identity by-id positive control failed.");
        await Sql("DELETE FROM identity.account_roles WHERE account_id=$1 AND role_name='DOCTOR'",id);
        await Sql("UPDATE identity.accounts SET account_status='DISABLED' WHERE account_id=$1",id);
        var next=await repository.GetAccountByIdAsync(id);
        if(next?.AccountStatus!="DISABLED" || next.Roles.Contains("DOCTOR"))throw new Exception("Authority must re-read current status and roles.");
        Console.WriteLine("Identity DB PASS: by-id lookup uses one pool slot and reads role/status changes immediately (aura_identity_app).");
    }
}
