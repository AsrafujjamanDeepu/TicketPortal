using TicketPortal.Api.Realtime;
using Xunit;

namespace TicketPortal.Api.Tests.Unit
{
    // REALTIME_SIGNALR_PLAN.md, principle 7 ("no listeners, no work"): the connection registry
    // that lets the change-capture code skip all work while nobody is connected.
    public class RealtimeConnectionTrackerTests
    {
        [Fact]
        public void NewTracker_HasNoListeners()
        {
            var tracker = new RealtimeConnectionTracker();

            Assert.False(tracker.HasListeners);
            Assert.Equal(0, tracker.Count);
        }

        [Fact]
        public void AddingAConnection_MakesTheTrackerLive_AndRemovingItMakesItIdleAgain()
        {
            var tracker = new RealtimeConnectionTracker();

            tracker.Add("a");
            Assert.True(tracker.HasListeners);
            Assert.True(tracker.Contains("a"));

            tracker.Remove("a");
            Assert.False(tracker.HasListeners);
            Assert.False(tracker.Contains("a"));
        }

        [Fact]
        public void TheSameConnectionReportedTwice_IsCountedOnce()
        {
            var tracker = new RealtimeConnectionTracker();

            tracker.Add("a");
            tracker.Add("a");

            Assert.Equal(1, tracker.Count);
        }

        [Fact]
        public void RemovingAConnectionThatWasNeverAdded_IsHarmless()
        {
            var tracker = new RealtimeConnectionTracker();

            tracker.Add("a");
            tracker.Remove("never-added");
            tracker.Remove("a");
            tracker.Remove("a");

            Assert.Equal(0, tracker.Count);
            Assert.False(tracker.HasListeners);
        }

        [Fact]
        public void ManyConnections_StayLiveUntilTheLastOneLeaves()
        {
            var tracker = new RealtimeConnectionTracker();
            var ids = Enumerable.Range(0, 50).Select(i => $"c{i}").ToList();

            foreach (var id in ids)
                tracker.Add(id);
            foreach (var id in ids.Take(49))
                tracker.Remove(id);

            Assert.True(tracker.HasListeners);
            Assert.Equal(1, tracker.Count);

            tracker.Remove(ids[49]);
            Assert.False(tracker.HasListeners);
        }

        [Fact]
        public async Task ConcurrentConnectsAndDisconnects_EndAtZero()
        {
            var tracker = new RealtimeConnectionTracker();

            await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() =>
            {
                var id = $"c{i}";
                tracker.Add(id);
                tracker.Remove(id);
            })));

            Assert.Equal(0, tracker.Count);
        }
    }
}
