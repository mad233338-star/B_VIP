using B_VIP.Application.Interfaces;
using B_VIP.Backend.Endpoints;
using B_VIP.Backend.Services;
using B_VIP.Infrastructure.Services;
using B_VIP.Infrastructure;
using B_VIP.Infrastructure.Data;
using B_VIP.Infrastructure.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.RateLimiting;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting B_VIP Tool backend");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    (string databaseProvider, string databaseDetail) = await DatabaseProviderChoice.DecideAsync(builder.Configuration);
    Log.Information("Database provider: {Provider} - {Detail}", databaseProvider, databaseDetail);

    builder.Services.AddB_VIPInfrastructure(builder.Configuration, databaseProvider);
    builder.Services.AddSingleton<IWebHostEnvironmentAccessor, HostEnvironmentAccessor>();
    builder.Services.AddHttpClient();

    builder.Services.AddProblemDetails();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new()
        {
            Title = "B_VIP Tool API",
            Version = "v1",
            Description = "Backend for B_VIP Tool by Bashir Al-Farhan",
            Contact = new() { Name = "Bashir Al-Farhan" }
        });
        options.AddSecurityDefinition("Bearer", new()
        {
            Description = "Access token obtained from /api/auth/login",
            Name = "Authorization",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT"
        });
        options.AddSecurityRequirement(new()
        {
            {
                new()
                {
                    Reference = new()
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });
    });

    string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:3000"];

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("Clients", policy => policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        options.AddFixedWindowLimiter("auth", limiterOptions =>
        {
            limiterOptions.PermitLimit = 10;
            limiterOptions.Window = TimeSpan.FromMinutes(1);
            limiterOptions.QueueLimit = 0;
            limiterOptions.AutoReplenishment = true;
        });

        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                error = new { code = "rate-limited", message = "Too many requests. Slow down and retry later." }
            }, ct);
        };
    });

    builder.Services.AddHealthChecks();

    WebApplication app = builder.Build();

    ValidateSecurityConfiguration(app);

    app.UseMiddleware<B_VIP.Backend.Middleware.ExceptionMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseMiddleware<B_VIP.Backend.Middleware.CorrelationMiddleware>();

    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("OpenApi:ExposeInProduction", false))
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors("Clients");
    app.UseRateLimiter();
    app.UseMiddleware<B_VIP.Backend.Auth.AuthenticationMiddleware>();
    app.UseMiddleware<B_VIP.Backend.Middleware.MaintenanceMiddleware>();

    app.MapHealthChecks("/api/health");

    app.MapB_VIPAuthEndpoints();
    app.MapB_VIPUserEndpoints();
    app.MapB_VIPLicenseEndpoints();
    app.MapB_VIPOperationEndpoints();
    app.MapB_VIPTicketEndpoints();
    app.MapB_VIPAdminEndpoints();
    app.MapB_VIPTechnicianEndpoints();
    app.MapB_VIPMetaEndpoints();

    await InitializeDatabaseAsync(app);

    app.Run();
    return 0;
}
// The test host (WebApplicationFactory) aborts the entry point by throwing a private
// StopTheHostException from inside builder.Build(). Swallowing it here would make the factory
// report "the entry point exited without ever building an IHost", so it is allowed through.
catch (Exception ex) when (ex.GetType().Name != "StopTheHostException")
{
    Log.Fatal(ex, "B_VIP Tool backend terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static void ValidateSecurityConfiguration(WebApplication app)
{
    AuthOptions auth = app.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
    if (string.IsNullOrWhiteSpace(auth.SigningKey) || auth.SigningKey.Length < 32)
    {
        throw new InvalidOperationException(
            "Auth:SigningKey is missing or too short (minimum 32 characters). " +
            "Set it in appsettings, user-secrets or the Auth__SigningKey environment variable before starting the server.");
    }
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    using IServiceScope scope = app.Services.CreateScope();
    B_VIPDbContext db = scope.ServiceProvider.GetRequiredService<B_VIPDbContext>();

    bool useSqlite = string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite",
        StringComparison.OrdinalIgnoreCase);

    if (useSqlite)
    {
        // The local fallback file carries no migrations: the schema is generated from the EF
        // model and created on first start (and left untouched afterwards).
        bool created = await db.Database.EnsureCreatedAsync();
        Log.Information("SQLite fallback in use ({Path}); schema {Action}",
            db.Database.GetDbConnection().DataSource,
            created ? "created" : "already up to date");
    }
    else
    {
        IEnumerable<string> pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Any())
        {
            Log.Information("Applying {Count} pending migrations", pending.Count());
            await db.Database.MigrateAsync();
        }
        else
        {
            Log.Information("Database schema is up to date");
        }
    }

    // Adds the account columns introduced with technician onboarding to databases created earlier.
    await SchemaUpgrader.ApplyAsync(db);

    IDataSeeder seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
    SeedResult result = await seeder.EnsureSeededAsync();
    Log.Information(
        "Reference data ready (alreadySeeded={AlreadySeeded}, roles={Roles}, permissions={Permissions}, plans={Plans})",
        result.WasAlreadySeeded, result.RolesCreated, result.PermissionsCreated, result.PlansCreated);
}

public partial class Program
{
    // Allows WebApplicationFactory based integration, API and security tests to boot the host.
}
