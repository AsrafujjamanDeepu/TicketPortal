using Microsoft.AspNetCore.Mvc;
using TicketPortal.Api.DTO;

namespace TicketPortal.Api.Extensions
{
    // C7-3: the already-validated paging request. `IsExplicit` is false for legacy callers that
    // sent neither page nor pageSize.
    public readonly record struct PageRequest(int Page, int PageSize, bool IsExplicit)
    {
        public int Skip => checked((Page - 1) * PageSize);
    }

    public static class Paging
    {
        public const int DefaultPageSize = 25;
        public const int MaxPageSize = 100;

        // Callers that do not ask for a page still get a plain array (the Angular customer pages
        // and counter lookups predate paging), but never an unbounded one. 200 rows is above any
        // single customer's history and above the demo data volume; the true total is always in
        // the X-Total-Count header so a client can tell when it has been truncated.
        public const int LegacyListCap = 200;

        // Backward-compatible defaults + hard caps: page < 1 becomes 1, pageSize <= 0 becomes the
        // default, and anything above MaxPageSize is clamped (never an error, never a bigger page).
        public static PageRequest Resolve(int? page, int? pageSize)
        {
            if (page is null && pageSize is null)
                return new PageRequest(1, LegacyListCap, IsExplicit: false);

            var safePage = Math.Clamp(page ?? 1, 1, 1_000_000);
            var safeSize = pageSize is null or <= 0 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
            return new PageRequest(safePage, safeSize, IsExplicit: true);
        }
    }

    public static class PagingResults
    {
        public const string TotalCountHeader = "X-Total-Count";

        // Explicit page request -> PagedResult<T> envelope. No page request -> the old plain array
        // (capped at Paging.LegacyListCap). X-Total-Count is set either way.
        public static IActionResult PagedOk<T>(this ControllerBase controller, IReadOnlyList<T> items, int totalCount, PageRequest request)
        {
            controller.Response.Headers[TotalCountHeader] = totalCount.ToString();

            if (!request.IsExplicit) return controller.Ok(items);

            var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)request.PageSize);
            return controller.Ok(new PagedResult<T>
            {
                Items = items,
                Page = request.Page,
                PageSize = request.PageSize,
                TotalCount = totalCount,
                TotalPages = totalPages,
                HasNextPage = request.Page < totalPages,
                HasPreviousPage = request.Page > 1 && totalPages > 0,
            });
        }
    }
}
