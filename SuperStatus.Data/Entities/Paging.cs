using Microsoft.EntityFrameworkCore;

namespace SuperStatus.Data.Entities
{
    public static class Paging
    {
        /// <summary>
        /// Returns paged results
        /// </summary>
        /// <typeparam name="T">The entity type</typeparam>
        /// <param name="query">The LINQ query</param>
        /// <param name="page">The page to return</param>
        /// <param name="pageSize">The size of the page. If 0, returns all results on page 1</param>
        /// <returns></returns>
        public static async Task<PagedResult<T>> GetPagedAsync<T>(this IQueryable<T> query,
            int page, int pageSize) where T : class
        {
            var result = new PagedResult<T>();
            result.CurrentPage = page;
            result.PageSize = pageSize;
            result.RowCount = await query.CountAsync();

            //return all results unpaged when page size is smaller than 1
            if (pageSize < 1)
            {
                result.Results = await query.ToListAsync();
            }
            else
            {
                var pageCount = (double)result.RowCount / pageSize;
                result.PageCount = (int)Math.Ceiling(pageCount);
                var skip = (page - 1) * pageSize;
                result.Results = await query.Skip(skip).Take(pageSize).ToListAsync();
            }

            return result;
        }
    }

    /// <summary>
    /// base class used for paged data where type of data doesn't matter
    /// </summary>
    public abstract class PagedResultBase
    {
        public int CurrentPage { get; set; }
        public int PageCount { get; set; }
        public int PageSize { get; set; }
        public int RowCount { get; set; }

        public int FirstRowOnPage => (CurrentPage - 1) * PageSize + 1;

        public int LastRowOnPage => Math.Min(CurrentPage * PageSize, RowCount);
    }

    public interface IPagedResult<T> where T : class
    {
        IList<T> Results { get; set; }
        int CurrentPage { get; set; }
        int PageCount { get; set; }
        int PageSize { get; set; }
        int RowCount { get; set; }
        int FirstRowOnPage { get; }
        int LastRowOnPage { get; }
    }

    /// <summary>
    /// generic class for paged results
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    public class PagedResult<T> : PagedResultBase, IPagedResult<T> where T : class
    {
        public IList<T> Results { get; set; }

        public PagedResult()
        {
            Results = new List<T>();
        }
    }

}
