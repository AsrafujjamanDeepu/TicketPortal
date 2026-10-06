using Microsoft.AspNetCore.Mvc;
using TicketPortal.Api.DTO;
using TicketPortal.Api.Extensions;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // C7-3: the shared page request/response rules (no database needed).
    public class PagingTests
    {
        [Fact]
        public void NoPagingParameters_KeepTheLegacyBehaviour_ACappedPlainList()
        {
            var request = Paging.Resolve(null, null);
            Assert.False(request.IsExplicit);
            Assert.Equal(1, request.Page);
            Assert.Equal(Paging.LegacyListCap, request.PageSize);
            Assert.Equal(0, request.Skip);
        }

        [Theory]
        [InlineData(null, 7, 1, 7)]
        [InlineData(3, null, 3, Paging.DefaultPageSize)]
        [InlineData(0, 10, 1, 10)]
        [InlineData(-5, 10, 1, 10)]
        [InlineData(2, 0, 2, Paging.DefaultPageSize)]
        [InlineData(2, -1, 2, Paging.DefaultPageSize)]
        [InlineData(2, 100, 2, 100)]
        [InlineData(2, 101, 2, Paging.MaxPageSize)]
        [InlineData(2, int.MaxValue, 2, Paging.MaxPageSize)]
        [InlineData(int.MaxValue, 50, 1_000_000, 50)]
        public void ExplicitRequests_AreNormalisedAndCapped_NeverRejectedNeverBigger(int? page, int? size, int expectedPage, int expectedSize)
        {
            var request = Paging.Resolve(page, size);
            Assert.True(request.IsExplicit);
            Assert.Equal(expectedPage, request.Page);
            Assert.Equal(expectedSize, request.PageSize);
            Assert.True(request.Skip >= 0); // the largest page * the largest size cannot overflow
        }

        private sealed class Host : ControllerBase { }

        private static Host NewHost() => new() { ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };

        [Fact]
        public void PagedOk_WithAnExplicitRequest_ReturnsTheEnvelope_AndTheTotalHeader()
        {
            var host = NewHost();
            var result = host.PagedOk<int>([1, 2, 3], 23, Paging.Resolve(2, 10));

            var envelope = Assert.IsType<PagedResult<int>>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(23, envelope.TotalCount);
            Assert.Equal(3, envelope.TotalPages);
            Assert.True(envelope.HasNextPage);
            Assert.True(envelope.HasPreviousPage);
            Assert.Equal("23", host.Response.Headers[PagingResults.TotalCountHeader].ToString());
        }

        [Fact]
        public void PagedOk_OnTheLastAndOnAnEmptyPage_HasNoNextPage()
        {
            var last = Assert.IsType<PagedResult<int>>(Assert.IsType<OkObjectResult>(NewHost().PagedOk<int>([1], 21, Paging.Resolve(3, 10))).Value);
            Assert.False(last.HasNextPage);

            var empty = Assert.IsType<PagedResult<int>>(Assert.IsType<OkObjectResult>(NewHost().PagedOk<int>([], 0, Paging.Resolve(1, 10))).Value);
            Assert.Equal(0, empty.TotalPages);
            Assert.False(empty.HasNextPage);
            Assert.False(empty.HasPreviousPage);
        }

        [Fact]
        public void PagedOk_WithoutPagingParameters_ReturnsThePlainArray_WithTheTotalHeader()
        {
            var host = NewHost();
            var result = host.PagedOk<int>([1, 2], 500, Paging.Resolve(null, null));

            Assert.IsAssignableFrom<IReadOnlyList<int>>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal("500", host.Response.Headers[PagingResults.TotalCountHeader].ToString());
        }
    }
}
