using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AURA.Notification.Api;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AURA.M1.RegressionTests;

public class NotificationTests
{
    [M1PostgresFact]
    public async Task Malformed_or_unsupported_payment_envelopes_never_create_delivery_obligations()
    {
        await using var f=await Fixture.CreateAsync();
        foreach(var value in new object[] {
            new {eventId=Guid.Empty,eventType="billing.payment.success",tenantId=f.Tenant,orderId=f.Order,schemaVersion="1.0"},
            new {eventId=Guid.NewGuid(),eventType="billing.payment.success",tenantId=f.Tenant,orderId=Guid.Empty,schemaVersion="1.0"},
            new {eventId=Guid.NewGuid(),eventType="billing.payment.success",tenantId=f.Tenant,orderId=f.Order,schemaVersion="unknown"},
            new {eventId=Guid.NewGuid(),eventType="billing.payment.success",tenantId=f.Tenant,orderId=f.Order},
        }) await Assert.ThrowsAsync<NotificationContextRejectedException>(()=>f.Consumer.ProcessNotificationEventAsync(JsonSerializer.Serialize(value),default));
        await Assert.ThrowsAsync<NotificationContextRejectedException>(()=>f.Consumer.ProcessNotificationEventAsync("[]",default));
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM notification.inbox_items"));
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM notification.delivery_jobs"));
    }

    [M1PostgresFact]
    public async Task Mixed_payment_analysis_and_batch_inbox_preserves_nullable_resource_identity()
    {
        await using var f=await Fixture.CreateAsync();
        await f.Consumer.ProcessNotificationEventAsync(f.Event("billing.payment.success"),default);
        await f.Consumer.ProcessNotificationEventAsync(f.Event("analysis.completed"),default);
        await f.Consumer.ProcessNotificationEventAsync(f.Event("batch.completed"),default);
        var page=await f.Inbox.GetInboxAsync(f.Account,20,null,false,default);
        Assert.Equal(3,page.Items.Count);
        var payment=Assert.Single(page.Items,x=>x.Type=="PAYMENT_SUCCESS");
        Assert.Null(payment.AnalysisId);Assert.Equal(f.Order,payment.OrderId);
        Assert.Equal($"/billing/orders/{f.Order:D}",payment.TargetPath);
    }

