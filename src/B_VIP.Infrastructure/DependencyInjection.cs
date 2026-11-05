using B_VIP.Application.Interfaces;
using B_VIP.Infrastructure.Data;
using B_VIP.Infrastructure.Options;
using B_VIP.Infrastructure.Security;
using B_VIP.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace B_VIP.Infrastructure
{
    public static class DependencyInjection
    {
        /// <summary>
        /// Registers EF Core (PostgreSQL, or the local SQLite file when PostgreSQL cannot be
        /// reached), the security primitives and every application service.
        /// </summary>
        /// <param name="databaseProvider">
        /// <see cref="Data.DatabaseProviderChoice.Postgres"/> or
        /// <see cref="Data.DatabaseProviderChoice.Sqlite"/>, decided once at startup.
        /// </param>
        public static IServiceCollection AddB_VIPInfrastructure(
            this IServiceCollection services, IConfiguration configuration, string databaseProvider)
        {
            services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
            services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
            services.Configure<LicenseOptions>(configuration.GetSection(LicenseOptions.SectionName));
            services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
            services.Configure<BootstrapOptions>(configuration.GetSection(BootstrapOptions.SectionName));
            services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

            string connectionString = configuration.GetConnectionString("Default")
                ?? configuration["ConnectionStrings:Default"]
                ?? "Host=127.0.0.1;Port=5433;Database=b_vip_dev;Username=postgres;Password=;Pooling=true";

            bool useSqlite = !string.Equals(databaseProvider, Data.DatabaseProviderChoice.Postgres,
                StringComparison.OrdinalIgnoreCase);

            services.AddDbContext<B_VIPDbContext>(options =>
            {
                if (useSqlite)
                {
                    // Local fallback file: created from the EF model on startup (EnsureCreated),
                    // because the Npgsql migrations are not portable to SQLite.
                    options.UseSqlite(Data.DatabaseProviderChoice.ResolveSqliteConnectionString(configuration));
                }
                else
                {
                    options.UseNpgsql(connectionString, npgsql =>
                    {
                        npgsql.MigrationsHistoryTable("__migrations_history");
                        npgsql.EnableRetryOnFailure(3);
                    });
                }

                options.EnableSensitiveDataLogging(false);
                options.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
            });

            services.AddSingleton<ISecretProtector, AesSecretProtector>();
            services.AddSingleton<ITokenProvider, HmacTokenProvider>();
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

            services.AddScoped<IAuditLogger, AuditLogger>();
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IUserAdminService, UserAdminService>();
            services.AddScoped<ILicenseService, LicenseService>();
            services.AddScoped<IOperationService, OperationService>();
            services.AddScoped<IReportService, ReportService>();
            services.AddScoped<ITicketService, TicketService>();
            services.AddScoped<INotificationService, NotificationService>();
            services.AddScoped<IAdminService, AdminService>();
            services.AddScoped<IDataSeeder, DataSeeder>();

            return services;
        }
    }
}
