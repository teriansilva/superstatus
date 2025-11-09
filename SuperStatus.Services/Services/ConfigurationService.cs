using Microsoft.Extensions.Logging;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Services;

public interface IConfigurationService
{
    Task<Configuration?> GetConfigurationAsync();
    Task UpdateConfigurationAsync(Configuration configuration);
}

public class ConfigurationService : IConfigurationService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<ConfigurationService> _logger;

    public ConfigurationService(
        IConfigurationRepository configurationRepository,
        ILogger<ConfigurationService> logger)
    {
        _configurationRepository = configurationRepository;
        _logger = logger;
    }

    public async Task<Configuration?> GetConfigurationAsync()
    {
        try
        {
            return await _configurationRepository.GetConfigurationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving configuration");
            throw;
        }
    }

    public async Task UpdateConfigurationAsync(Configuration configuration)
    {
        try
        {
            await _configurationRepository.UpdateConfigurationAsync(configuration);
            _logger.LogInformation("Configuration updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating configuration");
            throw;
        }
    }
}