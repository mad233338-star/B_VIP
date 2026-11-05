using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace B_VIP.Infrastructure.Data
{
    /// <summary>Design-time factory so `dotnet ef migrations` works without starting the host.</summary>
    public sealed class B_VIPDbContextFactory : IDesignTimeDbContextFactory<B_VIPDbContext>
    {
        public B_VIPDbContext CreateDbContext(string[] args)
        {
            string connectionString = ResolveDesignTimeConnectionString(args);

            var options = new DbContextOptionsBuilder<B_VIPDbContext>()
                .UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsHistoryTable("__migrations_history");
                    npgsql.EnableRetryOnFailure(3);
                })
                .Options;

            return new B_VIPDbContext(options);
        }

        public static string ResolveDesignTimeConnectionString(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--connection" && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return args[i + 1];
                }
            }

            return Environment.GetEnvironmentVariable("BVIP_DB")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                ?? "Host=127.0.0.1;Port=5433;Database=b_vip_dev;Username=postgres;Password=;Pooling=true";
        }
    }
}
