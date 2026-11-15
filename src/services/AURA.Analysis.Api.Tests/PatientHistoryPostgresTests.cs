using AURA.Analysis.Api.Upload;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class PatientHistoryPostgresTests
{
    [PostgresFact]
    public async Task History_and_cursor_are_scoped_to_tenant_clinic_patient_and_keep_reviewed_cases()
    {
        await using var db=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!) {Options="-c role=aura_analysis_app"}.ConnectionString);
        var tenant=Guid.NewGuid();var clinic=Guid.NewGuid();var patient=Guid.NewGuid();
        async Task Sql(string text,params object[] values)
        {await using var cmd=db.CreateCommand(text);foreach(var value in values)cmd.Parameters.AddWithValue(value);await cmd.ExecuteNonQueryAsync();}
        async Task<Guid> Seed(Guid t,Guid owner,Guid p,int minutes,string status="QUEUED",string review="PENDING")
        {
            var batch=Guid.NewGuid();var image=Guid.NewGuid();
            await Sql("""
                INSERT INTO analysis.analysis_requests(id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,
                    request_fingerprint,request_schema_version,accepted_response,correlation_id)
                VALUES($1,$2,$3,$4,$5,$6,repeat('a',64),'0.9','{}','history-fixture')
                """,batch,t,owner,Guid.NewGuid(),p,Guid.NewGuid());
            await Sql("""
                INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,
                    mime_type,size_bytes,width,height,sha256,processing_status,review_status,created_at)
                VALUES($1,$2,$3,$4,'FUNDUS','right','history-fixture/'||$1::text,'image/jpeg',100,1000,800,repeat('b',64),$5,$6,$7)
                """,image,t,batch,Guid.NewGuid(),status,review,new DateTime(2026,11,15,0,0,0,DateTimeKind.Utc).AddMinutes(minutes));
            return image;
        }
        var old=await Seed(tenant,clinic,patient,1,"COMPLETED","REVIEWED");
        var newest=await Seed(tenant,clinic,patient,2);
        var otherPatient=await Seed(tenant,clinic,Guid.NewGuid(),3);
        await Seed(Guid.NewGuid(),clinic,patient,4);
        await Seed(tenant,Guid.NewGuid(),patient,5);
        var repository=new PostgresAnalysisQueryRepository(db);
        var first=await repository.ListAsync(tenant,clinic,patient,1,null,default);
        Assert.Equal(newest,Assert.Single(first).AnalysisId);
        var second=await repository.ListAsync(tenant,clinic,patient,1,newest,default);
        Assert.Equal(old,Assert.Single(second).AnalysisId);Assert.Equal("REVIEWED",second[0].ReviewStatus);
        Assert.Empty(await repository.ListAsync(tenant,clinic,patient,20,old,default));
        Assert.Empty(await repository.ListAsync(tenant,clinic,patient,20,otherPatient,default));
        Assert.Empty(await repository.ListAsync(tenant,clinic,patient,20,Guid.NewGuid(),default));
    }
}
