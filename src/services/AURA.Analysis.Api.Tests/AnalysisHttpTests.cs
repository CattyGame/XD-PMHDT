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
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalysisHttpTests
{
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
        public WebApplication App=null!;public HttpClient Client=null!;
        public static async Task<Rig> Start(long imageLimit=10485760,int images=100,long total=1101004800)
        {
            var r=new Rig();var b=WebApplication.CreateBuilder();b.WebHost.UseTestServer();b.Logging.ClearProviders();
            b.Services.AddSingleton<IAnalysisUserIdentityResolver>(r.Identity);b.Services.AddSingleton<IAnalysisUploadHandler>(r.Upload);
            b.Services.AddSingleton(new UploadOptions{MaxImageBytes=imageLimit,MaxImagesPerRequest=images,MaxRequestBytes=total});
            b.Services.AddSingleton(new AnalysisUploadGate(1));b.Services.AddSingleton(new StorageOptions());
            b.Services.AddSingleton<IPatientAccessResolver>(r.Access);b.Services.AddSingleton<IFileAccessRepository>(r.Files);b.Services.AddSingleton<IPrivateFileStorage>(r.Storage);
            b.Services.AddSingleton<IBatchStatusRepository>(r.Batch);b.Services.AddTransient<BatchStatusService>();b.Services.AddTransient<FileAccessService>();
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
    private static async Task Code(HttpResponseMessage response,int status,string code)
    {
        Assert.Equal(status,(int)response.StatusCode);var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(code,json.GetProperty("error").GetProperty("code").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));Assert.Equal("no-store",response.Headers.CacheControl!.ToString());
    }
    [Fact] public async Task Multipart_binds_authenticated_actor_and_returns_202()
    {await using var r=await Rig.Start();using var request=r.Post();request.Headers.Add("X-Account-Id",Guid.NewGuid().ToString());request.Headers.Add("X-User-Id",Guid.NewGuid().ToString());using var result=await r.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,result.StatusCode);Assert.Equal(r.Identity.Account,r.Upload.Last!.RequesterId);Assert.Equal(10,r.Upload.Length);Assert.NotNull(result.Headers.Location);}
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
        r.Access.Allowed=false;using var denied=await r.Client.GetAsync(url);await Code(denied,404,"ERR_NOT_FOUND");
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
