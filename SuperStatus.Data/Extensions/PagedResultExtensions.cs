using SuperStatus.Data.DTO;
using SuperStatus.Data.Entities;

namespace SuperStatus.Data.Extensions
{
    public static class PagedResultExtensions
    {
        /// <summary>
        /// Maps an IPagedResult of one type to an IPagedResult of another type using the provided selector function
        /// </summary>
        /// <typeparam name="TSource">The source type</typeparam>
        /// <typeparam name="TDestination">The destination type</typeparam>
        /// <param name="source">The source paged result</param>
        /// <param name="selector">Function to map from source to destination type</param>
        /// <returns>A new PagedResult with mapped items</returns>
        public static PagedResult<TDestination> MapTo<TSource, TDestination>(
            this IPagedResult<TSource> source, 
            Func<TSource, TDestination> selector)
            where TSource : class
            where TDestination : class
        {
            return new PagedResult<TDestination>
            {
                Results = source.Results.Select(selector).ToList(),
                RowCount = source.RowCount,
                PageSize = source.PageSize,
                CurrentPage = source.CurrentPage,
                PageCount = source.PageCount
            };
        }

        /// <summary>
        /// Asynchronously maps an IPagedResult of one type to an IPagedResult of another type using the provided async selector function
        /// </summary>
        /// <typeparam name="TSource">The source type</typeparam>
        /// <typeparam name="TDestination">The destination type</typeparam>
        /// <param name="source">The source paged result</param>
        /// <param name="selector">Async function to map from source to destination type</param>
        /// <returns>A task that represents the asynchronous operation, containing a new PagedResult with mapped items</returns>
        public static async Task<PagedResult<TDestination>> MapToAsync<TSource, TDestination>(
            this IPagedResult<TSource> source, 
            Func<TSource, Task<TDestination>> selector)
            where TSource : class
            where TDestination : class
        {
            var mappedResults = new List<TDestination>();
            foreach (var item in source.Results)
            {
                var mappedItem = await selector(item);
                mappedResults.Add(mappedItem);
            }

            return new PagedResult<TDestination>
            {
                Results = mappedResults,
                RowCount = source.RowCount,
                PageSize = source.PageSize,
                CurrentPage = source.CurrentPage,
                PageCount = source.PageCount
            };
        }
    }
}