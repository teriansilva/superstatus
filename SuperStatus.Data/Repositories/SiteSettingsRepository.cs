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

        /// <summary>#241 Phase B: atomically stamp <c>SmtpVerifiedUtc</c> ONLY if the
        /// row's transport fields still match the ones just tested. A single
        /// conditional UPDATE — so a config edit racing an in-flight "send test"
        /// can't mark the new (untested) relay verified. Returns true if it stamped.</summary>
        Task<bool> StampSmtpVerifiedIfTransportMatchesAsync(
            string host, int port, bool useStartTls, string username, string password, string fromAddress,
            DateTime verifiedUtc, CancellationToken cancellationToken = default);

        /// <summary>#241 Phase C: atomically set the VAPID key pair ONLY if it isn't
        /// already present. A single conditional UPDATE (compare-and-set) — so two
        /// concurrent first-use callers can't clobber each other's keys: the first wins,
        /// the rest match no rows. Returns true if THIS call wrote the pair.</summary>
        Task<bool> SetVapidKeysIfAbsentAsync(
            string publicKey, string privateKey, DateTime updatedUtc, CancellationToken cancellationToken = default);

        /// <summary>#241 Phase C: the stored VAPID public key (untracked, fresh read),
        /// or null/empty if not yet generated. Reads back the winner's key after a
        /// compare-and-set, bypassing the change-tracker's cached (possibly stale) row.</summary>
        Task<string?> GetVapidPublicKeyAsync(CancellationToken cancellationToken = default);
    }

    public class SiteSettingsRepository : Repository<SiteSettings>, ISiteSettingsRepository
    {
        public SiteSettingsRepository(SuperStatusDb context) : base(context)
        {
        }

        public async Task<SiteSettings?> GetSingletonAsync(CancellationToken cancellationToken = default)
            => await DbSet.FirstOrDefaultAsync(x => x.Id == SiteSettings.SingletonId, cancellationToken);

        public async Task<bool> StampSmtpVerifiedIfTransportMatchesAsync(
            string host, int port, bool useStartTls, string username, string password, string fromAddress,
            DateTime verifiedUtc, CancellationToken cancellationToken = default)
        {
            int affected = await DbSet
                .Where(x => x.Id == SiteSettings.SingletonId
                    && x.SmtpHost == host
                    && x.SmtpPort == port
                    && x.SmtpUseStartTls == useStartTls
                    && x.SmtpUsername == username
                    && x.SmtpPassword == password
                    && x.SmtpFromAddress == fromAddress)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.SmtpVerifiedUtc, verifiedUtc), cancellationToken);
            return affected > 0;
        }

        public async Task<bool> SetVapidKeysIfAbsentAsync(
            string publicKey, string privateKey, DateTime updatedUtc, CancellationToken cancellationToken = default)
        {
            // WHERE the pair is still unset (columns default to ""). The first concurrent
            // caller's UPDATE matches + commits; later callers re-evaluate the WHERE
            // against the committed state, match no rows, and leave the winner's keys intact.
            int affected = await DbSet
                .Where(x => x.Id == SiteSettings.SingletonId
                    && (x.VapidPublicKey == "" || x.VapidPrivateKey == ""))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.VapidPublicKey, publicKey)
                    .SetProperty(x => x.VapidPrivateKey, privateKey)
                    .SetProperty(x => x.UpdatedUtc, updatedUtc), cancellationToken);
            return affected > 0;
        }

        public async Task<string?> GetVapidPublicKeyAsync(CancellationToken cancellationToken = default)
            => await DbSet.AsNoTracking()
                .Where(x => x.Id == SiteSettings.SingletonId)
                .Select(x => x.VapidPublicKey)
                .FirstOrDefaultAsync(cancellationToken);
    }
}
