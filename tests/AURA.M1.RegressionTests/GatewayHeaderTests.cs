using AURA.ApiGateway;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AURA.M1.RegressionTests;

public class GatewayHeaderTests
{
    [Theory]
    [InlineData("POST","/api/v1/analyses/camera",true)]
    [InlineData("POST","/api/v1/analyses/camera/",true)]
    [InlineData("GET","/api/v1/analyses/camera",false)]
    [InlineData("POST","/api/v1/analyses/camera/other",false)]
    [InlineData("POST","/api/v1/analyses",false)]
    public void Only_camera_admission_keeps_the_untrusted_tenant_selector(string method,string path,bool camera)
    {
        var request=new DefaultHttpContext().Request;request.Method=method;request.Path=path;
        request.Headers["X-Tenant-Id"]="selector-must-be-revalidated";
        request.Headers["X-Account-Id"]=Guid.NewGuid().ToString("D");request.Headers["X-Role"]="SYSTEM_ADMIN";
        request.Headers["X-User-Authorization"]="spoofed";request.Headers.Authorization="Bearer service-credential";
        GatewayFirebaseAuthMiddleware.RemoveUntrustedIdentityHeaders(request);
        Assert.Equal(camera,request.Headers.ContainsKey("X-Tenant-Id"));
        Assert.False(request.Headers.ContainsKey("X-Account-Id"));Assert.False(request.Headers.ContainsKey("X-Role"));
        Assert.False(request.Headers.ContainsKey("X-User-Authorization"));
        Assert.Equal("Bearer service-credential",request.Headers.Authorization.ToString());
    }
}
