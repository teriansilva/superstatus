using SuperStatus.Data.DatabaseContext;

namespace SuperStatus.Identity.Services
{
    public static class ServiceRegistration
    {

        public static void AddSuperStatusIdentityServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddSuperStatusIdentityDb(configuration);
            services.AddSuperStatusIdentity();
            services.AddDatabaseDeveloperPageExceptionFilter();
        }
    }
}
