using Microsoft.EntityFrameworkCore;
using TicketPortal.Api.Data;

namespace TicketPortal.Api.Services;

/// <summary>Moves legacy passenger ID photos out of the publicly served web root.</summary>
public static class PassengerIdPhotoMigration
{
    public static async Task MoveToPrivateStorageAsync(
        AppDbContext db,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var webRoot = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var publicImages = Path.GetFullPath(Path.Combine(webRoot, "images"));
        var privateRoot = ResolvePrivateRoot(configuration, environment);
        Directory.CreateDirectory(privateRoot);
        var publicFilesToDelete = new List<string>();

        var passengers = await db.BookingPassengers
            .Where(passenger => passenger.NationalIdPhotoUrl != null
                && passenger.NationalIdPhotoUrl.StartsWith("/images/"))
            .ToListAsync(cancellationToken);

        foreach (var passenger in passengers)
        {
            var leaf = Path.GetFileName(passenger.NationalIdPhotoUrl);
            if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
            {
                passenger.NationalIdPhotoUrl = null;
                logger.LogWarning("Cleared an invalid legacy passenger photo reference for passenger {PassengerId}.", passenger.Id);
                continue;
            }

            var source = Path.GetFullPath(Path.Combine(publicImages, leaf));
            if (!source.StartsWith(publicImages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(source))
            {
                passenger.NationalIdPhotoUrl = null;
                logger.LogWarning("Cleared a missing legacy passenger photo reference for passenger {PassengerId}.", passenger.Id);
                continue;
            }

            var destination = Path.Combine(privateRoot, leaf);
            if (!File.Exists(destination))
            {
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }

            passenger.NationalIdPhotoUrl = $"private/{leaf}";
            publicFilesToDelete.Add(source);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var source in publicFilesToDelete)
        {
            File.Delete(source);
        }
    }

    public static string ResolvePrivateRoot(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration["Storage:PrivateFilesRoot"];
        if (string.IsNullOrWhiteSpace(configuredPath)
            && !environment.IsDevelopment()
            && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "Storage:PrivateFilesRoot must point to persistent storage outside the public web root outside Development.");
        }

        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "NationalIdPhotos")
            : Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(environment.ContentRootPath, configuredPath));
        var webRoot = Path.GetFullPath(environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (path.Equals(webRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Private file storage must be outside the public web root.");
        }

        return path;
    }
}
