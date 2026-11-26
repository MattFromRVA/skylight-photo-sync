using SkylightPhotoSync.Config;
using SkylightPhotoSync.Delivery;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.Skylight;
using SkylightPhotoSync.State;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public sealed class FrameRotatorTests : IDisposable
{
    private const string _us = "sync@example.com";
    private const string _recipient = "skylight:frame:100";
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

    [Fact]
    public async Task Uploaded_photos_are_pruned_by_recorded_id_oldest_upload_first()
    {
        // Skylight's own timestamps say the opposite order; the recorded upload order decides.
        for (var i = 1; i <= 5; i++)
        {
            Upload($"u{i}", _t0.AddHours(-10 + i), listedAt: _t0.AddDays(-i));
        }

        var result = await Rotator(keep: 3, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(["u1", "u2"], _client.Deleted.Select(d => d.MessageId));
        Assert.Equal(["u3", "u4", "u5"], _client.Messages["100"].Select(m => m.Id));
        var frame = Assert.Single(result.Frames);
        Assert.Equal(new PruneGroup(OnFrame: 5, Deleted: 2, WouldDelete: 0), frame.Uploaded);
        Assert.Equal(new PruneGroup(0, 0, 0), frame.Emailed);
        Assert.Equal((5, 2, 0), (frame.Ours, frame.Deleted, frame.WouldDelete));
        Assert.Equal(["u3", "u4", "u5"], _store.RecordedUploads(_recipient).Select(u => u.MessageId));
        Assert.Equal(2, _store.TotalPruned());
    }

    [Fact]
    public async Task A_test_upload_is_the_first_photo_pruned_however_recently_it_was_uploaded()
    {
        Upload("u1", _t0.AddHours(-3));
        Upload("u2", _t0.AddHours(-2));
        Upload("u3", _t0.AddHours(-1));
        TestUpload("t1", _t0);

        var result = await Rotator(keep: 3, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0.AddHours(1), CancellationToken.None);

        // Without this the newest photo, the test image, would stay and the real u1 would go.
        Assert.Equal(["t1"], _client.Deleted.Select(d => d.MessageId));
        Assert.Equal(["u1", "u2", "u3"], _store.RecordedUploads(_recipient).Select(u => u.MessageId));
        Assert.Equal(new PruneGroup(OnFrame: 4, Deleted: 1, WouldDelete: 0), Assert.Single(result.Frames).Uploaded);
    }

    [Fact]
    public async Task A_test_upload_stays_while_the_frame_is_not_over_keep()
    {
        Upload("u1", _t0.AddHours(-3));
        Upload("u2", _t0.AddHours(-2));
        TestUpload("t1", _t0);

        await Rotator(keep: 3, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0.AddHours(1), CancellationToken.None);

        Assert.Empty(_client.Deleted);
        Assert.Equal(["u1", "u2", "t1"], _store.RecordedUploads(_recipient).Select(u => u.MessageId));
    }

    [Fact]
    public async Task A_test_upload_goes_before_emailed_photos_too()
    {
        _client.Add(_us, 3, _t0.AddHours(-5), idPrefix: "mail");
        TestUpload("t1", _t0);

        await Rotator(keep: 3).PruneAsync(dryRun: false, expectedNew: 0, _t0.AddHours(1), CancellationToken.None);

        Assert.Equal(["t1"], _client.Deleted.Select(d => d.MessageId));
    }

    [Fact]
    public async Task A_photo_with_no_recorded_id_is_never_deleted()
    {
        // Same account, same look as an upload, but nothing recorded it: someone added it in the app.
        Upload("u1", _t0.AddHours(-3));
        Upload("u2", _t0.AddHours(-2));
        _client.Messages["100"].Add(new SkylightMessage("app1", _t0.AddDays(-30), null, Destroyed: false));
        _client.Add("grandma@example.com", 4, _t0.AddDays(-20), idPrefix: "gma");

        var result = await Rotator(keep: 1, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(["u1"], _client.Deleted.Select(d => d.MessageId));
        var frame = Assert.Single(result.Frames);
        Assert.Equal(1, frame.OtherSenders["(no sender)"]);
        Assert.Equal(4, frame.OtherSenders["grandma@example.com"]);
    }

    [Fact]
    public async Task Without_a_sender_address_emailed_photos_are_left_alone()
    {
        _client.Add(_us, 5, _t0);

        var result = await Rotator(keep: 1, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Empty(_client.Deleted);
        Assert.Equal(5, Assert.Single(result.Frames).OtherSenders[_us]);
    }

    [Fact]
    public async Task A_recorded_id_recorded_for_another_frame_is_not_deleted_here()
    {
        _client.Frames.Add(new SkylightFrame("200", "Living Room"));
        Upload("u1", _t0.AddHours(-3), frameId: "200");
        Upload("u2", _t0.AddHours(-2), frameId: "200");
        _client.Messages["100"].Add(new SkylightMessage("u1", _t0, null, Destroyed: false));
        _client.Messages["100"].Add(new SkylightMessage("u2", _t0, null, Destroyed: false));

        await Rotator(keep: 1, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal([("200", "u1")], _client.Deleted);
    }

    [Fact]
    public async Task A_recorded_photo_someone_deleted_by_hand_is_skipped_without_a_delete_call()
    {
        for (var i = 1; i <= 4; i++)
        {
            Upload($"u{i}", _t0.AddHours(-10 + i));
        }

        _client.Messages["100"].RemoveAll(m => m.Id == "u1");
        _client.Messages["100"][0] = _client.Messages["100"][0] with { Destroyed = true };   // u2, deleted in the app

        var result = await Rotator(keep: 1, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(["u3"], _client.Deleted.Select(d => d.MessageId));
        Assert.Equal(new PruneGroup(OnFrame: 2, Deleted: 1, WouldDelete: 0), Assert.Single(result.Frames).Uploaded);
    }

    [Fact]
    public async Task Emailed_photos_still_match_on_from_email_and_keep_counts_both_groups_together()
    {
        // Emailed e1..e3 are older than uploaded u1..u3; keep 4 leaves the newest four of the six.
        _client.Add(_us, 3, _t0.AddDays(-2), idPrefix: "e");
        _client.Add("grandma@example.com", 2, _t0.AddDays(-9), idPrefix: "gma");
        for (var i = 1; i <= 3; i++)
        {
            Upload($"u{i}", _t0.AddHours(-10 + i));
        }

        var result = await Rotator(keep: 4).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(["e1", "e2"], _client.Deleted.Select(d => d.MessageId));
        var frame = Assert.Single(result.Frames);
        Assert.Equal(new PruneGroup(OnFrame: 3, Deleted: 2, WouldDelete: 0), frame.Emailed);
        Assert.Equal(new PruneGroup(OnFrame: 3, Deleted: 0, WouldDelete: 0), frame.Uploaded);
        Assert.Equal(6, frame.Ours);
        Assert.Equal(2, frame.OtherSenders["grandma@example.com"]);
    }

    [Fact]
    public async Task Dry_run_reports_both_groups_and_deletes_nothing()
    {
        _client.Add(_us, 3, _t0.AddDays(-2), idPrefix: "e");
        for (var i = 1; i <= 4; i++)
        {
            Upload($"u{i}", _t0.AddHours(-10 + i));
        }

        var result = await Rotator(keep: 2).PruneAsync(dryRun: true, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Empty(_client.Deleted);
        var frame = Assert.Single(result.Frames);
        Assert.Equal(new PruneGroup(OnFrame: 3, Deleted: 0, WouldDelete: 3), frame.Emailed);
        Assert.Equal(new PruneGroup(OnFrame: 4, Deleted: 0, WouldDelete: 2), frame.Uploaded);
        Assert.Equal(5, result.WouldDelete);
        Assert.Equal(4, _store.RecordedUploads(_recipient).Count);
    }

    [Fact]
    public async Task Waits_for_this_runs_uploads_to_be_listed_before_pruning()
    {
        Upload("u1", _t0.AddHours(-2));
        Upload("u2", _t0.AddHours(-1));
        _store.MarkSent([Photo("u3")], _recipient, "b", _t0, "u3");   // uploaded this run, not listed yet
        _client.BeforeListing = listing =>
        {
            if (listing == 3)
            {
                _client.Messages["100"].Add(new SkylightMessage("u3", _t0, null, Destroyed: false));
            }
        };

        var result = await Rotator(keep: 2, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Equal(2, _delays.Count);
        Assert.Equal(["u1"], _client.Deleted.Select(d => d.MessageId));
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task Does_not_wait_for_uploads_recorded_before_this_run()
    {
        Upload("u1", _t0.AddHours(-2));
        _store.MarkSent([Photo("gone")], _recipient, "b", _t0.AddHours(-1), "gone");   // deleted by hand since

        await Rotator(keep: 2, sender: null).PruneAsync(dryRun: false, expectedNew: 0, _t0, CancellationToken.None);

        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData("a@b.com", "a@b.com")]
    [InlineData("  A@B.com ", "a@b.com")]
    [InlineData("Name <A@B.com>", "a@b.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeEmail(string? input, string? expected) =>
        Assert.Equal(expected, FrameRotator.NormalizeEmail(input));

    private static Item Photo(string name) =>
        new($"album/{name}.jpg", $"{name}.jpg", ItemKind.Photo, _t0.AddDays(-40), 10, null, "image/jpeg");

    /// <summary>Records an upload of its own photo to the frame and puts it on the frame.</summary>
    private void Upload(string messageId, DateTimeOffset uploadedAt, DateTimeOffset? listedAt = null, string frameId = "100")
    {
        _store.MarkSent([Photo(messageId)], SkylightApiDelivery.RecipientFor(frameId), "batch", uploadedAt, messageId);
        if (!_client.Messages.TryGetValue(frameId, out var list))
        {
            _client.Messages[frameId] = list = [];
        }

        list.Add(new SkylightMessage(messageId, listedAt ?? uploadedAt, null, Destroyed: false));
    }

    /// <summary>What <c>test-upload</c> leaves behind: a recorded photo on the frame that is no sent item.</summary>
    private void TestUpload(string messageId, DateTimeOffset uploadedAt, string frameId = "100")
    {
        _store.RecordUpload(SkylightApiDelivery.RecipientFor(frameId), messageId, RecordedUpload.TestItemPrefix + messageId, uploadedAt);
        _client.Messages[frameId].Add(new SkylightMessage(messageId, uploadedAt, null, Destroyed: false));
    }

    private FrameRotator Rotator(int keep, int maxDeletes = 100, string? frameName = null, int waitMinutes = 10, string? sender = _us) =>
        new(
            new RotationConfig(Enabled: true, keep, maxDeletes, frameName, waitMinutes),
            sender,
            _client,
            _store,
            () => _t0,
            (delay, _) =>
            {
                _delays.Add(delay);
                return Task.CompletedTask;
            });
}
