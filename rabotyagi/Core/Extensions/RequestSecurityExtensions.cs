using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace rabotyagi.Core.Extensions;

public sealed class RequestSecurityOptions
{
    [Range(1, 100000)] public int ReadPermitLimit { get; set; } = 600;
    [Range(1, 100000)] public int WritePermitLimit { get; set; } = 120;
    [Range(1, 86400)] public int ApiWindowSeconds { get; set; } = 60;
    [Range(1, 1000)] public int LoginPermitLimit { get; set; } = 10;
    [Range(1, 1000)] public int RegisterPermitLimit { get; set; } = 5;
    [Range(1, 1000)] public int PasswordPermitLimit { get; set; } = 5;
    [Range(1, 1000)] public int DemoPermitLimit { get; set; } = 30;
    [Range(1, 86400)] public int LoginWindowSeconds { get; set; } = 60;
    [Range(1, 86400)] public int RegisterWindowSeconds { get; set; } = 600;
    [Range(1, 86400)] public int PasswordWindowSeconds { get; set; } = 600;
    [Range(1, 86400)] public int DemoWindowSeconds { get; set; } = 60;
    [Range(1, 20)] public int FailedLoginLimit { get; set; } = 5;
    [Range(1, 86400)] public int LockoutSeconds { get; set; } = 900;
    public string[] AllowedOrigins { get; set; } = [];
}

public static class RequestSecurityExtensions
{
    public static void AddRequestSecurity(this WebApplicationBuilder builder, bool demo)
    {
        var settings = builder.Configuration.GetSection("RequestSecurity").Get<RequestSecurityOptions>() ?? new();
        if (!builder.Configuration.GetSection("RequestSecurity:AllowedOrigins").Exists() && (demo || builder.Environment.IsDevelopment()))
            settings.AllowedOrigins = ["http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:3000", "http://127.0.0.1:3000"];
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        if (settings.AllowedOrigins.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.GetLeftPart(UriPartial.Authority) != x))
            throw new InvalidOperationException("RequestSecurity:AllowedOrigins must contain exact HTTP(S) origins without paths or trailing slashes.");
        builder.Services.AddSingleton(settings);
        builder.Services.Configure<IdentityOptions>(options =>
        {
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = settings.FailedLoginLimit;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromSeconds(settings.LockoutSeconds);
            options.Password.RequiredLength = 10;
            options.User.RequireUniqueEmail = true;
        });
        builder.Services.AddSingleton<DummyPasswordVerifier>();
        builder.Services.AddCors(options => options.AddPolicy("Cors", policy =>
            policy.WithOrigins(settings.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()
                .AllowCredentials().WithExposedHeaders("Retry-After")));
        builder.Services.AddRateLimiter(options =>
        {
            // Runs before cookie authentication so rejected traffic does not reach Identity/DB.
            // Only the socket peer is trusted. X-Forwarded-For cannot choose a new partition.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!context.Request.Path.StartsWithSegments("/api")) return RateLimitPartition.GetNoLimiter("non-api");
                var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
                return Window((read ? "read:" : "write:") + Peer(context), read ? settings.ReadPermitLimit : settings.WritePermitLimit, settings.ApiWindowSeconds);
            });
            void Policy(string name, int limit, int seconds) => options.AddPolicy(name, context => Window(Peer(context), limit, seconds));
            Policy("login", settings.LoginPermitLimit, settings.LoginWindowSeconds);
            Policy("register", settings.RegisterPermitLimit, settings.RegisterWindowSeconds);
            Policy("password", settings.PasswordPermitLimit, settings.PasswordWindowSeconds);
            Policy("demo-login", settings.DemoPermitLimit, settings.DemoWindowSeconds);
            options.OnRejected = async (context, token) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = $"Слишком много запросов. Повторите через {seconds} сек.", retryAfterSeconds = seconds }, token);
            };
        });
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            var https = builder.Configuration.GetValue<bool?>("RequestSecurity:RequireHttpsCookies")
                ?? (!demo && !builder.Environment.IsDevelopment());
            options.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
    }

    private static string Peer(HttpContext context) => context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
    private static RateLimitPartition<string> Window(string key, int permits, int seconds) => RateLimitPartition.GetFixedWindowLimiter(key,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromSeconds(seconds), QueueLimit = 0, AutoReplenishment = true });

    public static void UseBrowserRequestGuard(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<RequestSecurityOptions>();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                var request = context.Request;
                // Demo endpoints already enforce local Host/Origin and deliberately return 404.
                if (!request.Path.StartsWithSegments("/api/demo") && !HttpMethods.IsGet(request.Method)
                    && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method))
                {
                    var origin = request.Headers.Origin.ToString();
                    var sameOrigin = origin == $"{request.Scheme}://{request.Host}";
                    if ((!string.IsNullOrEmpty(origin) && !sameOrigin && !settings.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
                        || (string.IsNullOrEmpty(origin) && request.Headers["Sec-Fetch-Site"] == "cross-site"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new { error = "Запрос с этого сайта запрещён." });
                        return;
                    }
                }
            }
            await next(context);
        });
    }
}

// Perform password work for unknown/locked users too, without storing any reusable secret.
public sealed class DummyPasswordVerifier
{
    private readonly PasswordHasher<DataAccess.Entities.User> hasher = new();
    private readonly DataAccess.Entities.User user = new() { Name = "", Surname = "" };
    private readonly string hash;
    public DummyPasswordVerifier() => hash = hasher.HashPassword(user, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    public void Verify(string password) => hasher.VerifyHashedPassword(user, hash, password);
}
