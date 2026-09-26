using Microsoft.EntityFrameworkCore;

namespace OpenNet.Server.Infrastructure;

public static class ContentDirectoryDatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ContentDirectoryDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<ContentDirectoryDbContext>();

        await dbContext.Database.EnsureCreatedAsync();
    }
}
