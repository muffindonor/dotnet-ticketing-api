using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ticketing.Api.Domain;
using Ticketing.Api.Options;

namespace Ticketing.Api.Data;

public static class DatabaseInitializer
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Retries because instances starting together can collide while creating the database
    /// or seeding the admin. The loser simply tries again and finds the work already done.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await InitializeOnceAsync(services, ct);
                return;
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException && attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private static async Task InitializeOnceAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        await db.Database.MigrateAsync(ct);

        var seed = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (string.IsNullOrWhiteSpace(seed.AdminEmail) || string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            return;
        }

        var email = seed.AdminEmail.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return;
        }

        var admin = new User { Id = Guid.NewGuid(), Email = email, Role = UserRole.Admin };
        admin.PasswordHash = new PasswordHasher<User>().HashPassword(admin, seed.AdminPassword);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
    }
}