    [M1PostgresFact]
    public async Task Consumer_never_dispatches_and_partial_worker_failure_keeps_retry_only_for_failed_device()
    {
        await using var f=await Fixture.CreateAsync();
        var good=await f.DeviceAsync("good");var bad=await f.DeviceAsync("transient");
        var json=f.Event("analysis.completed");
        await f.Consumer.ProcessNotificationEventAsync(json,default);
        Assert.Equal(0,f.Provider.Calls);
        await f.Worker.DispatchPendingAsync(default);
        Assert.Equal("COMPLETED",await f.StatusAsync(good));Assert.Equal("PENDING",await f.StatusAsync(bad));
        Assert.Equal(2,f.Provider.Calls);
        await f.Consumer.ProcessNotificationEventAsync(json,default);
        Assert.Equal(2,f.Provider.Calls);
        Assert.Equal(2L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM notification.delivery_jobs"));
        Assert.All(f.Provider.Messages,m=>Assert.True(m.Data.ContainsKey("notificationId")));
    }

    [M1PostgresFact]
    public async Task Transient_failure_at_max_attempts_reaches_dead_letter_through_production_worker()
    {
        await using var f=await Fixture.CreateAsync();var device=await f.DeviceAsync("transient");
        await f.Consumer.ProcessNotificationEventAsync(f.Event("analysis.completed"),default);
        await f.Db.SqlAsync("UPDATE notification.delivery_jobs SET attempts=4,max_attempts=5");
        await f.Worker.DispatchPendingAsync(default);
        Assert.Equal("DEAD_LETTER",await f.StatusAsync(device));Assert.Equal(1,f.Provider.Calls);
    }

    [M1PostgresFact]
    public async Task Provider_outage_then_process_restart_recovers_the_persisted_job()
    {
        await using var f=await Fixture.CreateAsync();var device=await f.DeviceAsync("transient");
        await f.Consumer.ProcessNotificationEventAsync(f.Event("analysis.completed"),default);
        await f.Worker.DispatchPendingAsync(default);
        Assert.Equal("PENDING",await f.StatusAsync(device));
        Assert.True(await f.Db.ScalarAsync<bool>("SELECT next_attempt_at>now() FROM notification.delivery_jobs"));
        await f.Db.SqlAsync("UPDATE notification.delivery_jobs SET next_attempt_at=now()");
        f.Provider.Recovered=true;
        await f.NewWorker().DispatchPendingAsync(default);
        Assert.Equal("COMPLETED",await f.StatusAsync(device));
    }

    [M1PostgresFact]
    public async Task Expired_or_superseded_lease_cannot_complete_and_crash_at_limit_is_bounded()
    {
        await using var f=await Fixture.CreateAsync();await f.DeviceAsync("good");
        await f.Consumer.ProcessNotificationEventAsync(f.Event("analysis.completed"),default);
        var old=Assert.Single(await f.Jobs.ClaimPendingJobsAsync(1,TimeSpan.FromSeconds(30),default));
        await f.Db.SqlAsync("UPDATE notification.delivery_jobs SET lease_expires_at=now()-interval '1 second'");
        await f.Jobs.CompleteJobAsync(old.Id,old.LeaseToken,default);
        Assert.Equal("IN_FLIGHT",await f.Db.ScalarAsync<string>("SELECT status FROM notification.delivery_jobs"));
        var current=Assert.Single(await f.Jobs.ClaimPendingJobsAsync(1,TimeSpan.FromSeconds(30),default));
        Assert.NotEqual(old.LeaseToken,current.LeaseToken);
        await f.Jobs.CompleteJobAsync(old.Id,old.LeaseToken,default);
        Assert.Equal("IN_FLIGHT",await f.Db.ScalarAsync<string>("SELECT status FROM notification.delivery_jobs"));
        await f.Db.SqlAsync("UPDATE notification.delivery_jobs SET attempts=max_attempts,lease_expires_at=now()-interval '1 second'");
        Assert.Empty(await f.Jobs.ClaimPendingJobsAsync(1,TimeSpan.FromSeconds(30),default));
        Assert.Equal("DEAD_LETTER",await f.Db.ScalarAsync<string>("SELECT status FROM notification.delivery_jobs"));
    }

    [M1PostgresFact]
    public async Task Unregistered_cleanup_does_not_delete_a_token_rotated_during_send()
    {
        await using var f=await Fixture.CreateAsync();var device=await f.DeviceAsync("unregistered");
        f.Provider.DuringSend=()=>f.Devices.RegisterAsync("m1-tests",f.Accounts.Uid,device,"rotated-token",default);
        await f.Consumer.ProcessNotificationEventAsync(f.Event("analysis.completed"),default);
        await f.Worker.DispatchPendingAsync(default);
        Assert.Equal("rotated-token",await f.Devices.GetTokenAsync("m1-tests",f.Accounts.Uid,device,default));
        Assert.Equal("DEAD_LETTER",await f.StatusAsync(device));
    }

    [M1PostgresFact]
    public async Task Forged_payment_context_never_writes_inbox_or_jobs()
    {
        await using var f=await Fixture.CreateAsync();
        foreach(var kind in new[]{"event","order","tenant","status"})
        {
            f.Authority.Mismatch=kind;
            await Assert.ThrowsAsync<NotificationContextRejectedException>(()=>f.Consumer.ProcessNotificationEventAsync(f.Event("billing.payment.success"),default));
        }
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM notification.inbox_items"));
    }

    [Fact]
    public async Task Http_payment_resolver_uses_scoped_post_and_checks_all_authority_fields()
    {
        var e=Guid.NewGuid();var o=Guid.NewGuid();var t=Guid.NewGuid();var account=Guid.NewGuid();
        foreach(var kind in new[]{"ok","event","order","tenant","status"})
        {
            var scoped=false;
            var handler=new Handler(async request=> {
                if(request.RequestUri!.AbsolutePath.EndsWith("service-tokens")) {
                    var body=await request.Content!.ReadAsStringAsync();
                    scoped=body.Contains("billing:notify")&&body.Contains("aura-billing");
                    return new(HttpStatusCode.OK){Content=JsonContent.Create(new{accessToken="test-authority-token"})};
                }
                Assert.Equal(HttpMethod.Post,request.Method);
                Assert.Equal("/internal/v1/billing/notification-context/resolve",request.RequestUri.AbsolutePath);
                return new(HttpStatusCode.OK){Content=JsonContent.Create(new PaymentNotificationContext(kind=="event"?Guid.NewGuid():e,
                    kind=="order"?Guid.NewGuid():o,kind=="tenant"?Guid.NewGuid():t,account,kind=="status"?"PENDING":"COMPLETED"))};
            });
            var resolver=new NotificationContextResolver(new NotificationAuthorityClient(new Factory(handler),new ConfigurationBuilder().Build()));
            if(kind=="ok") Assert.Equal(account,(await resolver.ResolvePaymentAsync(e,o,t,default)).RecipientAccountId);
            else await Assert.ThrowsAsync<NotificationContextRejectedException>(()=>resolver.ResolvePaymentAsync(e,o,t,default));
            Assert.True(scoped);
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        public Database Db {get;} public Guid Account {get;}=Guid.NewGuid();public Guid Tenant {get;}=Guid.NewGuid();public Guid Order {get;}=Guid.NewGuid();
        public NotificationInboxRepository Inbox {get;} public NotificationDeliveryJobRepository Jobs {get;} public DeviceRegistrationRepository Devices {get;}
        public Accounts Accounts {get;} public Authority Authority {get;} public Provider Provider {get;}=new();
        public NotificationEventConsumer Consumer {get;} public NotificationDeliveryWorker Worker {get;}
        readonly TokenCipher _cipher;readonly IConfiguration _config;
        Fixture(Database db) {
            Db=db;_config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Firebase:ProjectId"]="m1-tests",["Notification:EncryptionKeyId"]="test-only",
                ["Notification:EncryptionKey"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}).Build();
            _cipher=new(_config);Devices=new(db.Notification,_cipher);Inbox=new(db.Notification,NullLogger<NotificationInboxRepository>.Instance);
            Jobs=new(db.Notification,NullLogger<NotificationDeliveryJobRepository>.Instance);Accounts=new(Account);Authority=new(Account);
            Consumer=new(Options.Create(new RabbitMqConsumerOptions()),Inbox,Devices,Accounts,_config,NullLogger<NotificationEventConsumer>.Instance,Authority,Jobs);
            Worker=NewWorker();
        }
        public static async Task<Fixture> CreateAsync()=>new(await Database.CreateAsync());
        public NotificationDeliveryWorker NewWorker()=>new(Jobs,Devices,Accounts,Provider,_config,NullLogger<NotificationDeliveryWorker>.Instance);
        public async Task<Guid> DeviceAsync(string token) {var id=Guid.NewGuid();await Devices.RegisterAsync("m1-tests",Accounts.Uid,id,token,default);return id;}
        public string Event(string type)=>JsonSerializer.Serialize(new{eventId=Guid.NewGuid(),schemaVersion="1.0",eventType=type,analysisId=Guid.NewGuid(),orderId=Order,tenantId=Tenant});
        public Task<string> StatusAsync(Guid device)=>Db.ScalarAsync<string>("SELECT status FROM notification.delivery_jobs WHERE device_id=$1",device);
        public async ValueTask DisposeAsync(){_cipher.Dispose();await Db.DisposeAsync();}
    }
    sealed class Accounts(Guid id):IAccountResolver {
        public string Uid {get;}="uid-"+id.ToString("N");
        public Task<Guid?> ResolveAccountIdAsync(string uid,CancellationToken ct)=>Task.FromResult<Guid?>(uid==Uid?id:null);
        public Task<string?> ResolveFirebaseUidAsync(Guid account,CancellationToken ct)=>Task.FromResult<string?>(account==id?Uid:null);
    }
    sealed class Authority(Guid recipient):INotificationContextResolver {
        public string Mismatch {get;set;}="";
        public Task<NotificationContext> ResolveAsync(Guid e,Guid a,Guid t,CancellationToken ct)=>Task.FromResult(new NotificationContext(e,a,t,true,[recipient],"COMPLETED",1));
        public Task<PaymentNotificationContext> ResolvePaymentAsync(Guid e,Guid o,Guid t,CancellationToken ct)=>Task.FromResult(new PaymentNotificationContext(
            Mismatch=="event"?Guid.NewGuid():e,Mismatch=="order"?Guid.NewGuid():o,Mismatch=="tenant"?Guid.NewGuid():t,recipient,Mismatch=="status"?"PENDING":"COMPLETED"));
    }
    sealed class Provider:IFcmClient {
        public int Calls;public bool Recovered;public Func<Task>? DuringSend;public List<MulticastMessage> Messages {get;}=[];
        public async Task<FcmMulticastResult> SendMulticastAsync(IReadOnlyList<ActiveDeviceToken> devices,MulticastMessage message,CancellationToken ct) {
            Interlocked.Increment(ref Calls);lock(Messages)Messages.Add(message);
            if(DuringSend is not null)await DuringSend();
            var d=Assert.Single(devices);var success=d.Token=="good"||Recovered;var code=d.Token=="unregistered"?"Unregistered":"Unavailable";
            return new(success?1:0,success?0:1,[new(d.Token,d.DeviceId,success,success?"test-message":null,success?null:code,null)]);
        }
    }
    sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r);
    }
    sealed class Factory(HttpMessageHandler handler):IHttpClientFactory {
        public HttpClient CreateClient(string name)=>new(handler,false){BaseAddress=new Uri("https://authority.test")};
    }
}
