using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface IConfigurationRepository : IRepository<Configuration>
    {
        /// <summary>
        /// Gets the application configuration from the database.
        /// </summary>
        /// <returns>The configuration entity.</returns>
        /// <exception cref="InvalidOperationException">Thrown when configuration is not found in the database.</exception>
        Task<Configuration> GetConfigurationAsync();
        
        Task UpdateConfigurationAsync(Configuration configuration);
    }
}