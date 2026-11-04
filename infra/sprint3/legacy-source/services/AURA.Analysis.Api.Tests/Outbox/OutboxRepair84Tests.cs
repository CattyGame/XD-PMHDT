using System.Text.Json;
using AURA.Analysis.Api.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public class OutboxRepair84Tests
{
    static readonly DateTimeOffset T0=new(2026,10,29,0,0,0,TimeSpan.Zero);
    static readonly Guid Tenant=Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Other=Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid Id=Guid.Parse("33333333-3333-4333-8333-333333333333");
    static OutboxOptions Opt()=>new(){Enabled=true,MinOccurredAtUtc=T0.AddDays(-1)};
    static OutboxClaim Scope(DateTimeOffset at)=>new(at,10,TimeSpan.FromSeconds(60),10,T0.AddDays(-1),new[]{Other});
    static Dictionary<string,object?> Event(string type="analysis.requested",object? data=null)=>new(){
        ["eventId"]=Id,["eventType"]=type,["schemaVersion"]="0.7",["occurredAt"]="2026-10-29T00:00:00Z",
        ["tenantId"]=Tenant,["correlationId"]="repair84",["analysisId"]=Id,["data"]=data??new{patientId=Tenant,batchId=Id}};
    static OutboxRow Row()=>new(){Id=Id,TenantId=Tenant,EventType="analysis.requested",SchemaVersion="0.7",Payload=JsonSerializer.Serialize(Event()),OccurredAt=T0};
    static OutboxMessageMapper.MapResult Map(Dictionary<string,object?> e)=>OutboxMessageMapper.Map(new OutboxRecord(Id,Tenant,(string)e["eventType"]!,"0.7",JsonSerializer.Serialize(e),T0,1),Opt());
    static OutboxPublisher Publisher(InMemoryOutboxStore s,FakeBroker b,FakeClock c,OutboxOptions? o=null)=>new(s,b,Options.Create(o??Opt()),c,NullLogger<OutboxPublisher>.Instance);

    [Fact] public async Task Sweep_and_backlog_share_cutoff_and_tenant_scope()
    {
        var s=new InMemoryOutboxStore();var old=Row();old.Id=Guid.NewGuid();old.OccurredAt=T0.AddDays(-30);old.Attempts=10;
        var excluded=Row();excluded.Id=Guid.NewGuid();excluded.TenantId=Other;excluded.Attempts=10;var good=Row();good.Attempts=10;
        s.Rows.AddRange(new[]{old,excluded,good});Assert.Equal(1,await s.SweepExhaustedAsync(Scope(T0),default));
        Assert.Null(old.NeedsAttentionAt);Assert.Null(old.LastError);Assert.Null(excluded.NeedsAttentionAt);Assert.Null(excluded.LastError);
        Assert.Equal(10,old.Attempts);Assert.Equal(10,excluded.Attempts);Assert.NotNull(good.NeedsAttentionAt);
        Assert.Equal(1,(await s.GetBacklogAsync(Scope(T0),default)).NeedsAttention);
    }
    [Fact] public async Task Every_stale_write_is_rejected_after_another_claim()
    {
        var s=new InMemoryOutboxStore();s.Rows.Add(Row());var a=(await s.ClaimAsync(Scope(T0),default)).Single();
        var at=T0.AddSeconds(61);var b=(await s.ClaimAsync(Scope(at),default)).Single();Assert.True(b.ClaimGeneration>a.ClaimGeneration);
        var lease=s.Rows[0].LockedUntil;var attempts=s.Rows[0].Attempts;
        Assert.False(await s.RenewLeaseAsync(a,at,TimeSpan.FromSeconds(60),default));
        Assert.False(await s.MarkPublishedAsync(a,at,default));Assert.False(await s.MarkRetryAsync(a,"stale",at,at,default));
        Assert.False(await s.MarkNeedsAttentionAsync(a,"stale",at,default));Assert.Equal(0,await s.ReleaseUnattemptedAsync(new[]{a},at,default));
        Assert.Equal(lease,s.Rows[0].LockedUntil);Assert.Equal(attempts,s.Rows[0].Attempts);Assert.Null(s.Rows[0].PublishedAt);Assert.Null(s.Rows[0].NeedsAttentionAt);
        Assert.True(await s.MarkPublishedAsync(b,at,default));
    }
    [Fact] public async Task Release_does_not_reuse_a_generation_even_when_attempt_counter_repeats()
    {
        var s=new InMemoryOutboxStore();s.Rows.Add(Row());var a=(await s.ClaimAsync(Scope(T0),default)).Single();
        Assert.Equal(1,await s.ReleaseUnattemptedAsync(new[]{a},T0,default));var b=(await s.ClaimAsync(Scope(T0),default)).Single();
        Assert.Equal(a.DeliveryAttempts,b.DeliveryAttempts);Assert.True(b.ClaimGeneration>a.ClaimGeneration);Assert.False(await s.MarkRetryAsync(a,"stale",T0,T0,default));
    }
    [Fact] public async Task Expired_unreclaimed_lease_cannot_be_renewed_or_written()
    {
        var s=new InMemoryOutboxStore();s.Rows.Add(Row());var a=(await s.ClaimAsync(Scope(T0),default)).Single();var at=T0.AddSeconds(60);
        Assert.False(await s.RenewLeaseAsync(a,at,TimeSpan.FromSeconds(60),default));Assert.False(await s.MarkPublishedAsync(a,at,default));
    }
    [Fact] public async Task Lost_lease_prevents_broker_call()
    {
        var s=new InMemoryOutboxStore(){RejectRenew=_=>true};s.Rows.Add(Row());var b=new FakeBroker();var r=await Publisher(s,b,new FakeClock(T0)).RunOnceAsync(default);
        Assert.Empty(b.Attempts);Assert.Equal(0,r.Published);Assert.Null(s.Rows[0].PublishedAt);
    }
    [Fact] public async Task Confirm_after_lease_expiry_does_not_mark_published()
    {
        var s=new InMemoryOutboxStore();s.Rows.Add(Row());var c=new FakeClock(T0);var b=new FakeBroker(){Throw=_=>{c.Advance(TimeSpan.FromSeconds(61));return null;}};
        var r=await Publisher(s,b,c).RunOnceAsync(default);Assert.Single(b.Delivered);Assert.Equal(0,r.Published);Assert.Null(s.Rows[0].PublishedAt);
    }
    [Fact] public async Task Disabled_publisher_does_not_sweep_or_claim()
    {
        var s=new InMemoryOutboxStore();var row=Row();row.Attempts=10;s.Rows.Add(row);var b=new FakeBroker();var r=await Publisher(s,b,new FakeClock(T0),new OutboxOptions()).RunOnceAsync(default);
        Assert.Equal(OutboxRunResult.Empty,r);Assert.Null(row.NeedsAttentionAt);Assert.Empty(b.Attempts);
    }
    [Fact] public void Event_data_and_unknown_fields_are_strict()
    {
        Assert.False(Map(Event(data:new{})).Ok);var extra=Event();extra["unexpected"]="x";Assert.False(Map(extra).Ok);
        Assert.False(Map(Event(data:new{patientId=Tenant,batchId=Id,unexpected="x"})).Ok);Assert.True(Map(Event()).Ok);
    }
    [Fact] public void Completed_risk_invariants_are_enforced()
    {
        Dictionary<string,object?> data=new(){["patientId"]=Tenant,["riskAvailable"]=false,["riskLevel"]=null,["modelVersion"]="frangi-v0.2",["isMock"]=false,["configVersion"]="s3-frangi-1.0.0",["thresholdVersion"]="frangi-threshold-0.02-v1",["resultVersion"]=1};
        Assert.True(Map(Event("analysis.completed",data)).Ok);data["riskLevel"]="LOW";Assert.False(Map(Event("analysis.completed",data)).Ok);
        data["riskAvailable"]=true;Assert.True(Map(Event("analysis.completed",data)).Ok);data["riskLevel"]=null;Assert.False(Map(Event("analysis.completed",data)).Ok);
    }
    [Fact] public void Failed_must_be_terminal_and_nonretryable()
    {
        Dictionary<string,object?> data=new(){["patientId"]=Tenant,["errorCode"]="ERR_AI_FAILED",["retryable"]=false,["terminalStatus"]="FAILED"};
        Assert.True(Map(Event("analysis.failed",data)).Ok);data["retryable"]=true;Assert.False(Map(Event("analysis.failed",data)).Ok);data["retryable"]=false;data.Remove("terminalStatus");Assert.False(Map(Event("analysis.failed",data)).Ok);
    }
    [Fact] public void Duplicate_fields_and_wrong_numeric_types_are_rejected_without_throwing()
    {
        var text=JsonSerializer.Serialize(Event());text=text.Replace("\"data\":","\"eventType\":\"analysis.requested\",\"data\":");
        Assert.False(OutboxMessageMapper.Map(new OutboxRecord(Id,Tenant,"analysis.requested","0.7",text,T0,1),Opt()).Ok);
        var complete=Event("analysis.completed",new{patientId=Tenant,riskAvailable=false,riskLevel=(string?)null,modelVersion="x",isMock=false,configVersion="x",thresholdVersion="x",resultVersion="1"});Assert.False(Map(complete).Ok);
    }
    [Fact] public void Job_requires_exact_fields_and_positive_integer_attempt()
    {
        Dictionary<string,object?> job=new(){["messageId"]=Id,["messageType"]="analysis.job.run",["schemaVersion"]="0.6",["occurredAt"]="2026-10-29T00:00:00Z",["tenantId"]=Tenant,["correlationId"]="repair84",["jobId"]=Id,["analysisId"]=Id,["batchId"]=Id,["imageFileId"]=Id,["attempt"]=1};
        bool Valid()=>OutboxMessageMapper.Map(new OutboxRecord(Id,Tenant,"analysis.job.run","0.6",JsonSerializer.Serialize(job),T0,1),Opt()).Ok;
        Assert.True(Valid());
        foreach(var value in new object?[]{0,-1,"1",1.5,null}){job["attempt"]=value;Assert.False(Valid());}
        job["attempt"]=1;job["unexpected"]="x";Assert.False(Valid());
    }
}
