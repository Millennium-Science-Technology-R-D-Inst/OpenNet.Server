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
        builder.Services
            .AddOptions<ContentDirectoryOptions>()
            .BindConfiguration(ContentDirectoryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services
            .AddOptions<UpdateCatalogOptions>()
            .BindConfiguration(UpdateCatalogOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                options => options.HasValidPackages(),
                "Every update package and mirror must satisfy the update catalog schema.")
            .ValidateOnStart();

        string connectionString = builder.Configuration.GetConnectionString("TraversalDirectory")
            ?? "Data Source=opennet-server.db";
        string databaseProvider =
            builder.Configuration["DatabaseProvider"] ?? "Sqlite";

        builder.Services.AddDbContext<TraversalDbContext>(
            options => ConfigureDatabase(options, databaseProvider, connectionString));

        string? configuredContentConnectionString =
            builder.Configuration.GetConnectionString("ContentDirectory");
        string contentConnectionString = configuredContentConnectionString
            ?? (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)
                ? "Data Source=opennet-content.db"
                : throw new InvalidOperationException(
                    "ConnectionStrings:ContentDirectory must be configured when DatabaseProvider is MySql."));
        builder.Services.AddDbContext<ContentDirectoryDbContext>(
            options => ConfigureDatabase(options, databaseProvider, contentConnectionString));
        builder.Services.AddScoped<ITraversalServerRepository, TraversalServerRepository>();
        builder.Services.AddScoped<TraversalDirectoryService>();
        builder.Services.AddScoped<ContentDirectoryService>();
        builder.Services.AddHostedService<ContentDirectoryCleanupService>();
        builder.Services.AddSingleton<UpdateCatalogService>();
        builder.Services.AddMemoryCache();
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TraversalDbContext>("traversal-database")
            .AddDbContextCheck<ContentDirectoryDbContext>("content-directory-database");
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
        await ContentDirectoryDatabaseInitializer.InitializeAsync(app.Services);
        await app.RunAsync();
    }

    private static void ConfigureDatabase(
        DbContextOptionsBuilder options,
        string databaseProvider,
        string connectionString)
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
                $"Unsupported DatabaseProvider '{databaseProvider}'. Use 'Sqlite' or 'MySql'.");
        }
    }
}
