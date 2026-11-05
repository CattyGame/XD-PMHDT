using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Billing.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AURA.M1.RegressionTests;

public class BillingTests
{
    [M1PostgresFact]
    public async Task Settlement_and_one_canonical_event_commit_together_and_replay_does_not_duplicate()
    {
        await using var f=await Fixture.CreateAsync();
        var paid=await f.PayAsync("SUCCESS");Assert.True(paid.Success);
        var id=await f.Db.ScalarAsync<Guid>("SELECT event_id FROM billing.payment_event_outbox");
        for(var i=0;i<5;i++)Assert.True((await f.PayAsync("SUCCESS")).Replayed);
        Assert.Equal(1L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.payment_event_outbox"));
        Assert.Equal(1L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.credit_ledger_entries WHERE order_id=$1",f.Order.Id));
        var result=await f.Events.ResolveAsync(new(id,f.Order.Id,f.Tenant),default);
        Assert.NotNull(result);Assert.Equal(f.Purchaser,result.RecipientAccountId);
        Assert.Null(await f.Events.ResolveAsync(new(Guid.NewGuid(),f.Order.Id,f.Tenant),default));
        Assert.Null(await f.Events.ResolveAsync(new(id,Guid.NewGuid(),f.Tenant),default));
        Assert.Null(await f.Events.ResolveAsync(new(id,f.Order.Id,Guid.NewGuid()),default));
    }

    [M1PostgresFact]
    public async Task Clinic_notification_recipient_is_verified_purchaser_not_clinic_id()
    {
        await using var f=await Fixture.CreateAsync("CLINIC");await f.PayAsync("SUCCESS");
        var id=await f.Db.ScalarAsync<Guid>("SELECT event_id FROM billing.payment_event_outbox");
        var result=await f.Events.ResolveAsync(new(id,f.Order.Id,f.Tenant),default);
        Assert.Equal(f.Purchaser,result!.RecipientAccountId);Assert.NotEqual(f.Order.BillingOwnerId,result.RecipientAccountId);
    }

    [M1PostgresFact]
    public async Task Pending_and_failed_payment_never_create_success_events()
    {
        await using var f=await Fixture.CreateAsync();await f.PayAsync("PENDING");
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.payment_event_outbox"));
        await f.PayAsync("FAILED");
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.payment_event_outbox"));
    }

    [M1PostgresFact]
    public async Task Outbox_write_failure_rolls_back_the_financial_transaction()
    {
        await using var f=await Fixture.CreateAsync();
        await f.Db.SqlAsync("""
            CREATE FUNCTION billing.reject_review_outbox() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-only injected storage failure'; END $$;
            CREATE TRIGGER reject_review_outbox BEFORE INSERT ON billing.payment_event_outbox
            FOR EACH ROW EXECUTE FUNCTION billing.reject_review_outbox();
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>f.PayAsync("SUCCESS"));
        Assert.Equal("PENDING",(await f.Orders.GetOrderByIdAsync(f.Order.Id,default))!.Status);
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.credit_ledger_entries WHERE order_id=$1",f.Order.Id));
        Assert.Equal(0L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.payments WHERE order_id=$1",f.Order.Id));
    }

    [M1PostgresFact]
    public async Task Broker_outage_and_publisher_restart_reuse_the_same_persisted_event()
    {
        await using var f=await Fixture.CreateAsync();await f.PayAsync("SUCCESS");
        var transport=new Transport {Unavailable=true};var publisher=f.Publisher(transport);
        await publisher.DispatchOnceAsync(default);
        Assert.Equal("PENDING",await f.Db.ScalarAsync<string>("SELECT state FROM billing.payment_event_outbox"));
        Assert.True(await f.Db.ScalarAsync<bool>("SELECT available_at>now() FROM billing.payment_event_outbox"));
        await f.PayAsync("SUCCESS");
        await f.Db.SqlAsync("UPDATE billing.payment_event_outbox SET available_at=now()");
        transport.Unavailable=false;await f.Publisher(transport).DispatchOnceAsync(default);
        Assert.Equal("SENT",await f.Db.ScalarAsync<string>("SELECT state FROM billing.payment_event_outbox"));
        Assert.Equal(2,transport.Events.Count);Assert.Equal(transport.Events[0],transport.Events[1]);
        Assert.Equal(1L,await f.Db.ScalarAsync<long>("SELECT count(*) FROM billing.credit_ledger_entries WHERE order_id=$1",f.Order.Id));
    }

    [M1PostgresFact]
    public async Task Expired_and_old_publisher_lease_cannot_mark_a_new_claim_sent()
    {
        await using var f=await Fixture.CreateAsync();await f.PayAsync("SUCCESS");
        var old=(await f.Events.ClaimAsync(default))!;
        await f.Db.SqlAsync("UPDATE billing.payment_event_outbox SET lease_expires_at=now()-interval '1 second'");
        await f.Events.CompleteAsync(old,default);
        Assert.Equal("IN_FLIGHT",await f.Db.ScalarAsync<string>("SELECT state FROM billing.payment_event_outbox"));
        var current=(await f.Events.ClaimAsync(default))!;Assert.NotEqual(old.LeaseToken,current.LeaseToken);
        await f.Events.CompleteAsync(old,default);Assert.Equal("IN_FLIGHT",await f.Db.ScalarAsync<string>("SELECT state FROM billing.payment_event_outbox"));
        await f.Events.CompleteAsync(current,default);Assert.Equal("SENT",await f.Db.ScalarAsync<string>("SELECT state FROM billing.payment_event_outbox"));
    }

    sealed class Fixture : IAsyncDisposable
    {
        public Database Db {get;}public OrderRepository Orders {get;}public PaymentNotificationRepository Events {get;}
        public Guid Purchaser {get;}=Guid.NewGuid();public Guid Tenant {get;}=Guid.NewGuid();public OrderRecord Order {get;private set;}=null!;
        Fixture(Database db){Db=db;Orders=new(db.Billing,NullLogger<OrderRepository>.Instance);Events=new(db.Billing);}
        public static async Task<Fixture> CreateAsync(string type="ACCOUNT")
        {
            var f=new Fixture(await Database.CreateAsync());
            var id=Random.Shared.Next(1000000,2000000000);
            var price=new PackagePriceVersionRecord(Guid.NewGuid(),id,1,"fixture",100000,"VND",10,"ACTIVE");
            await f.Db.SqlAsync("INSERT INTO billing_poc.service_packages(id,name,price,credits) VALUES($1,'fixture',100000,10)",id);
            await f.Db.SqlAsync("INSERT INTO billing.package_price_versions(id,package_id,version_number,display_name,price_amount,currency_code,credits,status,effective_from) VALUES($1,$2,1,'fixture',100000,'VND',10,'ACTIVE',now())",price.Id,id);
            f.Order=await f.Orders.CreateOrderAsync(Guid.NewGuid(),f.Tenant,type,type=="ACCOUNT"?f.Purchaser:Guid.NewGuid(),price,"TEST",Guid.NewGuid(),"m1-regression",default,f.Purchaser);
            return f;
        }
        public Task<PaymentCallbackResult> PayAsync(string status)=>Orders.ProcessCallbackInTransactionAsync(new(Order.Id,"test-transaction",100000,"VND",status,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"repository-fixture-signature"),"SANDBOX_MOCK",default);
        public BillingPaymentEventPublisher Publisher(IPaymentEventTransport transport)=>new(Events,transport,new ConfigurationBuilder().Build(),NullLogger<BillingPaymentEventPublisher>.Instance);
        public ValueTask DisposeAsync()=>Db.DisposeAsync();
    }
    sealed class Transport:IPaymentEventTransport
    {
        public bool Unavailable;public List<Guid> Events {get;}=[];
        public Task PublishAsync(PaymentOutboxEvent item,CancellationToken ct)
        {
            Events.Add(item.EventId);if(Unavailable)throw new IOException("test-only broker outage");return Task.CompletedTask;
        }
    }
}
