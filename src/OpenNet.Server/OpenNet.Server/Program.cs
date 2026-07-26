using Microsoft.EntityFrameworkCore;
using OpenNet.Server.Application;
using OpenNet.Server.Infrastructure;
using OpenNet.Server.Options;

namespace OpenNet.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services
            .AddOptions<TraversalDirectoryOptions>()
            .BindConfiguration(TraversalDirectoryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        string connectionString = builder.Configuration.GetConnectionString("TraversalDirectory")
            ?? "Data Source=opennet-server.db";
        string databaseProvider =
            builder.Configuration["DatabaseProvider"] ?? "Sqlite";

        builder.Services.AddDbContext<TraversalDbContext>(
            options =>
            {
                if (databaseProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseMySQL(connectionString);
                }
                else if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseSqlite(connectionString);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unsupported DatabaseProvider '{databaseProvider}'. "
                        + "Use 'Sqlite' or 'MySql'.");
                }
            });
        builder.Services.AddScoped<ITraversalServerRepository, TraversalServerRepository>();
        builder.Services.AddScoped<TraversalDirectoryService>();
        builder.Services.AddMemoryCache();
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TraversalDbContext>("traversal-database");
        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        WebApplication app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.MapControllers();
        app.MapHealthChecks("/health");

        await TraversalDatabaseInitializer.InitializeAsync(app.Services);
        await app.RunAsync();
    }
}
