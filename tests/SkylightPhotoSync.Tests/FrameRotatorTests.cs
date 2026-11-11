using SkylightPhotoSync.Config;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class FrameRotatorTests : IDisposable
{
    private const string _us = "sync@example.com";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly StateStore _store;
    private readonly FakeSkylightClient _client = new();
    private readonly List<TimeSpan> _delays = [];

    public FrameRotatorTests()
    {
        _store = new StateStore(Path.Combine(_dir.Path, "state.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Keeps_newest_of_ours_and_deletes_the_rest_oldest_first()
    {
        _client.Add(_us, 5, _t0.AddHours(-2), idPrefix: "old").Add(_us, 3, _t0, idPrefix: "new");

        var result = await Rotator(keep: 3).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(["old1", "old2", "old3", "old4", "old5"], _client.Deleted.Select(d => d.MessageId));
        Assert.Equal(["new6", "new7", "new8"], _client.Messages["100"].Select(m => m.Id));
        var frame = Assert.Single(result.Frames);
        Assert.Equal((8, 5, 0), (frame.Ours, frame.Deleted, frame.WouldDelete));
        Assert.Equal(5, _store.TotalPruned());
    }

    [Fact]
    public async Task Never_touches_other_senders_and_matches_our_address_loosely()
    {
        _client.Add("Grandma@Example.com", 10, _t0.AddDays(-5), idPrefix: "gma")
            .Add("Sync Bot <SYNC@example.com>", 2, _t0.AddHours(-1), idPrefix: "us")
            .Add(_us, 1, _t0, idPrefix: "us");
        _client.Messages["100"].Add(new SkylightMessage("app1", _t0.AddDays(-9), null, Destroyed: false));

        var result = await Rotator(keep: 1).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(["us11", "us12"], _client.Deleted.Select(d => d.MessageId));
        var frame = Assert.Single(result.Frames);
        Assert.Equal(10, frame.OtherSenders["grandma@example.com"]);
        Assert.Equal(1, frame.OtherSenders["(no sender)"]);
    }

    [Fact]
    public async Task Ignores_photos_already_marked_destroyed()
    {
        _client.Messages["100"].Add(new SkylightMessage("gone", _t0.AddHours(-3), _us, Destroyed: true));
        _client.Add(_us, 2, _t0);

        await Rotator(keep: 2).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Empty(_client.Deleted);
    }

    [Fact]
    public async Task Dry_run_deletes_nothing_and_reports_what_it_would_delete()
    {
        _client.Add(_us, 40, _t0);

        var result = await Rotator(keep: 30).PruneAsync(dryRun: true, expectedNew: 30, _t0, CancellationToken.None);

        Assert.Empty(_client.Deleted);
        Assert.Equal(10, result.WouldDelete);
        Assert.Empty(_delays);
        Assert.Equal(0, _store.TotalPruned());
    }

    [Fact]
    public async Task Safety_brake_caps_deletes_per_run()
    {
        _client.Add(_us, 230, _t0);

        var result = await Rotator(keep: 30, maxDeletes: 100).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(100, result.Deleted);
        Assert.Equal("m1", _client.Deleted[0].MessageId);
        Assert.Equal(130, _client.Messages["100"].Count);
    }

    [Fact]
    public async Task Waits_for_the_new_photos_before_pruning()
    {
        _client.Add(_us, 30, _t0.AddHours(-1), idPrefix: "old");
        _client.BeforeListing = listing =>
        {
            if (listing == 3)
            {
                _client.Add(_us, 30, _t0.AddMinutes(30), idPrefix: "new");
            }
        };

        var result = await Rotator(keep: 30).PruneAsync(dryRun: false, expectedNew: 30, _t0, CancellationToken.None);

        Assert.Equal(2, _delays.Count);
        Assert.All(_delays, d => Assert.Equal(FrameRotator.PollInterval, d));
        Assert.Equal(30, result.Deleted);
        Assert.All(_client.Messages["100"], m => Assert.StartsWith("new", m.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waits_for_the_new_photos_on_every_frame_not_a_total_across_frames()
    {
        // The same 30 photos go to both frames. Listing calls run frame 100 then frame 200, so
        // calls 3-4 are the first poll and 5-6 the second.
        _client.Frames.Add(new SkylightFrame("200", "Living Room"));
        _client.Add(_us, 30, _t0.AddHours(-1), idPrefix: "old").Add(_us, 30, _t0.AddHours(-1), frameId: "200", idPrefix: "old");
        _client.BeforeListing = listing =>
        {
            if (listing == 3)
            {
                _client.Add(_us, 30, _t0.AddMinutes(30), idPrefix: "new");
            }
            else if (listing == 6)
            {
                _client.Add(_us, 30, _t0.AddMinutes(30), frameId: "200", idPrefix: "new");
            }
        };

        var result = await Rotator(keep: 30).PruneAsync(dryRun: false, expectedNew: 30, _t0, CancellationToken.None);

        Assert.Equal(2, _delays.Count);
        Assert.Equal(60, result.Deleted);
        Assert.All(_client.Messages["100"], m => Assert.StartsWith("new", m.Id, StringComparison.Ordinal));
        Assert.All(_client.Messages["200"], m => Assert.StartsWith("new", m.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gives_up_waiting_and_prunes_anyway()
    {
        _client.Add(_us, 35, _t0.AddHours(-1));

        var result = await Rotator(keep: 30, waitMinutes: 2).PruneAsync(dryRun: false, expectedNew: 30, _t0, CancellationToken.None);

        Assert.Equal(4, _delays.Count);   // 2 minutes / 30 s
        Assert.Equal(5, result.Deleted);
    }

    [Fact]
    public async Task Frame_name_limits_rotation_to_that_frame()
    {
        _client.Frames.Add(new SkylightFrame("200", "Living Room"));
        _client.Add(_us, 3, _t0).Add(_us, 3, _t0, frameId: "200");

        var result = await Rotator(keep: 1, frameName: "living room").PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal("Living Room", Assert.Single(result.Frames).FrameName);
        Assert.All(_client.Deleted, d => Assert.Equal("200", d.FrameId));
        Assert.Equal(3, _client.Messages["100"].Count);
    }

    [Fact]
    public async Task Without_frame_name_every_frame_is_rotated()
    {
        _client.Frames.Add(new SkylightFrame("200", "Living Room"));
        _client.Add(_us, 3, _t0).Add(_us, 3, _t0, frameId: "200");

        var result = await Rotator(keep: 1).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(4, result.Deleted);
    }

    [Fact]
    public async Task Unknown_frame_name_names_the_frames_there_are()
    {
        var ex = await Assert.ThrowsAsync<SkylightApiException>(
            () => Rotator(keep: 1, frameName: "Garage").PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None));

        Assert.Contains("\"Kitchen\"", ex.Message);
    }

    [Theory]
    [InlineData("a@b.com", "a@b.com")]
    [InlineData("  A@B.com ", "a@b.com")]
    [InlineData("Name <A@B.com>", "a@b.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeEmail(string? input, string? expected) =>
        Assert.Equal(expected, FrameRotator.NormalizeEmail(input));

    private FrameRotator Rotator(int keep, int maxDeletes = 100, string? frameName = null, int waitMinutes = 10) =>
        new(
            new RotationConfig(Enabled: true, keep, maxDeletes, frameName, waitMinutes),
            _us,
            _client,
            _store,
            () => _t0,
            (delay, _) =>
            {
                _delays.Add(delay);
                return Task.CompletedTask;
            });
}
