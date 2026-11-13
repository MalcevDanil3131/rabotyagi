using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Conventions;
using rabotyagi.API.Features.Account;
using rabotyagi.API.Features.Address;
using rabotyagi.API.Features.Order;
using rabotyagi.API.Features.Worker;
using rabotyagi.API.Features.Users;
using rabotyagi.API.Utils;
using rabotyagi.Core.Extensions;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.API.Features.Demo;

var builder = WebApplication.CreateBuilder(args);

// Fail before connecting to PostgreSQL or applying migrations if demo isolation is invalid.
var demo = DemoRuntime.Validate(builder.Environment.EnvironmentName, builder.Configuration);
builder.Services.AddSingleton(demo);
var trustedProxies = (builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    .Select(System.Net.IPAddress.Parse).ToArray();
if (demo.Enabled && trustedProxies.Length > 0)
    throw new InvalidOperationException("Demo mode requires direct local requests without forwarded headers.");
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in trustedProxies) options.KnownProxies.Add(proxy);
    });
}
if (demo.Enabled)
{
    builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, demo.Port));
    // Do not share uploaded files, cookie keys or cookie name with a working application.
    var demoRoot = Path.Combine(builder.Environment.ContentRootPath, ".demo",
        new Npgsql.NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("DemoConnection")).Database!);
    builder.Configuration["Uploads:Path"] = Path.Combine(demoRoot, "uploads");
    builder.Configuration["DataProtection:Path"] = Path.Combine(demoRoot, "keys");
    Directory.CreateDirectory(builder.Configuration["DataProtection:Path"]!);
}

var connectionString =
    builder.Configuration.GetConnectionString(demo.Enabled ? "DemoConnection" : builder.Environment.IsDevelopment()
        ? "DefaultConnection"
        : "ProductionConnection");

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString,
        b => b.MigrationsAssembly("rabotyagi")));

builder.Services.AddIdentity<User, IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddTokenProvider<DataProtectorTokenProvider<User>>(TokenOptions.DefaultProvider);

builder.Services.ConfigureApplicationCookie(options =>
{
    if (demo.Enabled) options.Cookie.Name = "Rabotyagi.Demo.Identity";
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
});


builder.AddRequestSecurity(demo.Enabled);

builder.Services.AddAuthorization();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
});

builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ClaimsPrincipalUtils>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddSingleton<PhotoFileCleanup>();
builder.Services.AddHostedService(services => services.GetRequiredService<PhotoFileCleanup>());
builder.Services.AddScoped<AddressService>();
builder.Services.AddScoped<WorkerService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddHttpContextAccessor();
if (builder.Configuration["DataProtection:Path"] is { } keyPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

var app = builder.Build();
if (trustedProxies.Length > 0) app.UseForwardedHeaders();

if (demo.Enabled || app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

using var scope = app.Services.CreateScope();
var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
if (builder.Configuration.GetValue<bool>("Database:MigrateOnly"))
{
    await dbContext.Database.MigrateAsync();
    return;
}
if (builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
    await dbContext.Database.MigrateAsync();
else if ((await dbContext.Database.GetPendingMigrationsAsync()).Any())
    throw new InvalidOperationException("Database migrations are pending. Run the migration step before starting the API.");

await app.SeedRolesAndAdminAsync();
if (builder.Configuration.GetValue<bool>("BootstrapAdmin:InitializeOnly")) return;
if (demo.Enabled) await DemoSeeder.SeedAsync(app.Services, builder.Configuration);
app.UseHttpsRedirection();
app.UseRouting();
app.UseBrowserRequestGuard();
app.UseCors("Cors");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();
app.MapControllers();
app.MapGet("/healthz/ready", async (AppDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.Run();
