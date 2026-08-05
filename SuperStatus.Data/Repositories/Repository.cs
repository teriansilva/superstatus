using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Exceptions;

namespace SuperStatus.Data.Repositories;

/// <inheritdoc />
public class Repository<T>(SuperStatusDb context) : IRepository<T>
    where T : class
{
    protected readonly SuperStatusDb Context = context;
    protected readonly DbSet<T> DbSet = context.Set<T>();

    /// <inheritdoc />
    public T? GetById(string id)
    {
        return DbSet.Find(id);
    }

    /// <inheritdoc />
    public virtual async Task<T?> GetByIdAsync(int id)
    {
        return await DbSet.FindAsync(id);
    }

    /// <inheritdoc />
    public virtual async Task<T?> GetByIdAsync(string id)
    {
        return await DbSet.FindAsync(id);
    }

    /// <inheritdoc />
    public async Task<T> GetByIdAndThrowIfNotFound(int id)
    {
        T? entity = await GetByIdAsync(id);
        return entity ?? throw new ItemNotFoundException(typeof(T).Name, id);
    }

    /// <inheritdoc />
    public virtual async Task<ICollection<T>> GetMany(CancellationToken cancellation = default)
    {
        return await DbSet.ToListAsync(cancellation);
    }

    /// <inheritdoc />
    public IQueryable<T> Query()
    {
        return DbSet.AsQueryable();
    }

    /// <inheritdoc />
    public async Task<bool> Any(Expression<Func<T, bool>> predicate, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return await DbSet.AnyAsync(predicate, cancellation);
    }

    /// <inheritdoc />
    public async Task<T?> FirstOrDefault(Expression<Func<T, bool>> predicate, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return await DbSet.FirstOrDefaultAsync(predicate, cancellation);
    }

    /// <inheritdoc />
    public async Task<T> Add(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await DbSet.AddAsync(entity);

        return entity;
    }

    /// <inheritdoc />
    public async Task<T> AddAndSave(T entity, CancellationToken cancellation = default)
    {
        T addedEntity = await Add(entity);
        await SaveChangesAsync(cancellation);

        return addedEntity;
    }

    /// <inheritdoc />
    public T Update(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        DbSet.Update(entity);
        return entity;
    }

    /// <inheritdoc />
    public async Task<T> UpdateAndSave(T entity, CancellationToken cancellation = default)
    {
        T updatedEntity = Update(entity);
        await SaveChangesAsync(cancellation);

        return updatedEntity;
    }

    /// <inheritdoc />
    public async Task Delete(int id)
    {
        T? entity = await GetByIdAsync(id);
        if (entity != null)
        {
            DbSet.Remove(entity);
        }
    }

    /// <inheritdoc />
    public void Delete(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        DbSet.Remove(entity);
    }

    /// <inheritdoc />
    public async Task DeleteAndSave(int id, CancellationToken cancellation = default)
    {
        await Delete(id);
        await SaveChangesAsync(cancellation);
    }

    /// <inheritdoc />
    public async Task DeleteAndSave(T entity, CancellationToken cancellation = default)
    {
        Delete(entity);
        await SaveChangesAsync(cancellation);
    }

    /// <inheritdoc />
    public void DeleteMany(ICollection<T> itemsToDelete)
    {
        ArgumentNullException.ThrowIfNull(itemsToDelete);

        DbSet.RemoveRange(itemsToDelete);
    }

    /// <inheritdoc />
    public async Task DeleteManyAndSave(ICollection<T> itemsToDelete, CancellationToken cancellation = default)
    {
        DeleteMany(itemsToDelete);
        await SaveChangesAsync(cancellation);
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellation = default)
    {
        return Context.SaveChangesAsync(cancellation);
    }
}