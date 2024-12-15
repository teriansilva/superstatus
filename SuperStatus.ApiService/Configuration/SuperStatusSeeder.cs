using SuperStatus.Data.Repositories;

namespace SuperStatus.ApiService.Configuration
{
    public class SuperStatusSeeder
    {
        private readonly SuperStatusContext context;
        private readonly IWebHostEnvironment hosting;

        public SuperStatusSeeder(SuperStatusContext context, IWebHostEnvironment hosting)
        {

            this.hosting = hosting;
            this.context = context;
        }

        public async Task SeedAsync()
        {
            context.Database.EnsureCreated();

        }


    }
}
