using Microsoft.Extensions.Configuration;
using TicketPortal.Api.Services;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // Chunk 6 / C6-2 + C6-4 — the validated settings. Each one is read per request AND checked once
    // at startup (Program.cs), so a typo stops the API from starting instead of silently turning a
    // protection off. These tests pin down what "valid" means.
    public class Chunk6SettingsTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
                .Build();

        // ---------------------------------------------------------------- hold limits

        [Fact]
        public void HoldLimits_DefaultToSixSeatsAndThreeActiveHolds()
        {
            var limits = SeatHoldLimits.FromConfiguration(Config());

            Assert.Equal(6, limits.MaxSeatsPerHold);
            Assert.Equal(3, limits.MaxActiveHoldsPerUser);
        }

        [Fact]
        public void HoldLimits_AreReadFromConfiguration()
        {
            var limits = SeatHoldLimits.FromConfiguration(Config(
                (SeatHoldLimits.MaxSeatsPerHoldKey, "4"),
                (SeatHoldLimits.MaxActiveHoldsPerUserKey, "10")));

            Assert.Equal(4, limits.MaxSeatsPerHold);
            Assert.Equal(10, limits.MaxActiveHoldsPerUser);
        }

        [Theory]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "0")]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "-3")]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "abc")]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "2.5")]
        [InlineData(SeatHoldLimits.MaxSeatsPerHoldKey, "61")]
        [InlineData(SeatHoldLimits.MaxActiveHoldsPerUserKey, "0")]
        [InlineData(SeatHoldLimits.MaxActiveHoldsPerUserKey, "nope")]
        [InlineData(SeatHoldLimits.MaxActiveHoldsPerUserKey, "1001")]
        public void InvalidHoldLimits_AreRejected_NamingTheSetting(string key, string value)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SeatHoldLimits.FromConfiguration(Config((key, value))));

            Assert.Contains(key, ex.Message);
        }

        [Fact]
        public void CheckRequest_FlagsARepeatedSeat_BeforeAnythingElse()
        {
            var seat = Guid.NewGuid();
            var problem = SeatHoldLimits.CheckRequest(new[] { seat, Guid.NewGuid(), seat }, new SeatHoldLimits(6, 3));

            Assert.NotNull(problem);
            Assert.Equal(SeatHoldLimitKind.DuplicateSeats, problem!.Kind);

            // Even with no seat cap (the demo-data seeder), a repeated seat is still wrong.
            Assert.Equal(SeatHoldLimitKind.DuplicateSeats, SeatHoldLimits.CheckRequest(new[] { seat, seat }, null)!.Kind);
        }

        [Fact]
        public void CheckRequest_EnforcesTheSeatCap_ExactlyAtTheLimit()
        {
            var limits = new SeatHoldLimits(MaxSeatsPerHold: 2, MaxActiveHoldsPerUser: 3);

            Assert.Null(SeatHoldLimits.CheckRequest(new[] { Guid.NewGuid(), Guid.NewGuid() }, limits));

            var over = SeatHoldLimits.CheckRequest(new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }, limits);
            Assert.NotNull(over);
            Assert.Equal(SeatHoldLimitKind.TooManySeats, over!.Kind);
            Assert.Contains("at most 2", over.Message);
        }

        [Fact]
        public void CheckRequest_WithoutLimits_AllowsAnyNumberOfDistinctSeats()
        {
            var many = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();
            Assert.Null(SeatHoldLimits.CheckRequest(many, null));
        }

        // ---------------------------------------------------------------- availability policy (D7)

        [Fact]
        public void AvailabilityFailureMode_DefaultsToClosed()
        {
            Assert.Equal(AvailabilityFailureMode.Closed, ExternalAvailabilityPolicy.GetFailureMode(Config()));
        }

        [Theory]
        [InlineData("Closed", AvailabilityFailureMode.Closed)]
        [InlineData("closed", AvailabilityFailureMode.Closed)]
        [InlineData("Open", AvailabilityFailureMode.Open)]
        [InlineData("OPEN", AvailabilityFailureMode.Open)]
        [InlineData("  open  ", AvailabilityFailureMode.Open)]
        public void AvailabilityFailureMode_IsReadCaseInsensitively(string value, AvailabilityFailureMode expected)
        {
            var config = Config((ExternalAvailabilityPolicy.FailureModeKey, value));
            Assert.Equal(expected, ExternalAvailabilityPolicy.GetFailureMode(config));
        }

        [Theory]
        [InlineData("maybe")]
        [InlineData("1")]          // an enum's numeric value must NOT be accepted
        [InlineData("fail-open")]
        public void AnUnknownFailureMode_IsRejected_RatherThanSilentlyPickingOne(string value)
        {
            var config = Config((ExternalAvailabilityPolicy.FailureModeKey, value));
            var ex = Assert.Throws<InvalidOperationException>(() => ExternalAvailabilityPolicy.GetFailureMode(config));

            Assert.Contains(ExternalAvailabilityPolicy.FailureModeKey, ex.Message);
        }

        [Fact]
        public void AvailabilityCache_DefaultsToThirtySeconds_AndZeroTurnsItOff()
        {
            Assert.Equal(TimeSpan.FromSeconds(30), ExternalAvailabilityPolicy.GetCacheTtl(Config()));
            Assert.Equal(TimeSpan.Zero, ExternalAvailabilityPolicy.GetCacheTtl(Config((ExternalAvailabilityPolicy.CacheSecondsKey, "0"))));
            Assert.Equal(TimeSpan.FromSeconds(300), ExternalAvailabilityPolicy.GetCacheTtl(Config((ExternalAvailabilityPolicy.CacheSecondsKey, "300"))));
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("301")]
        [InlineData("soon")]
        [InlineData("1.5")]
        public void AnInvalidCacheLifetime_IsRejected(string value)
        {
            var config = Config((ExternalAvailabilityPolicy.CacheSecondsKey, value));
            Assert.Throws<InvalidOperationException>(() => ExternalAvailabilityPolicy.GetCacheTtl(config));
        }
    }
}
