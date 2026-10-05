using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketPortal.Api.Data;
using TicketPortal.Api.Models.Identity;
using TicketPortal.Api.Tests.Infrastructure;
using Xunit;

namespace TicketPortal.Api.Tests.Integration;

[Collection(SharedApiCollection.Name)]
public sealed class AccountSecurityTests(TicketPortalWebApplicationFactory factory)
{
    [Fact]
    public async Task FailedLogins_UseSameUnauthorizedBody_ForUnknownWrongDisabledAndLockedAccounts()
    {
        using var scope = factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(DemoAccounts.Customer);
        Assert.NotNull(user);

        var originalActive = user.IsActive;
        var originalLockoutEnabled = user.LockoutEnabled;
        var originalLockoutEnd = user.LockoutEnd;
        try
        {
            using var client = factory.CreateClient();
            var unknown = await client.PostAsJsonAsync("/api/account/login", new
            {
                UserName = "missing-user-security-check",
                Password = DemoAccounts.Password
            });
            var wrongPassword = await client.PostAsJsonAsync("/api/account/login", new
            {
                UserName = DemoAccounts.Customer,
                Password = "definitely-wrong"
            });

            user.IsActive = false;
            await users.UpdateAsync(user);
            var disabled = await client.PostAsJsonAsync("/api/account/login", new
            {
                UserName = DemoAccounts.Customer,
                Password = DemoAccounts.Password
            });

            user.IsActive = true;
            user.LockoutEnabled = true;
            await users.UpdateAsync(user);
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(10));
            var locked = await client.PostAsJsonAsync("/api/account/login", new
            {
                UserName = DemoAccounts.Customer,
                Password = DemoAccounts.Password
            });

            var responses = new[] { unknown, wrongPassword, disabled, locked };
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
            Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        }
        finally
        {
            user.IsActive = originalActive;
            user.LockoutEnabled = originalLockoutEnabled;
            await users.UpdateAsync(user);
            await users.SetLockoutEndDateAsync(user, originalLockoutEnd);
        }
    }

    [Fact]
    public async Task ChangingPassword_RejectsPreviouslyIssuedAccessToken()
    {
        using var anonymousClient = factory.CreateClient();
        var username = $"security-{Guid.NewGuid():N}";
        const string originalPassword = "Initial!123";
        const string newPassword = "Changed!456";
        var registration = await anonymousClient.PostAsJsonAsync("/api/account/register", new
        {
            FullName = "Security Test",
            UserName = username,
            Email = $"{username}@example.test",
            Password = originalPassword
        });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        try
        {
            var token = await anonymousClient.LoginAsync(username, originalPassword);
            using var authenticated = factory.CreateClient();
            authenticated.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var changed = await authenticated.PostAsJsonAsync("/api/account/change-password", new
            {
                CurrentPassword = originalPassword,
                NewPassword = newPassword
            });
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

            var staleTokenResponse = await authenticated.GetAsync("/api/account/me");
            Assert.Equal(HttpStatusCode.Unauthorized, staleTokenResponse.StatusCode);
        }
        finally
        {
            using var scope = factory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.UserName == username);
            if (user is not null)
            {
                db.LoginHistories.RemoveRange(db.LoginHistories.Where(history => history.UserId == user.Id));
                db.UserRoles.RemoveRange(db.UserRoles.Where(role => role.UserId == user.Id));
                db.UserClaims.RemoveRange(db.UserClaims.Where(claim => claim.UserId == user.Id));
                db.UserLogins.RemoveRange(db.UserLogins.Where(login => login.UserId == user.Id));
                db.UserTokens.RemoveRange(db.UserTokens.Where(token => token.UserId == user.Id));
                db.Users.Remove(user);
                await db.SaveChangesAsync();
            }
        }
    }
}

public sealed class ProductionBootstrapTests : IAsyncLifetime
{
    private readonly string databaseName = $"TicketPortalProductionSecurity_{Guid.NewGuid():N}";
    private readonly string privateFilesPath = Path.Combine(Path.GetTempPath(), $"TicketPortalPrivateFiles_{Guid.NewGuid():N}");
    private ProductionFactory? factory;

    public async Task InitializeAsync()
    {
        factory = new ProductionFactory(
            $@"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog={databaseName};Integrated Security=True;TrustServerCertificate=True",
            privateFilesPath);
        using var client = factory.CreateClient();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProductionStartup_WithoutBootstrapSecrets_DoesNotCreateTheDevelopmentAdmin()
    {
        using var scope = factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(user => user.UserName == "admin"));
    }

    public async Task DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        try
        {
            await using var connection = new SqlConnection(
                @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IF EXISTS [{databaseName}];";
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Test database cleanup is best effort if startup failed before a database existed.
        }
    }

    private sealed class ProductionFactory(string connectionString, string privateFilesPath)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("JWT:SigningKey", new string('K', 48));
            builder.UseSetting("Storage:PrivateFilesRoot", privateFilesPath);
            builder.UseSetting("Payments:DemoMode", "false");
        }
    }
}
