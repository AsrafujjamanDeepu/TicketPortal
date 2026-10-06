namespace TicketPortal.Api.DTO
{
    // C7-3: the one page envelope shared by every paged list endpoint (Bookings, Tickets).
    // Returned only when the caller explicitly asks for a page (?page= and/or ?pageSize=);
    // callers that send neither keep receiving a plain JSON array - see PagingResults.
    public sealed class PagedResult<T>
    {
        public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
        public int Page { get; init; }
        public int PageSize { get; init; }
        public int TotalCount { get; init; }
        public int TotalPages { get; init; }
        public bool HasNextPage { get; init; }
        public bool HasPreviousPage { get; init; }
    }
}
