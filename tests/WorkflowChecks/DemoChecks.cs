using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using rabotyagi.API.Features.Demo;

internal static class DemoChecks
{
    public static void RunGuards(Action<bool, string> check)
    {
        IConfiguration Config(params (string Key, string? Value)[] items) => new ConfigurationBuilder()
            .AddInMemoryCollection(items.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value))).Build();
        const string testConnection = "Host=localhost;Database=rabotyagi_demo_checks;Username=demo";
        void Reject(string environment, IConfiguration config)
        {
            var rejected = false;
            try { DemoRuntime.Validate(environment, config); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "unsafe demo configuration rejected before database access");
        }
        check(!DemoRuntime.Validate("Production", Config()).Enabled, "demo disabled by default");
        foreach (var environment in new[] { "Production", "Development", "Staging" })
            Reject(environment, Config(("Demo:Enabled", "true"), ("ConnectionStrings:DemoConnection", testConnection)));
        Reject("Demo", Config());
        Reject("Demo", Config(("Demo:Enabled", "true")));
        Reject("Demo", Config(("Demo:Enabled", "true"), ("ConnectionStrings:DemoConnection", "Host=localhost;Database=rabotyagi_db")));
        Reject("Demo", Config(("Demo:Enabled", "true"), ("ConnectionStrings:DemoConnection", testConnection),
            ("ConnectionStrings:ProductionConnection", "Host=another-alias;Database=rabotyagi_demo_checks")));
        Reject("Demo", Config(("Demo:Enabled", "true"), ("ConnectionStrings:DemoConnection", testConnection), ("BootstrapAdmin:Enabled", "true")));
        var runtime = DemoRuntime.Validate("Demo", Config(("Demo:Enabled", "true"), ("ConnectionStrings:DemoConnection", testConnection)));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("127.0.0.1", 5189);
        check(!runtime.Allows(context.Request), "simple requests without demo header are denied");
        context.Request.Headers["X-Rabotyagi-Demo"] = "1";
        context.Request.Headers.Origin = "http://localhost:5173";
        check(runtime.Allows(context.Request), "local frontend can use explicit demo header");
        foreach (var origin in new[] { "https://localhost.evil.example", "null", "https://evil.example", "file:///tmp/page" })
        {
            context.Request.Headers.Origin = origin;
            check(!runtime.Allows(context.Request), "foreign origin cannot switch a demo session");
        }
        context.Request.Headers.Origin = "http://127.0.0.1:5173";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        check(!runtime.Allows(context.Request), "remote callers cannot use demo login");
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("evil.example");
        check(!runtime.Allows(context.Request), "untrusted host cannot use demo login");
        context.Request.Host = new HostString("localhost");
        check(!new DemoRuntime(false).Allows(context.Request), "disabled runtime denies even local demo requests");
        check(DemoAccounts.All.Length == 6 && DemoAccounts.All.Select(x => x.Id).Distinct().Count() == 6,
            "demo catalog has six distinct fixed identities");
    }
}
