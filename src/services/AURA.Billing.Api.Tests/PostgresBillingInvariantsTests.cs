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
    public async Task Wallet_read_is_scoped_to_owner_and_environment_and_does_not_create_a_wallet()
    {
        await using var f=await Fixture.CreateAsync();
        Assert.Equal(100,await f.Orders.GetWalletBalanceAsync("ACCOUNT",f.Owner,"TEST",default));
        Assert.Equal(0,await f.Orders.GetWalletBalanceAsync("ACCOUNT",Guid.NewGuid(),"TEST",default));
        Assert.Equal(0,await f.Orders.GetWalletBalanceAsync("ACCOUNT",f.Owner,"LIVE",default));
        Assert.Equal(0,await f.Orders.GetWalletBalanceAsync("CLINIC",f.Owner,"TEST",default));
        Assert.Equal(100,await f.BalanceAsync());
    }
    [BillingPostgresFact]
    public async Task Inbox_accepts_legacy_and_ADR005_events_without_accepting_unknown_versions()
    {
        await using var f=await Fixture.CreateAsync();
        foreach(var version in new[]{"0.7","0.8"})
        {
            var item=new SettlementInboxEvent("adr005-version",Guid.NewGuid(),"analysis.completed",version,DateTimeOffset.UtcNow,Guid.NewGuid(),Guid.NewGuid(),"version-regression",new string('a',64));
            Assert.Equal(SettlementInboxIngestStatus.Inserted,(await f.Inbox.IngestAsync(item,default)).Status);
            Assert.Equal(SettlementInboxIngestStatus.Duplicate,(await f.Inbox.IngestAsync(item,default)).Status);
            var changedPayload=await f.Inbox.IngestAsync(item with {PayloadSha256=new string('b',64)},default);
            Assert.Equal(SettlementInboxIngestStatus.PayloadConflict,changedPayload.Status);
            Assert.Equal("HOLD",changedPayload.CurrentStatus);
            await Assert.ThrowsAsync<ArgumentException>(()=>f.Inbox.IngestAsync(item with{EventId=Guid.NewGuid(),SchemaVersion="0.9"},default));
        }
    }
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
    public async Task Reservation_replay_is_idempotent_and_same_key_changed_payload_conflicts_without_double_charge()
    {
        await using var f=await Fixture.CreateAsync();
        var options=new BillingRuntimeOptions("TEST",false);
        var request=new ReserveCreditRequest(Guid.NewGuid(),f.Tenant,f.Owner,Guid.NewGuid(),new("ACCOUNT",f.Owner),1,1);
        var key=Guid.NewGuid();
        var fingerprint=ReservationFingerprint.ForReserve(request,options);

        var created=await f.Credits.ReserveAsync(request,key,fingerprint,options,default);
        Assert.Equal(ReservationOperationStatus.Created,created.Status);
        var reservation=Assert.IsType<ReservationResponse>(created.Reservation);
        Assert.Equal(99,await f.BalanceAsync());

        var replay=await f.Credits.ReserveAsync(request,key,fingerprint,options,default);
        Assert.Equal(ReservationOperationStatus.Replayed,replay.Status);
        Assert.Equal(reservation.ReservationId,replay.Reservation!.ReservationId);
        Assert.Equal(99,await f.BalanceAsync());

        var changedRequest=request with {Units=2};
        var conflict=await f.Credits.ReserveAsync(changedRequest,key,
            ReservationFingerprint.ForReserve(changedRequest,options),options,default);
        Assert.Equal(ReservationOperationStatus.IdempotencyConflict,conflict.Status);
        Assert.Equal(99,await f.BalanceAsync());

        var captureKey=Guid.NewGuid();
        var captureFingerprint=ReservationFingerprint.ForCapture(reservation.ReservationId);
        var captured=await f.Credits.CaptureAsync(reservation.ReservationId,captureKey,captureFingerprint,default);
        Assert.Equal(ReservationOperationStatus.Changed,captured.Status);
        var captureReplay=await f.Credits.CaptureAsync(reservation.ReservationId,captureKey,captureFingerprint,default);
        Assert.Equal(ReservationOperationStatus.Replayed,captureReplay.Status);
        Assert.Equal("CAPTURED",(await f.Credits.GetAsync(reservation.ReservationId,default))!.State);
        Assert.Equal(99,await f.BalanceAsync());

        Assert.Equal(2L,await f.ReservationLedgerCountAsync(reservation.ReservationId));
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

    [BillingPostgresFact]
    public async Task Clinic_revenue_summary_requires_successful_payment_and_exact_tenant_owner_and_time_window()
    {
        await using var f=await Fixture.CreateAsync();
        var clinicOrder=await f.NewOrderAsync("CLINIC");
        Assert.True((await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(clinicOrder,"SUCCESS"),"SANDBOX_MOCK",default)).Success);

        var accountOrder=await f.NewOrderAsync("ACCOUNT");
        Assert.True((await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(accountOrder,"SUCCESS"),"SANDBOX_MOCK",default)).Success);

        var otherClinicOrder=await f.NewOrderAsync("CLINIC",Guid.NewGuid());
        Assert.True((await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(otherClinicOrder,"SUCCESS"),"SANDBOX_MOCK",default)).Success);

        var failedClinicOrder=await f.NewOrderAsync("CLINIC");
        Assert.Equal("CANCELLED",(await f.Orders.ProcessCallbackInTransactionAsync(f.Payload(failedClinicOrder,"FAILED"),"SANDBOX_MOCK",default)).Status);

        var from=DateTime.UtcNow.AddMinutes(-5);
        var to=DateTime.UtcNow.AddMinutes(5);
        var totals=await new BillingHistoryRepository(f.Runtime).OwnerSummaryAsync("CLINIC",f.Owner,f.Tenant,"TEST",from,to,default);
        var total=Assert.Single(totals);
        Assert.Equal("TEST",total.Environment);
        Assert.Equal("VND",total.Currency);
        Assert.Equal(1,total.PaidOrders);
        Assert.Equal(100000m,total.PaidAmount);
        Assert.Empty(await new BillingHistoryRepository(f.Runtime).OwnerSummaryAsync("CLINIC",f.Owner,Guid.NewGuid(),"TEST",from,to,default));
        Assert.Empty(await new BillingHistoryRepository(f.Runtime).OwnerSummaryAsync("CLINIC",Guid.NewGuid(),f.Tenant,"TEST",from,to,default));
        Assert.Empty(await new BillingHistoryRepository(f.Runtime).OwnerSummaryAsync("CLINIC",f.Owner,f.Tenant,"TEST",to.AddMinutes(1),to.AddMinutes(2),default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SettlementInboxRepository Inbox=>new(_runtime);
        public Guid Tenant {get;}=Guid.NewGuid();public Guid Owner {get;}=Guid.NewGuid();public Guid Wallet {get;}=Guid.NewGuid();
        private readonly NpgsqlDataSource _admin;private readonly NpgsqlDataSource _runtime;private readonly PackagePriceVersionRecord _price;
        public NpgsqlDataSource Runtime=>_runtime;
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
        public async Task<long> ReservationLedgerCountAsync(Guid reservationId)
        {await using var cmd=_admin.CreateCommand("SELECT count(*) FROM billing.credit_ledger_entries WHERE reservation_id=$1 AND entry_type IN ('RESERVE','CAPTURE')");cmd.Parameters.AddWithValue(reservationId);return (long)(await cmd.ExecuteScalarAsync())!;}
        public Task<OrderRecord> NewOrderAsync(string ownerType="ACCOUNT",Guid? owner=null)=>Orders.CreateOrderAsync(Guid.NewGuid(),Tenant,ownerType,owner??Owner,_price,"TEST",Guid.NewGuid(),Guid.NewGuid().ToString(),default);
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
