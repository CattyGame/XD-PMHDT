using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Upload;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Npgsql;
using AURA.Common.Audit;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalysisHttpTests
{
    [Fact]
    public async Task Camera_receipt_requires_live_delegation_and_preserves_event_metadata()
    {
        await using var r = await Rig.Start();
        var key = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        using var bare = r.Post(path: "/api/v1/analyses/camera");
        r.Client.DefaultRequestHeaders.Authorization = null;
        using var unauthorized = await r.Client.SendAsync(bare);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        async Task<HttpResponseMessage> Send(string device)
        {
            using var request = r.Post(path: "/api/v1/analyses/camera");
            request.Headers.Authorization = new("Bearer", "camera-fixture");
            request.Headers.Remove("Idempotency-Key");
            request.Headers.Add("Idempotency-Key", key.ToString("D"));
            request.Headers.Add("X-Tenant-Id", tenant.ToString("D"));
            request.Headers.Add("X-Device-Id", device);
            request.Headers.Add("X-Camera-Version", "1");
            request.Headers.Add("X-Camera-SHA256", new string('a', 64));
            return await r.Client.SendAsync(request);
        }
        using var allowed = await Send("device-1");
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        var receipt = await allowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(key, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(1, receipt.GetProperty("version").GetInt32());
        Assert.Equal(new string('a', 64), receipt.GetProperty("imageSha256").GetString());
        Assert.Equal(r.Camera.Actor, r.Upload.Last!.RequesterId);
        Assert.Equal(key.ToString("D"), r.Upload.Last.CorrelationId);
        Assert.Equal(tenant, r.Upload.Last.Camera!.TenantId);
        var acceptedCommand = r.Upload.Last;
        using var changedDevice = await Send("device-2");
        Assert.Equal(HttpStatusCode.Forbidden, changedDevice.StatusCode);
        r.Camera.Revoked = true;
        using var revokedReplay = await Send("device-1");
        Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
        Assert.Same(acceptedCommand, r.Upload.Last);
        Assert.Equal(3, r.Camera.Calls);
        Assert.Equal(10, r.Upload.Length);
    }

    private sealed class CameraAuthority : ICameraDelegation
    {
        public readonly Guid Actor = Guid.NewGuid();
        private readonly Guid grant = Guid.NewGuid();
        public int Calls;
        public bool Revoked;
        public Task<CameraGrant> ResolveAsync(System.Net.Http.Headers.AuthenticationHeaderValue bearer,
            Guid patient, Guid tenant, Guid? clinic, string device, CancellationToken ct)
        {
            Calls++;
            if (Revoked || device != "device-1") throw new UploadFailure(403, "ERR_FORBIDDEN", "Denied");
            return Task.FromResult(new CameraGrant(true, Actor, patient, tenant, "PERSONAL", Actor,
                new("ACCOUNT", Actor), grant, clinic));
        }
    }
    [Fact]
    public async Task Patient_history_requires_bearer_before_reading_any_history()
    {
        await using var r=await Rig.Start();
        r.Client.DefaultRequestHeaders.Authorization=null;
        using var response=await r.Client.GetAsync($"/api/v1/analyses/patients/{Guid.NewGuid()}/history?clinicContextId={Guid.NewGuid()}");
        await Code(response,401,"ERR_UNAUTHORIZED");
    }
    [Fact]
    public async Task Patient_history_requires_explicit_clinic_selector()
    {
        await using var r=await Rig.Start();
        using var response=await r.Client.GetAsync($"/api/v1/analyses/patients/{Guid.NewGuid()}/history");
        await Code(response,400,"ERR_VALIDATION");
    }
    [Fact]
    public async Task Patient_history_returns_no_store_page_and_rechecks_revocation_on_next_request()
    {
        await using var r=await Rig.Start();var patient=Guid.NewGuid();
        var url=$"/api/v1/analyses/patients/{patient}/history?clinicContextId={r.Identity.Account}";
        using(var response=await r.Client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            Assert.Equal("no-store",response.Headers.CacheControl!.ToString());
            var value=await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(patient,value.GetProperty("patientId").GetGuid());
            Assert.Equal(JsonValueKind.Null,value.GetProperty("nextBeforeId").ValueKind);
        }
        r.Access.Allowed=false;
        using var denied=await r.Client.GetAsync(url);await Code(denied,404,"ERR_NOT_FOUND");
        Assert.Equal(1,r.History.Reads);
    }
    private sealed class Identity : IAnalysisUserIdentityResolver
    {
        public readonly Guid Account=Guid.NewGuid();public string[] Roles=["PATIENT"];public long? LastLength;
        public Task<AnalysisUserContext> ResolveAsync(HttpRequest request,CancellationToken ct)
        {
            LastLength=request.ContentLength;
            if(request.Headers.Authorization!="Bearer fixture-user")throw new UploadFailure(401,"ERR_UNAUTHORIZED","Unauthorized");
            return Task.FromResult(new AnalysisUserContext(Account,Roles));
        }
    }
    private sealed class Upload : IAnalysisUploadHandler
    {
        public UploadCommand? Last;public bool Replay,Fail;public long Length;public Func<Task>? Before;public Guid? DuplicateOf;
        public async Task<UploadResult> CreateAsync(UploadCommand c,CancellationToken ct)
        {
            Last=c;if(Fail)throw new IOException("PRIVATE_DEPENDENCY_DIAGNOSTIC");if(Before is not null)await Before();
            foreach(var f in c.Files){await using var s=f.OpenReadStream();using var bytes=new MemoryStream();await s.CopyToAsync(bytes,ct);Length+=bytes.Length;}
            var batch=Guid.NewGuid();return new(new(batch,[new(Guid.NewGuid(),"synthetic.png",DuplicateOf is null?"QUEUED":"DUPLICATE",DuplicateOf is null?null:new("ERR_DUPLICATE_REQUEST","duplicate",false),DuplicateOf)],DateTime.UtcNow,$"/api/v1/batches/{batch}"),Replay);
        }
    }
    private sealed class Batch : IBatchStatusRepository
    {
        public BatchIdentity? Identity;public int Reads;public Guid? DuplicateOf;
        public Task<BatchIdentity?> FindIdentityAsync(Guid id,CancellationToken ct)=>Task.FromResult(Identity);
        public Task<BatchStatusSnapshot?> ReadAsync(BatchIdentity binding,CancellationToken ct){Reads++;return Task.FromResult<BatchStatusSnapshot?>(new(binding.BatchId,DateTime.UtcNow,[new(Guid.NewGuid(),"synthetic.png",DuplicateOf is null?"QUEUED":"DUPLICATE","NOT_REQUIRED",null,false,null,null,DuplicateOf)]));}
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Identity Identity=new();public readonly Upload Upload=new();public readonly FakeAccess Access=new();
        public readonly FakeFileRepo Files=new();public readonly FakeStorage Storage=new();public readonly Batch Batch=new();
        public readonly HistoryRepository History=new();
        public readonly AuditWriter Audit=new();
        public readonly CameraAuthority Camera=new();
        public WebApplication App=null!;public HttpClient Client=null!;
        public static async Task<Rig> Start(long imageLimit=10485760,int images=100,long total=1101004800,NpgsqlDataSource? itemDatabase=null)
        {
            var r=new Rig();var b=WebApplication.CreateBuilder();b.WebHost.UseTestServer();b.Logging.ClearProviders();
            b.Services.AddSingleton<IAnalysisUserIdentityResolver>(r.Identity);b.Services.AddSingleton<IAnalysisUploadHandler>(r.Upload);
            b.Services.AddSingleton(new UploadOptions{MaxImageBytes=imageLimit,MaxImagesPerRequest=images,MaxRequestBytes=total});
            b.Services.AddSingleton(new AnalysisUploadGate(1));b.Services.AddSingleton(new StorageOptions());
            b.Services.AddSingleton<IPatientAccessResolver>(r.Access);b.Services.AddSingleton<IFileAccessRepository>(r.Files);b.Services.AddSingleton<IPrivateFileStorage>(r.Storage);
            b.Services.AddSingleton<IBatchStatusRepository>(r.Batch);b.Services.AddTransient<BatchStatusService>();b.Services.AddTransient<FileAccessService>();
            b.Services.AddSingleton<IPatientHistoryRepository>(r.History);b.Services.AddTransient<PatientHistoryService>();
            b.Services.AddSingleton<IAuditWriter>(r.Audit);
            b.Services.AddSingleton<ICameraDelegation>(r.Camera);
            if(itemDatabase is not null)
            {b.Services.AddSingleton(itemDatabase);b.Services.AddSingleton(TimeProvider.System);b.Services.AddTransient<ItemUploadService>();}
            r.App=b.Build();r.App.MapAnalysisEndpoints();await r.App.StartAsync();r.Client=r.App.GetTestClient();r.Client.DefaultRequestHeaders.Authorization=new("Bearer","fixture-user");
            return r;
        }
        public async ValueTask DisposeAsync(){Client.Dispose();await App.DisposeAsync();}
        public HttpRequestMessage Post(int bytes=10,string path="/api/v1/analyses")
        {
            var form=new MultipartFormDataContent();form.Add(new StringContent(Guid.NewGuid().ToString()),"patientId");
            var image=new ByteArrayContent(new byte[bytes]);image.Headers.ContentType=new("image/png");form.Add(image,"images","synthetic.png");
            var request=new HttpRequestMessage(HttpMethod.Post,path){Content=form};request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());return request;
        }
    }
    private sealed class HistoryRepository : IPatientHistoryRepository
    {
        public int Reads;
        public Task<IReadOnlyList<PatientHistoryItem>> ListAsync(Guid tenant,Guid clinic,Guid patient,int limit,Guid? before,CancellationToken ct)
        {Reads++;return Task.FromResult<IReadOnlyList<PatientHistoryItem>>([]);}
    }
    private static async Task Code(HttpResponseMessage response,int status,string code)
    {
        Assert.Equal(status,(int)response.StatusCode);var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(code,json.GetProperty("error").GetProperty("code").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));Assert.Equal("no-store",response.Headers.CacheControl!.ToString());
    }
    [Fact] public async Task Multipart_binds_authenticated_actor_and_returns_202()
    {await using var r=await Rig.Start();using var request=r.Post();request.Headers.Add("X-Account-Id",Guid.NewGuid().ToString());request.Headers.Add("X-User-Id",Guid.NewGuid().ToString());using var result=await r.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,result.StatusCode);Assert.Equal(r.Identity.Account,r.Upload.Last!.RequesterId);Assert.Equal(10,r.Upload.Length);Assert.NotNull(result.Headers.Location);}
    [PostgresFact]
    public async Task Item_multipart_accepts_one_image_and_rejects_extra_sections()
    {
        var cs=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!){Options="-c role=aura_analysis_app"};
        await using var database=NpgsqlDataSource.Create(cs.ConnectionString);
        await using var r=await Rig.Start(itemDatabase:database);
        var bytes=TestImages.Png(64,64);var item=Guid.NewGuid();
        using var create=new HttpRequestMessage(HttpMethod.Post,"/api/v1/analysis-upload-batches")
        {Content=JsonContent.Create(new UploadManifest([new(item,Guid.NewGuid(),"synthetic.png",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),"FUNDUS","unknown")]))};
        create.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());
        using var admitted=await r.Client.SendAsync(create);Assert.Equal(HttpStatusCode.Created,admitted.StatusCode);
        var inventory=(await admitted.Content.ReadFromJsonAsync<UploadInventory>(UploadJson.Options))!;
        foreach(var extra in new[]{"none","file","field"})
        {
            using var form=new MultipartFormDataContent();
            var image=new ByteArrayContent(bytes);image.Headers.ContentType=new("image/png");form.Add(image,"image","synthetic.png");
            if(extra=="file")form.Add(new ByteArrayContent(bytes),"image","second.png");
            if(extra=="field")form.Add(new StringContent("ignored"),"extra");
            using var put=new HttpRequestMessage(HttpMethod.Put,$"/api/v1/analysis-upload-batches/{inventory.BatchId:D}/items/{item:D}"){Content=form};
            put.Headers.Add("Idempotency-Key",item.ToString());
            using var response=await r.Client.SendAsync(put);
            if(extra=="none")
            {Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Equal("STAGED",(await response.Content.ReadFromJsonAsync<UploadInventoryItem>(UploadJson.Options))!.UploadStatus);}
            else await Code(response,400,"ERR_VALIDATION");
        }
        Assert.Equal(1,r.Storage.PutCalls);
    }
    [Fact] public async Task Legacy_batch_endpoint_rejects_multiple_images_before_admission()
    {
        await using var r=await Rig.Start(images:100);
        using var request=r.Post(path:"/api/v1/analyses/batches");
        var form=(MultipartFormDataContent)request.Content!;
        for(var i=1;i<100;i++)
        {
            var image=new ByteArrayContent(new byte[10]);image.Headers.ContentType=new("image/png");
            form.Add(image,"images",$"synthetic-{i:D3}.png");
        }
        using var response=await r.Client.SendAsync(request);
        await Code(response,400,"ERR_VALIDATION");
        Assert.Null(r.Upload.Last);
    }
    [Fact] public async Task Replay_sets_header()
    {await using var r=await Rig.Start();r.Upload.Replay=true;using var request=r.Post();using var result=await r.Client.SendAsync(request);Assert.Equal("true",Assert.Single(result.Headers.GetValues("Idempotent-Replayed")));}
    [Fact] public async Task Duplicate_upload_exposes_the_canonical_analysis_reference()
    {
        await using var r=await Rig.Start();r.Upload.DuplicateOf=Guid.NewGuid();using var request=r.Post();
        using var response=await r.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var json=await response.Content.ReadFromJsonAsync<JsonElement>();var item=json.GetProperty("items")[0];
        Assert.Equal("DUPLICATE",item.GetProperty("processingStatus").GetString());
        Assert.Equal(r.Upload.DuplicateOf,item.GetProperty("existingAnalysisId").GetGuid());
    }
    [Fact] public async Task No_bearer_is_rejected_before_parsing()
    {await using var r=await Rig.Start();r.Client.DefaultRequestHeaders.Authorization=null;using var result=await r.Client.PostAsync("/api/v1/analyses",new StringContent("not multipart"));await Code(result,401,"ERR_UNAUTHORIZED");Assert.Null(r.Upload.Last);}
    [Theory][InlineData("SYSTEM_ADMIN")][InlineData("UNSUPPORTED")]
    public async Task Role_is_checked_before_body(string role)
    {await using var r=await Rig.Start();r.Identity.Roles=[role];using var request=r.Post();using var result=await r.Client.SendAsync(request);await Code(result,403,"ERR_FORBIDDEN");Assert.Null(r.Upload.Last);}
    [Fact] public async Task Missing_key_is_400()
    {await using var r=await Rig.Start();using var request=r.Post();request.Headers.Remove("Idempotency-Key");using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");}
    [Fact] public async Task Multiple_keys_are_400()
    {await using var r=await Rig.Start();using var request=r.Post();request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");}
    [Fact] public async Task Non_multipart_is_415()
    {await using var r=await Rig.Start();using var request=r.Post();request.Content=new StringContent("{}");using var result=await r.Client.SendAsync(request);await Code(result,415,"ERR_UNSUPPORTED_MEDIA_TYPE");}
    [Fact] public async Task Missing_boundary_is_400()
    {await using var r=await Rig.Start();using var request=r.Post();request.Content!.Headers.ContentType=new("multipart/form-data");using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");}
    [Fact] public async Task File_bytes_are_bounded_even_without_content_length()
    {await using var r=await Rig.Start(imageLimit:4);using var request=r.Post(5);request.Headers.TransferEncodingChunked=true;using var result=await r.Client.SendAsync(request);await Code(result,413,"ERR_PAYLOAD_TOO_LARGE");Assert.Null(r.Identity.LastLength);Assert.Null(r.Upload.Last);}
    [Fact] public async Task Total_request_length_is_bounded()
    {await using var r=await Rig.Start(total:10);using var request=r.Post();using var result=await r.Client.SendAsync(request);await Code(result,413,"ERR_PAYLOAD_TOO_LARGE");}
    [Fact] public async Task Duplicate_fields_are_400()
    {await using var r=await Rig.Start();using var request=r.Post();((MultipartFormDataContent)request.Content!).Add(new StringContent("x"),"patientId");using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");}
    [Fact] public async Task Unknown_identity_field_cannot_supply_authority()
    {await using var r=await Rig.Start();using var request=r.Post();((MultipartFormDataContent)request.Content!).Add(new StringContent(Guid.NewGuid().ToString()),"requesterId");using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");}
    [Fact] public async Task Clinic_is_an_explicit_untrusted_selector()
    {await using var r=await Rig.Start();var clinic=Guid.NewGuid();using var request=r.Post(path:"/api/v1/analyses?clinicContextId="+clinic);using var result=await r.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,result.StatusCode);Assert.Equal(clinic,r.Upload.Last!.ClinicId);}
    [Fact] public async Task Malformed_clinic_is_400()
    {await using var r=await Rig.Start();using var request=r.Post(path:"/api/v1/analyses?clinicContextId=bad");using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");}
    [Fact] public async Task Dependency_exception_is_sanitized()
    {await using var r=await Rig.Start();r.Upload.Fail=true;using var request=r.Post();using var result=await r.Client.SendAsync(request);await Code(result,503,"ERR_NETWORK");Assert.DoesNotContain("PRIVATE_DEPENDENCY_DIAGNOSTIC",await result.Content.ReadAsStringAsync());}
    [Fact] public async Task Bad_correlation_is_replaced_and_valid_correlation_is_forwarded()
    {await using var r=await Rig.Start();using var request=r.Post();request.Headers.Add("X-Correlation-Id","not valid");using var result=await r.Client.SendAsync(request);Assert.True(Guid.TryParse(r.Upload.Last!.CorrelationId,out _));}
    [Fact] public async Task Batch_authorizes_before_reading_items()
    {await using var r=await Rig.Start();var batch=Guid.NewGuid();r.Batch.Identity=new(batch,FakeAccess.Tenant,Guid.NewGuid(),r.Identity.Account);r.Access.Allowed=false;using var denied=await r.Client.GetAsync("/api/v1/batches/"+batch);await Code(denied,404,"ERR_NOT_FOUND");Assert.Equal(0,r.Batch.Reads);r.Access.Allowed=true;using var accepted=await r.Client.GetAsync("/api/v1/batches/"+batch);Assert.Equal(HttpStatusCode.OK,accepted.StatusCode);Assert.Equal(1,r.Batch.Reads);}
    [Fact] public async Task Analysis_batch_progress_route_uses_the_authorized_durable_status_service()
    {
        await using var r=await Rig.Start();var batch=Guid.NewGuid();
        r.Batch.Identity=new(batch,FakeAccess.Tenant,Guid.NewGuid(),r.Identity.Account);
        using var response=await r.Client.GetAsync($"/api/v1/analyses/batches/{batch:D}");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal(1,r.Batch.Reads);
    }
    [Fact] public async Task Duplicate_batch_exposes_reference_only_after_resource_authorization()
    {
        await using var r=await Rig.Start();var batch=Guid.NewGuid();r.Batch.Identity=new(batch,FakeAccess.Tenant,Guid.NewGuid(),r.Identity.Account);
        r.Batch.DuplicateOf=Guid.NewGuid();r.Access.Allowed=false;
        using(var denied=await r.Client.GetAsync("/api/v1/batches/"+batch)){await Code(denied,404,"ERR_NOT_FOUND");Assert.Equal(0,r.Batch.Reads);}
        r.Access.Allowed=true;using var response=await r.Client.GetAsync("/api/v1/batches/"+batch);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);var json=await response.Content.ReadFromJsonAsync<JsonElement>();
        var item=json.GetProperty("items")[0];Assert.Equal(r.Batch.DuplicateOf,item.GetProperty("existingAnalysisId").GetGuid());
        Assert.Equal("ERR_DUPLICATE_REQUEST",item.GetProperty("error").GetProperty("code").GetString());
        Assert.False(item.TryGetProperty("job",out _));Assert.Equal(1,json.GetProperty("summary").GetProperty("duplicate").GetInt32());
    }
    [Fact] public async Task Malformed_resource_is_404_after_auth()
    {await using var r=await Rig.Start();using var result=await r.Client.GetAsync("/api/v1/batches/not-a-guid");await Code(result,404,"ERR_NOT_FOUND");}
    [Fact] public async Task File_owner_mismatch_never_signs_url()
    {await using var r=await Rig.Start();var image=Guid.NewGuid();var file=Guid.NewGuid();r.Files.Records.Add(new(image,file,FakeAccess.Tenant,Guid.NewGuid(),Guid.NewGuid(),StoredFileKind.Original,"private/key"));using var result=await r.Client.GetAsync($"/api/v1/analyses/{image}/files/{file}/access");await Code(result,404,"ERR_NOT_FOUND");Assert.Null(r.Storage.LastReadAccess);}
    [Fact] public async Task Authorized_file_access_is_no_store()
    {await using var r=await Rig.Start();var image=Guid.NewGuid();var file=Guid.NewGuid();r.Files.Records.Add(new(image,file,FakeAccess.Tenant,Guid.NewGuid(),r.Identity.Account,StoredFileKind.Original,"private/key"));using var result=await r.Client.GetAsync($"/api/v1/analyses/{image}/files/{file}/access");Assert.Equal(HttpStatusCode.OK,result.StatusCode);Assert.Equal("no-store",result.Headers.CacheControl!.ToString());Assert.Null(r.Storage.LastReadAccess);Assert.Contains("/content",await result.Content.ReadAsStringAsync());}
    [Fact] public async Task Image_bytes_have_verified_MIME_and_revoked_assignment_denies_the_same_URL()
    {
        await using var r=await Rig.Start();var image=Guid.NewGuid();var file=Guid.NewGuid();
        r.Files.Records.Add(new(image,file,FakeAccess.Tenant,Guid.NewGuid(),r.Identity.Account,StoredFileKind.Original,"private/key"));
        r.Storage.Files["aura-originals/private/key"]=TestImages.Png(10,10);
        var url=$"/api/v1/analyses/{image}/files/{file}/content";
        using var allowed=await r.Client.GetAsync(url);Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
        Assert.Equal("image/png",allowed.Content.Headers.ContentType!.MediaType);Assert.Equal("no-store",allowed.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff",Assert.Single(allowed.Headers.GetValues("X-Content-Type-Options")));
        var audit=Assert.Single(r.Audit.Events);Assert.Equal(AuditAction.ImageRead,audit.Action);
        Assert.Equal(r.Identity.Account,audit.ActorAccountId);Assert.Equal(file.ToString("D"),audit.ResourceId);
        r.Access.Allowed=false;using var denied=await r.Client.GetAsync(url);await Code(denied,404,"ERR_NOT_FOUND");
        Assert.Equal(2,r.Audit.Events.Count);Assert.Equal(AuditOutcome.Denied,r.Audit.Events[1].Outcome);
    }
    [Fact] public async Task Audit_failure_blocks_image_bytes_and_disposes_the_open_private_stream()
    {
        await using var r=await Rig.Start();var image=Guid.NewGuid();var file=Guid.NewGuid();
        r.Files.Records.Add(new(image,file,FakeAccess.Tenant,Guid.NewGuid(),r.Identity.Account,StoredFileKind.Original,"private/key"));
        using var stream=new MemoryStream(TestImages.Png(10,10));r.Storage.ReadOverride=(_,_)=>stream;r.Audit.Down=true;
        using var response=await r.Client.GetAsync($"/api/v1/analyses/{image}/files/{file}/content?token=PRIVATE_TOKEN");
        await Code(response,503,"ERR_NETWORK");Assert.False(stream.CanRead);
        Assert.Empty(r.Audit.Events);Assert.DoesNotContain("PRIVATE_TOKEN",await response.Content.ReadAsStringAsync());
    }
    private sealed class AuditWriter:IAuditWriter
    {
        public readonly List<AuditEvent> Events=[];public bool Down;
        public Task AppendAsync(AuditEvent e,CancellationToken ct)
        {if(Down)throw new IOException("PRIVATE_AUDIT_FAILURE");Events.Add(e);return Task.CompletedTask;}
    }
    [Fact] public async Task Request_over_capacity_is_rejected_without_running_handler()
    {await using var r=await Rig.Start();var entered=new TaskCompletionSource();var release=new TaskCompletionSource();r.Upload.Before=async()=>{entered.SetResult();await release.Task;};using var first=r.Post();var running=r.Client.SendAsync(first);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));try{using var second=r.Post();using var response=await r.Client.SendAsync(second);await Code(response,409,"ERR_REQUEST_IN_PROGRESS");}finally{release.SetResult();}using var done=await running;Assert.Equal(HttpStatusCode.Accepted,done.StatusCode);}
    [Fact] public async Task Rejected_body_releases_slot_for_next_request()
    {await using var r=await Rig.Start(imageLimit:4);using var bad=r.Post(5);using var denied=await r.Client.SendAsync(bad);Assert.Equal(HttpStatusCode.RequestEntityTooLarge,denied.StatusCode);using var good=r.Post(3);using var ok=await r.Client.SendAsync(good);Assert.Equal(HttpStatusCode.Accepted,ok.StatusCode);}
    [Fact] public async Task Buffer_spill_preserves_bytes_for_handler()
    {await using var r=await Rig.Start();using var request=r.Post(70000);using var result=await r.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,result.StatusCode);Assert.Equal(70000,r.Upload.Length);}
    [Fact] public async Task Truncated_multipart_is_a_validation_error()
    {await using var r=await Rig.Start();using var request=r.Post();var type=request.Content!.Headers.ContentType;var raw=await request.Content.ReadAsByteArrayAsync();request.Content=new ByteArrayContent(raw[..^20]);request.Content.Headers.ContentType=type;using var result=await r.Client.SendAsync(request);await Code(result,400,"ERR_VALIDATION");Assert.Null(r.Upload.Last);}
    [Theory][InlineData("database")][InlineData("role")][InlineData("secret")][InlineData("origin")]
    public void Runtime_rejects_invalid_configuration_before_dependencies_are_used(string mutation)
    {
        var settings=new Dictionary<string,string?>{["ConnectionStrings:Postgres"]="Host=127.0.0.1;Database=synthetic;Username=aura_analysis_app;Password=synthetic-only;",
            ["ServiceAuth:ClientId"]="analysis-api",["ServiceAuth:ClientSecret"]="synthetic-secret",
            ["Services:Identity:BaseUrl"]="http://127.0.0.1:5004",["Services:Profile:BaseUrl"]="http://127.0.0.1:5005",["Services:Billing:BaseUrl"]="http://127.0.0.1:5002"};
        if(mutation=="database")settings["ConnectionStrings:Postgres"]=null;
        if(mutation=="role")settings["ConnectionStrings:Postgres"]="Host=127.0.0.1;Database=synthetic;Username=aura_dev;Password=synthetic-only;";
        if(mutation=="secret")settings["ServiceAuth:ClientSecret"]=null;
        if(mutation=="origin")settings["Services:Identity:BaseUrl"]="http://example.invalid/wrong-path";
        var b=WebApplication.CreateBuilder();var config=new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var error=Assert.Throws<InvalidOperationException>(()=>b.Services.AddAnalysisRuntime(config,b.Environment));
        Assert.DoesNotContain("synthetic-secret",error.Message);Assert.DoesNotContain("synthetic-only",error.Message);
    }
}
