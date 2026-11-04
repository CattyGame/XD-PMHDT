using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AURA.Billing.Api.Tests;

public sealed class BillingPostgresFactAttribute : FactAttribute
{
    public BillingPostgresFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES")))
            Skip="Set AURA_TEST_POSTGRES to an isolated migrated test database.";
    }
}

public sealed class PostgresBillingInvariantsTests
{
    [BillingPostgresFact]
    public async Task Pending_then_success_replay_and_failed_preserve_one_topup()
    {
        await using var f=await Fixture.CreateAsync();var order=await f.NewOrderAsync();
        var pending=await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(order,"PENDING"),"SANDBOX_MOCK",default);
        Assert.Equal("PENDING",pending.Status);Assert.Equal(100,await f.BalanceAsync());
        var success=await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(order,"SUCCESS"),"SANDBOX_MOCK",default);
        Assert.True(success.Success);Assert.Equal(110,await f.BalanceAsync());
        var replay=await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(order,"SUCCESS"),"SANDBOX_MOCK",default);
        Assert.True(replay.Replayed);Assert.Equal(110,await f.BalanceAsync());
        var lateFailed=await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(order,"FAILED"),"SANDBOX_MOCK",default);
        Assert.Equal("COMPLETED",lateFailed.Status);Assert.Equal(110,await f.BalanceAsync());
    }

    [BillingPostgresFact]
    public async Task Failed_and_unknown_callbacks_do_not_credit_or_corrupt_order_enum()
    {
        await using var f=await Fixture.CreateAsync();var failedOrder=await f.NewOrderAsync();
        var failure=await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(failedOrder,"FAILED"),"SANDBOX_MOCK",default);
        Assert.Equal("CANCELLED",failure.Status);Assert.Equal(100,await f.BalanceAsync());
        var unknownOrder=await f.NewOrderAsync();
        var unknown=await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(unknownOrder,"PROVIDER_NEW_STATUS"),"SANDBOX_MOCK",default);
        Assert.Equal("ERR_INVALID_PAYMENT_STATUS",unknown.Status);
        Assert.Equal("PENDING",(await f.Orders.GetOrderByIdAsync(unknownOrder.Id,default))!.Status);
        Assert.Equal(100,await f.BalanceAsync());
    }

    [BillingPostgresFact]
    public async Task Captured_refund_requires_authority_and_distinct_concurrent_keys_have_one_effect()
    {
        await using var f=await Fixture.CreateAsync();var reservation=await f.ReserveAsync();
        await f.Credits.CaptureAsync(reservation.ReservationId,Guid.NewGuid(),ReservationFingerprint.ForCapture(reservation.ReservationId),default);
        var request=new BatchRefundRequest(f.Tenant,null,new[]{new BatchRefundItemRequest(reservation.ReservationId,"BATCH_PARTIAL_REFUND")});
        var forbidden=await f.Credits.ExecuteBatchRefundAsync(request,Guid.NewGuid(),false);
        Assert.Equal("FORBIDDEN",forbidden.Results.Single().Status);Assert.Equal(99,await f.BalanceAsync());
        Assert.Equal("FORBIDDEN",(await f.Credits.ExecuteBatchRefundAsync(request,Guid.NewGuid(),true)).Results.Single().Status);
        var tasks=Enumerable.Range(0,8).Select(_=>f.Credits.ExecuteBatchRefundAsync(request,Guid.NewGuid(),true,default,new RefundAuthorization(Guid.NewGuid(),"SYSTEM_ADMIN")));
        var results=await Task.WhenAll(tasks);
        Assert.Equal(1,results.Sum(x=>x.CreditsRefunded));Assert.Equal(100,await f.BalanceAsync());
        var changedReason=request with {Items=new[]{new BatchRefundItemRequest(reservation.ReservationId,"SECOND_REFUND_REASON")}};
        Assert.Equal(0,(await f.Credits.ExecuteBatchRefundAsync(changedReason,Guid.NewGuid(),true,default,new RefundAuthorization(Guid.NewGuid(),"SYSTEM_ADMIN"))).CreditsRefunded);
        Assert.Equal("CAPTURED",(await f.Credits.GetAsync(reservation.ReservationId,default))!.State);
    }

    [BillingPostgresFact]
    public async Task Unrelated_adjustment_does_not_block_refund_and_database_rejects_second_refund()
    {
        await using var f=await Fixture.CreateAsync();var reservation=await f.ReserveAsync();
        await f.Credits.CaptureAsync(reservation.ReservationId,Guid.NewGuid(),ReservationFingerprint.ForCapture(reservation.ReservationId),default);
        await f.SqlAsync("""
            INSERT INTO billing.credit_ledger_entries(wallet_id,tenant_id,reservation_id,entry_type,amount,balance_after,idempotency_key,request_fingerprint,metadata)
            VALUES($1,$2,$3,'ADJUSTMENT',-1,98,$4,'unrelated','{"operation":"CORRECTION"}')
            """,f.Wallet,f.Tenant,reservation.ReservationId,Guid.NewGuid());
        await f.SqlAsync("UPDATE billing.credit_wallets SET balance=balance-1 WHERE id=$1",f.Wallet);
        var request=new BatchRefundRequest(f.Tenant,null,new[]{new BatchRefundItemRequest(reservation.ReservationId)});
        Assert.Equal(1,(await f.Credits.ExecuteBatchRefundAsync(request,Guid.NewGuid(),true,default,new RefundAuthorization(Guid.NewGuid(),"SYSTEM_ADMIN"))).CreditsRefunded);
        var ex=await Assert.ThrowsAsync<PostgresException>(()=>f.SqlAsync("""
            INSERT INTO billing.credit_ledger_entries(wallet_id,tenant_id,reservation_id,entry_type,amount,balance_after,idempotency_key,request_fingerprint,metadata)
            VALUES($1,$2,$3,'ADJUSTMENT',1,100,$4,'duplicate','{"operation":"REFUND","refund_authority":"SYSTEM_ADMIN","approved_by":"11111111-1111-4111-8111-111111111111"}')
            """,f.Wallet,f.Tenant,reservation.ReservationId,Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.UniqueViolation,ex.SqlState);Assert.Equal(99,await f.BalanceAsync());
    }

    [BillingPostgresFact]
    public async Task Stale_claim_backoff_does_not_starve_next_records_and_preserves_generation()
    {
        await using var f=await Fixture.CreateAsync();var first=await f.ReserveAsync();var second=await f.ReserveAsync();
        await f.SqlAsync("UPDATE billing.credit_reservations SET updated_at=now()-interval '2 hours' WHERE id=$1 OR id=$2",first.ReservationId,second.ReservationId);
        var claim=await f.Credits.GetStaleReservationsAsync(30,1,default);Assert.Single(claim);
        Assert.Equal(1,claim[0].Generation);
        await f.Credits.DeferReconciliationAsync(claim[0].ReservationId,"NOT_TERMINAL",default);
        var next=await f.Credits.GetStaleReservationsAsync(30,1,default);Assert.Single(next);Assert.NotEqual(claim[0].ReservationId,next[0].ReservationId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Tenant {get;}=Guid.NewGuid();public Guid Owner {get;}=Guid.NewGuid();public Guid Wallet {get;}=Guid.NewGuid();
        private readonly NpgsqlDataSource _admin;private readonly NpgsqlDataSource _runtime;private readonly PackagePriceVersionRecord _price;
        public OrderRepository Orders {get;}public CreditReservationRepository Credits {get;}
        private Fixture(NpgsqlDataSource admin,NpgsqlDataSource runtime,PackagePriceVersionRecord price)
        {_admin=admin;_runtime=runtime;_price=price;Orders=new(runtime,NullLogger<OrderRepository>.Instance);Credits=new(runtime,NullLogger<CreditReservationRepository>.Instance);}
        public static async Task<Fixture> CreateAsync()
        {
            var connection=Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES")!;var config=new NpgsqlConnectionStringBuilder(connection);
            if(config.Database?.Contains("test",StringComparison.OrdinalIgnoreCase)!=true && !(config.Database=="aura_dev" && config.Host=="127.0.0.1" && config.Port==55432 && Environment.GetEnvironmentVariable("AURA_TEST_ALLOW_DATABASE")=="aura_dev"))throw new InvalidOperationException("Requires isolated test database.");
            var admin=NpgsqlDataSource.Create(connection);config.Options="-c role=aura_billing_app";var runtime=NpgsqlDataSource.Create(config.ConnectionString);
            var packageId=Random.Shared.Next(1_000_000,2_000_000_000);var price=new PackagePriceVersionRecord(Guid.NewGuid(),packageId,1,"billing fixture",100000,"VND",10,"ACTIVE");
            var f=new Fixture(admin,runtime,price);
            await f.SqlAsync("INSERT INTO billing_poc.service_packages(id,name,price,credits) VALUES($1,'billing fixture',100000,10)",packageId);
            await f.SqlAsync("INSERT INTO billing.package_price_versions(id,package_id,version_number,display_name,price_amount,currency_code,credits,status,effective_from) VALUES($1,$2,1,'billing fixture',100000,'VND',10,'ACTIVE',now())",price.Id,packageId);
            await f.SqlAsync("INSERT INTO billing.credit_wallets(id,owner_type,owner_id,environment,balance) VALUES($1,'ACCOUNT',$2,'TEST',100)",f.Wallet,f.Owner);
            return f;
        }
        public async Task SqlAsync(string sql,params object[] values)
        {await using var cmd=_admin.CreateCommand(sql);foreach(var value in values)cmd.Parameters.AddWithValue(value);await cmd.ExecuteNonQueryAsync();}
        public async Task<int> BalanceAsync()
        {await using var cmd=_admin.CreateCommand("SELECT balance FROM billing.credit_wallets WHERE id=$1");cmd.Parameters.AddWithValue(Wallet);return (int)(await cmd.ExecuteScalarAsync())!;}
        public Task<OrderRecord> NewOrderAsync()=>Orders.CreateOrderAsync(Guid.NewGuid(),Tenant,"ACCOUNT",Owner,_price,"TEST",Guid.NewGuid(),Guid.NewGuid().ToString(),default);
        public PaymentCallbackPayload Payload(OrderRecord order,string status)=>new(order.Id,Guid.NewGuid().ToString(),order.PriceAmountSnapshot,order.CurrencyCodeSnapshot,status,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"repository-boundary-fixture-signature");
        public async Task<ReservationResponse> ReserveAsync()
        {
            var request=new ReserveCreditRequest(Guid.NewGuid(),Tenant,Owner,Guid.NewGuid(),new("ACCOUNT",Owner),1,1);
            var options=new BillingRuntimeOptions("TEST",false);
            return (await Credits.ReserveAsync(request,Guid.NewGuid(),ReservationFingerprint.ForReserve(request,options),options,default)).Reservation!;
        }
        public async ValueTask DisposeAsync(){await _runtime.DisposeAsync();await _admin.DisposeAsync();}
    }
}
