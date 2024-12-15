using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Reflection;
using static System.Formats.Asn1.AsnWriter;

namespace SuperStatus.Data.Repositories
{
    public interface IBaseRepository
    {

        Task BeginTransaction();
        Task CommitTransaction();
        Task RollbackTransaction();

        void Add(object model);
        Task AddAsync(object model);
        Task AddRangeAsync(IEnumerable<object> model);
        void Delete(object model);
        void DeleteRange(IEnumerable<object> model);
        void Update(object model);

        bool SaveAll();
        Task<bool> SaveAllAsync();
    }

    public class BaseRepository : IBaseRepository
    {
        protected readonly IDbContextFactory<SuperStatusContext> dbContextFactory;
        protected readonly SuperStatusContext context;
        private IDbContextTransaction transaction;
        protected readonly ILogger<BaseRepository> logger;

        public BaseRepository(IDbContextFactory<SuperStatusContext> dbContextFactory, ILogger<BaseRepository> logger)
        {
            this.dbContextFactory = dbContextFactory;
            this.context = dbContextFactory.CreateDbContext();
            this.logger = logger;
        }

        public void Add(object model)
        {
            context.Add(model);
        }


        public async Task AddAsync(object model)
        {
            await context.AddAsync(model);
        }

        public async Task AddRangeAsync(IEnumerable<object> model)
        {
            await context.AddRangeAsync(model);
        }

        public void Delete(object model)
        {
            context.Remove(model);
        }

        public void DeleteRange(IEnumerable<object> model)
        {
            context.RemoveRange(model);
        }

        public void Update(object model)
        {
            context.Update(model);
        }

        public async Task<bool> SaveAllAsync()
        {
            try
            {
                return await context.SaveChangesAsync() > 0;
            }
            catch (Exception ex)
            {
                logger.LogInformation($"Failed to execute {MethodBase.GetCurrentMethod().Name}: {ex.Message}");
                throw;
            }

        }

        public bool SaveAll()
        {
            try
            {
                return context.SaveChanges() > 0;
            }
            catch (Exception ex)
            {
                logger.LogInformation($"Failed to execute {MethodBase.GetCurrentMethod().Name}: {ex.Message}");
                throw;
            }

        }

        #region TransactionHandling

        public async Task BeginTransaction()
        {
            transaction = await context.Database.BeginTransactionAsync();
        }

        public async Task CommitTransaction()
        {
            await transaction.CommitAsync();
        }

        public async Task RollbackTransaction()
        {
            await transaction.RollbackAsync();
        }
        #endregion

    }
}
