using AuthService.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class RedisAuthenticationRateLimitMiddlewareTests
{
    [Theory]
    [InlineData("/api/accounts/login")]
    [InlineData("/api/accounts/LOGIN")]
    [InlineData("/API/Accounts/Login/")]
    public void PolicyComesFromTheMatchedEndpointForAnyPathCasing(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("login")),
            "login"));

        Assert.Equal("login", RedisAuthenticationRateLimitMiddleware.ResolvePolicyName(context));
    }

    [Fact]
    public void EndpointsWithoutAnAuthenticationPolicyAreNotLimited()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute("other")),
            "other"));

        Assert.Null(RedisAuthenticationRateLimitMiddleware.ResolvePolicyName(context));
        Assert.Null(RedisAuthenticationRateLimitMiddleware.ResolvePolicyName(new DefaultHttpContext()));
    }
}
