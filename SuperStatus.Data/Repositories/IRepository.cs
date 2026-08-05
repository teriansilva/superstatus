using System.Linq.Expressions;

namespace SuperStatus.Data.Repositories;

/// <summary>
/// Repository Interface.
/// </summary>
/// <typeparam name="T">The type of entity to be used in the repository.</typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>
    /// Get an entity by its id.
    /// </summary>
    /// <param name="id">Id of the entity to be retrieved.</param>
    /// <returns>Entity with the given id.</returns>
    T? GetById(string id);

    /// <summary>
    /// Get an entity by its id.
    /// </summary>
    /// <param name="id">Id of the entity to be retrieved.</param>
    /// <returns>Entity with the given id.</returns>
    Task<T?> GetByIdAsync(int id);

    /// <summary>
    /// Get an entity by its id.
    /// </summary>
    /// <param name="id">Id of the entity to be retrieved.</param>
    /// <returns>Entity with the given id.</returns>
    Task<T?> GetByIdAsync(string id);

    /// <summary>
    /// Get an entity by its id and throw an exception if not found.
    /// </summary>
    /// <param name="id">Id of the entity to be retrieved.</param>
    /// <returns>Entity with the given id.</returns>
    Task<T> GetByIdAndThrowIfNotFound(int id);

    /// <summary>
    /// Get all entities of type T.
    /// </summary>
    /// <returns>All entities of type T.</returns>
    Task<ICollection<T>> GetMany(CancellationToken cancellation = default);

    /// <summary>
    /// Gives queryable access to the entities of type T.
    /// </summary>
    /// <returns>Query to access entities of type T.</returns>
    IQueryable<T> Query();

    /// <summary>
    /// Check if any entity satisfies the given predicate.
    /// </summary>
    /// <param name="predicate">Predicate to be checked.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    /// <returns>True if any entity satisfies the predicate, false otherwise.</returns>
    Task<bool> Any(Expression<Func<T, bool>> predicate, CancellationToken cancellation = default);

    /// <summary>
    /// Get the first entity that satisfies the given predicate.
    /// </summary>
    /// <param name="predicate">Predicate to be checked.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    /// <returns>First entity that satisfies the predicate.</returns>
    Task<T?> FirstOrDefault(Expression<Func<T, bool>> predicate, CancellationToken cancellation = default);

    /// <summary>
    /// Add an entity to the repository.
    /// </summary>
    /// <param name="entity">Entity to be added.</param>
    /// <returns>Returns the added entity.</returns>
    Task<T> Add(T entity);

    /// <summary>
    /// Adds an entity and saves changes.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    /// <returns>The added entity.</returns>
    Task<T> AddAndSave(T entity, CancellationToken cancellation = default);

    /// <summary>
    /// Update an entity in the repository.
    /// </summary>
    /// <param name="entity">Entity to be updated.</param>
    /// <returns>Returns the updated entity.</returns>
    T Update(T entity);

    /// <summary>
    /// Updates an entity and saves changes.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    /// <returns>The updated entity.</returns>
    Task<T> UpdateAndSave(T entity, CancellationToken cancellation = default);

    /// <summary>
    /// Delete an entity from the repository.
    /// </summary>
    /// <param name="id">Id of the entity to be deleted.</param>
    Task Delete(int id);

    /// <summary>
    /// Delete an entity from the repository.
    /// </summary>
    /// <param name="entity">Entity to be deleted.</param>
    void Delete(T entity);

    /// <summary>
    /// Deletes an entity by its ID and saves changes.
    /// </summary>
    /// <param name="id">The ID of the entity.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    Task DeleteAndSave(int id, CancellationToken cancellation = default);

    /// <summary>
    /// Deletes an entity and saves changes.
    /// </summary>
    /// <param name="entity">The entity to delete.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    Task DeleteAndSave(T entity, CancellationToken cancellation = default);

    /// <summary>
    /// Delete many entities from the repository.
    /// </summary>
    /// <param name="itemsToDelete">Entities to be deleted.</param>
    void DeleteMany(ICollection<T> itemsToDelete);

    /// <summary>
    /// Delete many entities from the repository and save changes.
    /// </summary>
    /// <param name="itemsToDelete">Entities to be deleted.</param>
    /// <param name="cancellation">Cancellation token to cancel the operation.</param>
    Task DeleteManyAndSave(ICollection<T> itemsToDelete, CancellationToken cancellation = default);

    /// <summary>
    /// Save changes to the repository.
    /// </summary>
    /// <returns>No. of changes saved.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellation = default);
}