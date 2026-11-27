using AURA.Billing.Api;
using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Notification.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AURA.M1.RegressionTests;

public sealed class CatalogVersionTests
{
    [M1PostgresFact]
    public async Task Catalog_versions_are_atomic_and_replay_keeps_the_original_price()
    {
        await using var db = await Database.CreateAsync();
        var repository = new PackageRepository(db.Billing);
        var actor = Guid.NewGuid();
        var key = Guid.NewGuid();
        var initial = new PublishPriceRequest(0, "Fixture package", 500, "VND", 3, "Fixture");
        var first = await repository.PublishAsync(null, initial, actor, key, null, default);
        Assert.NotNull(first.Price);
        var packageId = first.Price.PackageId;
        var orders = new OrderRepository(db.Billing, NullLogger<OrderRepository>.Instance);
        var order = await orders.CreateOrderAsync(Guid.NewGuid(), actor, "ACCOUNT", actor, first.Price, "TEST",
            Guid.NewGuid(), "fixture", default);
        var published = await repository.PublishAsync(packageId, initial with { ExpectedVersion = 1, AmountMinor = 700 },
            actor, Guid.NewGuid(), null, default);
        Assert.Equal(700m, published.Price!.PriceAmount);
        Assert.Equal(first.Price.Id, order.PackagePriceVersionId);
        Assert.Equal(500m, (await orders.GetOrderByIdAsync(order.Id, default))!.PriceAmountSnapshot);
        await Assert.ThrowsAsync<PriceVersionUnavailableException>(() => orders.CreateOrderAsync(Guid.NewGuid(), actor,
            "ACCOUNT", actor, first.Price, "TEST", Guid.NewGuid(), "expired-quote", default));
        var payment = new PaymentCallbackPayload(order.Id, "fixture-payment", 500, "VND", "SUCCESS", 0, "fixture-signature");
        Assert.Equal(3, (await orders.ProcessCallbackInTransactionAsync(payment, "SANDBOX_MOCK", default)).CreditsAdded);
        Assert.True((await orders.ProcessCallbackInTransactionAsync(payment, "SANDBOX_MOCK", default)).Replayed);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.credit_ledger_entries WHERE order_id=$1", order.Id));
        var replay = await repository.PublishAsync(null, initial, actor, key, null, default);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Price.Id, replay.Price!.Id);
        Assert.Equal(500m, replay.Price.PriceAmount);
        Assert.True((await repository.PublishAsync(null, initial with { Credits = 8 }, actor, key, null, default)).Conflict);
        Assert.True((await repository.PublishAsync(packageId, initial with { ExpectedVersion = 1 },
            actor, Guid.NewGuid(), null, default)).Conflict);
        var catalog = await repository.GetAllAsync(default);
        Assert.Equal(published.Price.Id, Assert.Single(catalog, p => p.Id == packageId).PriceVersionId);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events"));
        var next = initial with { ExpectedVersion = 2, AmountMinor = 800 };
        var results = await Task.WhenAll(
            repository.PublishAsync(packageId, next, actor, Guid.NewGuid(), null, default),
            repository.PublishAsync(packageId, next with { AmountMinor = 900 }, actor, Guid.NewGuid(), null, default));
        Assert.Single(results, r => r.Conflict);
        Assert.Single(results, r => r.Price?.VersionNumber == 3);
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM billing.package_price_versions WHERE package_id=$1 AND status='ACTIVE'", packageId));
    }

    [M1PostgresFact]
    public async Task Published_admin_routes_and_owner_history_use_current_authority_and_real_ledger()
    {
        await using var db = await Database.CreateAsync();
        var actor = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        using var identity = new IdentityFixture(actor, tenant);
        var packages = new PackageRepository(db.Billing);
        var templates = new PushTemplates(db.Notification);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHttpClientFactory>(identity);
        builder.Services.AddSingleton(packages);
        builder.Services.AddSingleton(templates);
        builder.Services.AddSingleton(new BillingHistoryRepository(db.Billing));
        builder.Services.AddSingleton(new BillingRuntimeOptions("TEST", false));
        builder.Services.AddSingleton<BillingOwnerAuthorizer>();
        builder.Services.AddSingleton<SystemAdminAuthorizer>();
        await using var app = builder.Build();
        app.MapBillingCatalog();
        app.MapPushTemplates();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-session");
        var key = Guid.NewGuid();
        async Task<HttpResponseMessage> Publish()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/billing/packages")
            {
                Content = JsonContent.Create(new PublishPriceRequest(0, "Fixture", 250, "USD", 5, "Test"))
            };
            request.Headers.Add("Idempotency-Key", key.ToString("D"));
            return await client.SendAsync(request);
        }
        using var published = await Publish();
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var publishedJson = await published.Content.ReadFromJsonAsync<JsonElement>();
        var priceId = publishedJson.GetProperty("priceVersionId").GetGuid();
        await RecordResponse("publication", "billing", "/api/v1/admin/billing/packages", "post", 201, publishedJson);
        using var replay = await Publish();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(priceId, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("priceVersionId").GetGuid());

        var active = await packages.GetAllAsync(default);
        var usd = Assert.Single(active, p => p.PriceVersionId == priceId);
        var vnd = Assert.Single(active, p => p.Id == 1);
        using var anonymous = app.GetTestClient();
        using var catalog = await anonymous.GetAsync("/api/v1/billing/packages");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.True(catalog.Headers.CacheControl!.NoStore);
        var view = (await catalog.Content.ReadFromJsonAsync<PackageCatalogResponse>())!;
        Assert.Equal(usd, Assert.Single(view.Data, p => p.PriceVersionId == priceId));
        var orders = new OrderRepository(db.Billing, NullLogger<OrderRepository>.Instance);
        async Task<OrderRecord> Paid(PackageDto plan, string env)
        {
            var price = await orders.GetActivePriceVersionAsync(plan.Id, plan.PriceVersionId, default);
            var order = await orders.CreateOrderAsync(Guid.NewGuid(), tenant, "ACCOUNT", actor, price!, env,
                Guid.NewGuid(), "test", default);
            var callback = new PaymentCallbackPayload(order.Id, $"tx-{order.Id:N}", plan.Price, plan.Currency!, "SUCCESS", 0, "test");
            Assert.True((await orders.ProcessCallbackInTransactionAsync(callback, "SANDBOX_MOCK", default)).Success);
            return order;
        }
        var paidUsd = await Paid(usd, "TEST");
        var paidVnd = await Paid(vnd, "TEST");
        var live = await Paid(vnd, "LIVE");
        var orderPath = $"/api/v1/billing/orders?ownerType=ACCOUNT&ownerId={actor:D}&limit=1";
        var page = await client.GetFromJsonAsync<JsonElement>(orderPath);
        var next = page.GetProperty("nextCursor").GetString();
        Assert.NotNull(next);
        var oldPage = await client.GetFromJsonAsync<JsonElement>(orderPath + "&cursor=" + next);
        Assert.Equal(paidVnd.Id, page.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(paidUsd.Id, oldPage.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, oldPage.GetProperty("nextCursor").ValueKind);
        var range = $"from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}T00:00:00Z&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}T00:00:00Z";
        var usage = await client.GetFromJsonAsync<JsonElement>($"/api/v1/billing/usage?ownerType=ACCOUNT&ownerId={actor:D}&{range}");
        var entries = usage.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(usd.Credits + vnd.Credits, entries.Sum(i => i.GetProperty("balanceDelta").GetInt32()));
        Assert.DoesNotContain(entries, i => i.GetProperty("orderId").GetGuid() == live.Id);
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/summary?" + range);
        var groups = summary.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(3, groups.Length);
        Assert.Contains(groups, g => g.GetProperty("currency").GetString() == "USD" && g.GetProperty("paidAmount").GetDecimal() == 2.50m);
        Assert.Contains(groups, g => g.GetProperty("environment").GetString() == "LIVE");

        var templateList = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/notification-templates");
        var template = templateList.GetProperty("items")[0];
        await RecordResponse("orders", "billing", "/api/v1/billing/orders", "get", 200, page);
        await RecordResponse("usage", "billing", "/api/v1/billing/usage", "get", 200, usage);
        await RecordResponse("summary", "billing", "/api/v1/admin/billing/summary", "get", 200, summary);
        await RecordResponse("templates", "notification", "/api/v1/admin/notification-templates", "get", 200, templateList);
        var templatePath = "/api/v1/admin/notification-templates/" + template.GetProperty("id").GetGuid().ToString("D");
        var tweak = new PushTemplateChange(1, "Thông báo từ {{app_name}}", "{{event_label}}. Đăng nhập để xem chi tiết.", true, "Test");
        using var unsafeTemplate = await client.PutAsJsonAsync(templatePath, tweak with { Body = "https://outside.example" });
        Assert.Equal(HttpStatusCode.BadRequest, unsafeTemplate.StatusCode);
        using var acceptedTemplate = await client.PutAsJsonAsync(templatePath, tweak);
        Assert.Equal(HttpStatusCode.OK, acceptedTemplate.StatusCode);
        Assert.Equal(2, (await acceptedTemplate.Content.ReadFromJsonAsync<PushTemplate>())!.Version);
        using var staleTemplate = await client.PutAsJsonAsync(templatePath, tweak);
        Assert.Equal(HttpStatusCode.Conflict, staleTemplate.StatusCode);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM notification.audit_events"));

        using var wrongOwner = await client.GetAsync($"/api/v1/billing/orders?ownerType=ACCOUNT&ownerId={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOwner.StatusCode);
        identity.Permitted = false;
        using var revoked = await client.GetAsync(orderPath + "&cursor=" + next);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var nonAdmin = await client.GetAsync("/api/v1/admin/billing/summary");
        Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.StatusCode);
    }

    [Fact]
    public void Push_templates_accept_only_generic_copy_and_predefined_variables()
    {
        Assert.True(PushTemplates.IsAllowed("{{app_name}}", "{{event_label}}. Đăng nhập để xem chi tiết."));
        Assert.False(PushTemplates.IsAllowed("{{patient_name}}", "Đăng nhập để xem chi tiết."));
        Assert.False(PushTemplates.IsAllowed("AURA", "https://outside.example"));
        Assert.False(PushTemplates.IsAllowed("Nguyễn Văn A", "Đăng nhập để xem chi tiết."));
        Assert.False(PushTemplates.IsAllowed("AURA", "Kết quả bệnh nhân: cao."));
    }

    [M1PostgresFact]
    public async Task Admin_catalog_and_retirement_are_authorized_replay_safe_and_preserve_paid_snapshots()
    {
        await using var db = await Database.CreateAsync();
        var actor = Guid.NewGuid(); var tenant = Guid.NewGuid();
        using var authority = new IdentityFixture(actor, tenant);
        await using var app = await CatalogHost(db, authority);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-session");
        using var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/billing/packages")).StatusCode);
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/packages?limit=1");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        await RecordResponse("admin-catalog", "billing", "/api/v1/admin/billing/packages", "get", 200, page);
        if (page.GetProperty("nextCursor").ValueKind==JsonValueKind.String)
        {
            var next = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/packages?limit=1&cursor="+page.GetProperty("nextCursor").GetString());
            Assert.True(Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("packageId").GetInt32()>
                item.GetProperty("packageId").GetInt32());
        }
        var packageId = item.GetProperty("packageId").GetInt32();
        var version = item.GetProperty("version").GetInt32();
        var priceId = item.GetProperty("priceVersionId").GetGuid();
        Assert.True(item.GetProperty("amountMinor").GetInt64() >= 0);
        var packages = new PackageRepository(db.Billing);
        var orders = new OrderRepository(db.Billing, NullLogger<OrderRepository>.Instance);
        var price = await orders.GetActivePriceVersionAsync(packageId, priceId, default);
        var order = await orders.CreateOrderAsync(Guid.NewGuid(), tenant, "ACCOUNT", actor, price!, "TEST", Guid.NewGuid(), "fixture", default);
        var key = Guid.NewGuid();
        async Task<HttpResponseMessage> Retire(int expected, Guid command, string reason="Retire fixture")
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/billing/packages/{packageId}/status")
            { Content = JsonContent.Create(new { expectedVersion=expected, status="RETIRED", reason }) };
            request.Headers.Add("Idempotency-Key", command.ToString("D"));
            return await client.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Retire(version+1, Guid.NewGuid())).StatusCode);
        using var retired = await Retire(version,key);
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        var retiredJson=await retired.Content.ReadFromJsonAsync<JsonElement>();
        await RecordResponse("retirement", "billing", "/api/v1/admin/billing/packages/{packageId}/status", "patch", 200, retiredJson);
        Assert.Equal("RETIRED", retiredJson.GetProperty("status").GetString());
        var replayedRetirement = await packages.RetireAsync(packageId,new(version,"RETIRED","Retire fixture"),actor,key,null,default);
        Assert.Equal("RETIRED",replayedRetirement.Price!.Status);
        Assert.DoesNotContain(await packages.GetAllAsync(default), x=>x.Id==packageId);
        await Assert.ThrowsAsync<PriceVersionUnavailableException>(()=>orders.CreateOrderAsync(Guid.NewGuid(),tenant,"ACCOUNT",actor,price!,"TEST",Guid.NewGuid(),"retired",default));
        var payment = new PaymentCallbackPayload(order.Id,"retired-order",price!.PriceAmount,price.CurrencyCode,"SUCCESS",0,"fixture");
        Assert.True((await orders.ProcessCallbackInTransactionAsync(payment,"SANDBOX_MOCK",default)).Success);
        Assert.True((await orders.ProcessCallbackInTransactionAsync(payment,"SANDBOX_MOCK",default)).Replayed);
        Assert.Equal(priceId,(await orders.GetOrderByIdAsync(order.Id,default))!.PackagePriceVersionId);
        Assert.Equal(HttpStatusCode.OK,(await Retire(version,key)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Retire(version,key,"different")).StatusCode);
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events WHERE payload->>'resourceId'=$1",packageId.ToString()));
        var updated = await packages.PublishAsync(packageId,new(version,"New",700,"VND",5,"new"),actor,Guid.NewGuid(),null,default);
        Assert.NotNull(updated.Price);
        Assert.Equal(HttpStatusCode.Conflict,(await Retire(version,Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Retire(version,key)).StatusCode);
        Assert.Contains(await packages.GetAllAsync(default),x=>x.Id==packageId && x.PriceVersionId==updated.Price.Id);
        authority.Permitted=false;
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/v1/admin/billing/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Retire(updated.Price.VersionNumber,Guid.NewGuid())).StatusCode);
    }

    [M1PostgresFact]
    public async Task Published_price_receipt_has_status_and_owner_summary_is_scoped_to_tenant_and_environment()
    {
        await using var db = await Database.CreateAsync();
        var actor=Guid.NewGuid(); var tenant=Guid.NewGuid();
        using var authority=new IdentityFixture(actor,tenant);
        await using var app=await CatalogHost(db,authority);
        using var client=app.GetTestClient(); client.DefaultRequestHeaders.Authorization=new("Bearer","synthetic-session");
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/admin/billing/packages")
        {Content=JsonContent.Create(new PublishPriceRequest(0,"USD fixture",250,"USD",5,"test"))};
        request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("D"));
        using var response=await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        var quote=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACTIVE",quote.GetProperty("status").GetString());
        var orders=new OrderRepository(db.Billing,NullLogger<OrderRepository>.Instance);
        var price=await orders.GetActivePriceVersionAsync(quote.GetProperty("packageId").GetInt32(),quote.GetProperty("priceVersionId").GetGuid(),default);
        foreach(var (owner,env,t) in new[]{(actor,"TEST",tenant),(actor,"LIVE",tenant),(Guid.NewGuid(),"TEST",tenant),(actor,"TEST",Guid.NewGuid())})
        {
            var order=await orders.CreateOrderAsync(Guid.NewGuid(),t,"CLINIC",owner,price!,env,Guid.NewGuid(),"fixture",default);
            Assert.True((await orders.ProcessCallbackInTransactionAsync(new(order.Id,order.Id.ToString(),2.5m,"USD","SUCCESS",0,"fixture"),"SANDBOX_MOCK",default)).Success);
        }
        var path=$"/api/v1/billing/summary?ownerType=CLINIC&ownerId={actor:D}&from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}T00:00:00Z&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}T00:00:00Z";
        var summary=await client.GetFromJsonAsync<JsonElement>(path);
        await RecordResponse("owner-summary", "billing", "/api/v1/billing/summary", "get", 200, summary);
        var group=Assert.Single(summary.GetProperty("groups").EnumerateArray());
        Assert.Equal("TEST",group.GetProperty("environment").GetString());
        Assert.Equal(1,group.GetProperty("paidOrders").GetInt64());
        Assert.Equal(2.5m,group.GetProperty("paidAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path.Replace(actor.ToString("D"),Guid.NewGuid().ToString("D")))).StatusCode);
        authority.Permitted=false;
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode);
    }

    [M1PostgresFact]
    public async Task Retirement_rolls_back_when_audit_fails_and_races_share_the_publication_lock()
    {
        await using var db=await Database.CreateAsync();
        var repo=new PackageRepository(db.Billing);var actor=Guid.NewGuid();
        var first=await repo.PublishAsync(null,new(0,"Fixture",100,"VND",2,"new"),actor,Guid.NewGuid(),null,default);
        var package=first.Price!;var retirement=new RetirePackageRequest(1,"RETIRED","Test");
        var key=Guid.NewGuid();
        await db.SqlAsync("REVOKE INSERT ON billing.audit_events FROM aura_billing_app");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>repo.RetireAsync(package.PackageId,retirement,actor,key,null,default));
        Assert.Contains(await repo.GetAllAsync(default),x=>x.Id==package.PackageId);
        Assert.Equal(0L,await db.ScalarAsync<long>("SELECT count(*) FROM billing.catalog_publications WHERE idempotency_key=$1",key));
        await db.SqlAsync("GRANT INSERT ON billing.audit_events TO aura_billing_app");
        var results=await Task.WhenAll(
            repo.RetireAsync(package.PackageId,retirement,actor,key,null,default),
            repo.PublishAsync(package.PackageId,new(1,"New",200,"VND",3,"new"),actor,Guid.NewGuid(),null,default));
        Assert.False(results[1].Conflict);
        var latest=Assert.Single(await repo.ListAdminAsync(package.PackageId-1,1,default));
        Assert.Equal(2,latest.Version);Assert.Equal("ACTIVE",latest.Status);
        Assert.True(results[0].Conflict || results[0].Price?.Id==package.Id);
        Assert.True((await repo.RetireAsync(package.PackageId,new(2,"RETIRED","cross operation"),actor,
            await db.ScalarAsync<Guid>("SELECT idempotency_key FROM billing.catalog_publications WHERE price_version_id=$1",results[1].Price!.Id),null,default)).Conflict);
    }

    private static async Task<WebApplication> CatalogHost(Database db,IdentityFixture authority)
    {
        var builder=WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IHttpClientFactory>(authority);
        builder.Services.AddSingleton(new PackageRepository(db.Billing));
        builder.Services.AddSingleton(new BillingHistoryRepository(db.Billing));
        builder.Services.AddSingleton(new BillingRuntimeOptions("TEST",false));
        builder.Services.AddSingleton<BillingOwnerAuthorizer>();builder.Services.AddSingleton<SystemAdminAuthorizer>();
        var app=builder.Build();app.MapBillingCatalog();await app.StartAsync();return app;
    }

    private static async Task RecordResponse(string name,string contract,string path,string method,int status,JsonElement value)
    {
        var directory=Environment.GetEnvironmentVariable("AURA_M1_ARTIFACTS");
        if(string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,name+".json"),JsonSerializer.Serialize(new { name,contract,path,method,status,value }));
    }

    private sealed class IdentityFixture(Guid actor, Guid tenant) : HttpMessageHandler, IHttpClientFactory
    {
        public bool Permitted { get; set; } = true;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("http://authority.test") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object body;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v1/identity/me":
                    Assert.Equal("synthetic-session", request.Headers.Authorization!.Parameter);
                    body = new { accountId = actor, accountStatus = "ACTIVE", roles = Permitted ? new[] { "SYSTEM_ADMIN" } : [] };
                    break;
                case "/internal/v1/identity/service-tokens":
                    body = new { accessToken = "fixture-service" };
                    break;
                default:
                    Assert.Equal("/internal/v1/profile/billing-owner/resolve", request.RequestUri.AbsolutePath);
                    var query = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    Assert.Equal("BILLING_READ", query.GetProperty("action").GetString());
                    body = new { allowed = Permitted && query.GetProperty("ownerId").GetGuid() == actor, tenantId = tenant };
                    break;
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
