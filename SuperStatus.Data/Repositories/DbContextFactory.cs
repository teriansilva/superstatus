using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SuperStatus.Data.Repositories
{
    public class SuperDbContextFactory<TContext> : IDbContextFactory<TContext> where TContext : DbContext
    {
        private readonly IServiceProvider serviceProvider;

        public SuperDbContextFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public TContext CreateDbContext()
        {
            return serviceProvider.GetRequiredService<TContext>();
        }
    }
}
