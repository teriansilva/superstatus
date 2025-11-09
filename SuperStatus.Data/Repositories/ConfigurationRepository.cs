using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public class ConfigurationRepository : Repository<Configuration>, IConfigurationRepository
    {
        private readonly SuperStatusDb context;

        public ConfigurationRepository(SuperStatusDb context) : base(context)
        {
            this.context = context;
        }

        /// <summary>
        /// Gets the application configuration from the database.
        /// </summary>
        /// <returns>The configuration entity.</returns>
        /// <exception cref="InvalidOperationException">Thrown when configuration is not found in the database.</exception>
        public async Task<Configuration> GetConfigurationAsync()
        {
            var config = await context.ConfigurationSet
                .OrderBy(c => c.Id)
                .FirstOrDefaultAsync();
            
            if (config is null)
            {
                throw new InvalidOperationException(
                    "Application configuration not found in database. Please ensure the database has been properly seeded.");
            }
            
            return config;
        }

        public async Task UpdateConfigurationAsync(Configuration configuration)
        {
            context.ConfigurationSet.Update(configuration);
            await context.SaveChangesAsync();
        }
    }
}