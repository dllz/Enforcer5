using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Enforcer5.Storage
{
    internal static class EnforcerDbContextFactory
    {
        internal static EnforcerDbContext Create(string connectionString)
        {
            var options = new DbContextOptionsBuilder<EnforcerDbContext>()
                .UseNpgsql(connectionString, postgres =>
                    postgres.MigrationsAssembly(typeof(EnforcerDbContext).Assembly.FullName))
                .Options;
            return new EnforcerDbContext(options);
        }
    }

    public sealed class EnforcerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<EnforcerDbContext>
    {
        public EnforcerDbContext CreateDbContext(string[] args)
        {
            return EnforcerDbContextFactory.Create(
                "Host=localhost;Database=enforcer_design;Username=enforcer;Password=enforcer");
        }
    }
}
