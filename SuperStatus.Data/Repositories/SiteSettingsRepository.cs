using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Repositories
{
    public interface ISiteSettingsRepository
    {
        /// <summary>The single settings row (Id == SiteSettings.SingletonId), or null if not yet seeded.</summary>
        Task<SiteSettings?> GetSingletonAsync(CancellationToken cancellationToken = default);

        Task<SiteSettings> AddAndSave(SiteSettings entity, CancellationToken cancellation = default);
        Task<SiteSettings> UpdateAndSave(SiteSettings entity, CancellationToken cancellation = default);
    }

    public class SiteSettingsRepository : Repository<SiteSettings>, ISiteSettingsRepository
    {
        public SiteSettingsRepository(SuperStatusDb context) : base(context)
        {
        }

        public async Task<SiteSettings?> GetSingletonAsync(CancellationToken cancellationToken = default)
            => await DbSet.FirstOrDefaultAsync(x => x.Id == SiteSettings.SingletonId, cancellationToken);
    }
}
