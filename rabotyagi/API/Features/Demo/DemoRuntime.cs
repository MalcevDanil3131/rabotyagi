using System.Net;
using Npgsql;

namespace rabotyagi.API.Features.Demo;

public sealed record DemoRuntime(bool Enabled, int Port = 5189)
{
    public static DemoRuntime Validate(string environment, IConfiguration config)
    {
        var enabled = config.GetValue<bool>("Demo:Enabled");
        if (!enabled)
        {
            if (environment == "Demo") throw new InvalidOperationException("Demo environment requires Demo:Enabled=true.");
            return new(false);
        }
        if (environment != "Demo") throw new InvalidOperationException("Quick demo login is allowed only in the Demo environment.");
        var connection = config.GetConnectionString("DemoConnection");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("A separate DemoConnection is required.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        if (parsed.Database == null || !System.Text.RegularExpressions.Regex.IsMatch(parsed.Database, "^rabotyagi_demo_[a-z0-9_]+$"))
            throw new InvalidOperationException("Demo database name must start with rabotyagi_demo_ and contain only lowercase letters, digits and underscores.");
        foreach (var name in new[] { "DefaultConnection", "ProductionConnection" })
        {
            var other = config.GetConnectionString(name);
            // A matching database name is rejected even if hosts use different aliases.
            if (!string.IsNullOrWhiteSpace(other) && new NpgsqlConnectionStringBuilder(other).Database == parsed.Database)
                throw new InvalidOperationException("DemoConnection must not refer to an application database.");
        }
        if (config.GetValue<bool>("BootstrapAdmin:Enabled") || config.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("Demo mode cannot use bootstrap or custom Kestrel endpoints.");
        var port = config.GetValue("Demo:Port", 5189);
        if (port is < 1024 or > 65535) throw new InvalidOperationException("Invalid demo port.");
        return new(true, port);
    }

    public bool Allows(HttpRequest request)
    {
        if (!Enabled || request.HttpContext.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
            || !IsLocalHost(request.Host.Host) || request.Headers["X-Rabotyagi-Demo"] != "1") return false;
        var origin = request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && IsLocalHost(uri.Host));
    }

    private static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
