using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using rabotyagi.API.Features.Order;
using rabotyagi.API.Features.Order.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

internal static class OrderRepairChecks
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1sAAAAASUVORK5CYII=");
    public static async Task Run(string connection, Guid sourceId, Guid workerId, HttpClient worker, string uploads, Action<bool, string> check)
    {
        if (!new NpgsqlConnectionStringBuilder(connection).Database!.StartsWith("rabotyagi_checks_"))
            throw new InvalidOperationException("Repair checks require an isolated test database.");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Uploads:Path"] = uploads }).Build();
        var claims = new ClaimsPrincipalUtils(new HttpContextAccessor { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, workerId.ToString()), new Claim(ClaimTypes.Role, "Worker")], "checks")) } });
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        var source = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == sourceId);
        async Task<Order> Fresh(EOrderStatus status)
        {
            var order = new Order { InitiatorId = source.InitiatorId, AddressId = source.AddressId, ExecutorId = status == EOrderStatus.Published ? null : workerId,
                OrderType = source.OrderType, Comment = "Isolated repair fixture", Status = status };
            db.Orders.Add(order); await db.SaveChangesAsync(); return order;
        }
        foreach (var change in new[] { "close-slot", "block", "remove-role" })
        {
            var gate = new AssignmentGate();
            var original = await db.Users.AsNoTracking().SingleAsync(x => x.Id == workerId);
            var roles = await db.UserRoles.AsNoTracking().Where(x => x.UserId == workerId).ToListAsync();
            await db.Users.Where(x => x.Id == workerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUntil, DateTimeOffset.UtcNow.AddHours(1)).SetProperty(x => x.LockoutEnd, (DateTimeOffset?)null));
            var order = await Fresh(EOrderStatus.Published);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IConfiguration>(config);
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection).AddInterceptors(gate));
            services.AddSingleton<PhotoFileCleanup>();
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var service = new OrderService(scope.ServiceProvider.GetRequiredService<AppDbContext>(), claims, config, provider.GetRequiredService<PhotoFileCleanup>());
            var assignment = service.SetExecutor(new SetExecutorDto { OrderId = order.Id, ExecutorId = workerId });
            try
            {
                await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (change == "close-slot") await db.Users.Where(x => x.Id == workerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUntil, (DateTimeOffset?)null));
                if (change == "block") await db.Users.Where(x => x.Id == workerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LockoutEnd, DateTimeOffset.UtcNow.AddHours(1)));
                if (change == "remove-role") await db.UserRoles.Where(x => x.UserId == workerId).ExecuteDeleteAsync();
                gate.Resume.TrySetResult(); await assignment;
                var after = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == order.Id);
                check(after.Status == EOrderStatus.Published && after.ExecutorId == null && !await db.OrderHistoryEvents.AnyAsync(x => x.OrderId == order.Id),
                    "concurrent " + change + " prevents first assignment and its history");
            }
            finally
            {
                gate.Resume.TrySetResult(); await assignment;
                if (change == "remove-role") { db.UserRoles.AddRange(roles); await db.SaveChangesAsync(); }
                await db.Users.Where(x => x.Id == workerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AvailableUntil, original.AvailableUntil).SetProperty(x => x.LockoutEnd, original.LockoutEnd));
            }
        }
        var cleanupServices = new ServiceCollection().AddLogging();
        cleanupServices.AddSingleton<IConfiguration>(config);
        cleanupServices.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        cleanupServices.AddSingleton<PhotoFileCleanup>();
        await using var cleanupProvider = cleanupServices.BuildServiceProvider();
        var cleanup = cleanupProvider.GetRequiredService<PhotoFileCleanup>();
        var reported = await Fresh(EOrderStatus.Rework);
        var old = new OrderPhoto { OrderId = reported.Id, OriginalName = "old.png", ContentType = "image/png" };
        old.FileName = old.Id + ".png";
        db.OrderPhotos.Add(old); await db.SaveChangesAsync();
        var oldPath = Path.Combine(uploads, old.FileName);
        await File.WriteAllBytesAsync(oldPath, Png);
        using (var locked = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Corrected report with a new photo"), "Summary");
            foreach (var field in new[] { "WorkDone", "WorkplaceClean", "ClientInformed" }) form.Add(new StringContent("true"), field);
            form.Add(new ByteArrayContent(Png), "Photos", "new.png");
            using var response = await worker.PostAsync($"order/{reported.Id}/report", form);
            check(response.StatusCode == HttpStatusCode.OK, "locked retired photo does not turn a committed report into HTTP 500");
            check((await db.Orders.AsNoTracking().SingleAsync(x => x.Id == reported.Id)).Status == EOrderStatus.WorkFinished
                && await db.OrderHistoryEvents.CountAsync(x => x.OrderId == reported.Id && x.EventType == EOrderHistoryEventType.ReportSubmitted) == 1,
                "report, replacement photo and history remain committed after cleanup failure");
            check(File.Exists(Path.Combine(uploads, ".cleanup", old.FileName + ".pending")), "failed cleanup is recorded durably in uploads volume");
        }
        await cleanup.RetryPendingAsync();
        check(!File.Exists(oldPath) && !File.Exists(Path.Combine(uploads, ".cleanup", old.FileName + ".pending")), "retired photo is deleted when retry succeeds");
        var active = await db.OrderPhotos.AsNoTracking().SingleAsync(x => x.OrderId == reported.Id);
        await cleanup.DeleteOrDeferAsync(active.FileName);
        check(File.Exists(Path.Combine(uploads, active.FileName)), "cleanup never deletes a photo still referenced by database");
        var rollback = await Fresh(EOrderStatus.InProgress);
        var failure = new SaveFailure(uploads);
        await using (var failureDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(failure).Options))
        {
            var service = new OrderService(failureDb, claims, config, cleanup);
            var dto = new SubmitReportDto { Summary = "Rollback report fixture", WorkDone = true, WorkplaceClean = true, ClientInformed = true,
                Photos = [new FormFile(new MemoryStream(Png), 0, Png.Length, "Photos", "test.png")] };
            try { await service.SubmitReport(rollback.Id, dto); check(false, "injected save failure must propagate"); }
            catch (InvalidOperationException error) { check(error.Message == "Injected save failure", "cleanup failure does not mask the original save error"); }
            finally { failure.Locked?.Dispose(); }
        }
        check((await db.Orders.AsNoTracking().SingleAsync(x => x.Id == rollback.Id)).Status == EOrderStatus.InProgress
            && !await db.OrderPhotos.AnyAsync(x => x.OrderId == rollback.Id) && !await db.OrderHistoryEvents.AnyAsync(x => x.OrderId == rollback.Id), "failed save leaves order, photos and history unchanged");
        await cleanup.RetryPendingAsync();
        check(failure.FileName != null && !File.Exists(Path.Combine(uploads, failure.FileName)), "new photo from failed save is removed on retry");
    }

    private sealed class AssignmentGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"Orders\"")) { Reached.TrySetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
    private sealed class SaveFailure(string uploads) : SaveChangesInterceptor
    {
        public FileStream? Locked { get; private set; }
        public string? FileName { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            FileName = eventData.Context!.ChangeTracker.Entries<OrderPhoto>().Single(x => x.State == EntityState.Added).Entity.FileName;
            Locked = new FileStream(Path.Combine(uploads, FileName), FileMode.Open, FileAccess.Read, FileShare.None);
            throw new InvalidOperationException("Injected save failure");
        }
    }
}
