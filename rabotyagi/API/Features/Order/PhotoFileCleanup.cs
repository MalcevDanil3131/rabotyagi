using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using rabotyagi.DataAccess;

namespace rabotyagi.API.Features.Order;

// Tickets live in the persistent uploads volume. Never scan/delete arbitrary orphan files:
// only files explicitly retired by SubmitReport are eligible for cleanup.
public sealed class PhotoFileCleanup(IServiceScopeFactory scopes, IConfiguration config, ILogger<PhotoFileCleanup> logger) : BackgroundService
{
    private string Root => config["Uploads:Path"] ?? Path.Combine(AppContext.BaseDirectory, "uploads");
    private string Queue => Path.Combine(Root, ".cleanup");
    private static bool Valid(string name) => Regex.IsMatch(name, @"\A[0-9a-fA-F-]{32,36}\.(png|jpg|webp)\z");

    public async Task DeleteOrDeferAsync(string fileName)
    {
        if (!Valid(fileName)) { logger.LogWarning("Refused unsafe photo cleanup filename"); return; }
        var ticket = Path.Combine(Queue, fileName + ".pending");
        try
        {
            Directory.CreateDirectory(Queue);
            using var marker = new FileStream(ticket, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        }
        catch (Exception error) { logger.LogWarning(error, "Could not persist photo cleanup ticket {FileName}", fileName); }
        // In particular, cleanup must not mask the outcome of SaveChanges, including rollback.
        await TryDeleteAsync(fileName, ticket);
    }

    private async Task TryDeleteAsync(string fileName, string ticket)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A fresh connection also protects files if the result of a failed commit was uncertain.
            if (!await db.OrderPhotos.AsNoTracking().AnyAsync(x => x.FileName == fileName))
                File.Delete(Path.Combine(Root, fileName));
            File.Delete(ticket);
        }
        catch (Exception error) { logger.LogWarning(error, "Photo cleanup deferred for {FileName}", fileName); }
    }

    public async Task RetryPendingAsync()
    {
        try
        {
            if (!Directory.Exists(Queue)) return;
            foreach (var ticket in Directory.EnumerateFiles(Queue, "*.pending"))
            {
                var fileName = Path.GetFileName(ticket)[..^8];
                if (Valid(fileName)) await TryDeleteAsync(fileName, ticket);
                else logger.LogWarning("Ignored invalid photo cleanup ticket");
            }
        }
        catch (Exception error) { logger.LogWarning(error, "Could not read pending photo cleanup tickets"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do { await RetryPendingAsync(); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
