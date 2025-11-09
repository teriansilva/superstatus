using SuperStatus.Data.DatabaseContext;
using SuperStatus.Services.Services;


namespace SuperStatus.Services
{
    /// <summary>
    /// Initializes all the require application services
    /// </summary>
    public static class ServiceRegistration
    {
        public static void AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
        {

            services.AddSuperStatusDb(configuration); 
            services.AddRepositories();

            services.AddHttpClient();

            services.AddScoped<IStatusCheckService, StatusCheckService>();
            services.AddScoped<IIncidentService, IncidentService>();
            services.AddScoped<IConfigurationService, ConfigurationService>();

        }
    }
}
