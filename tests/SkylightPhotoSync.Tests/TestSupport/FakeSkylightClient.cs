using SkylightPhotoSync.Skylight;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>An in-memory Skylight account: frames, their photos, and a log of deletes.</summary>
public sealed class FakeSkylightClient : ISkylightClient
{
    private int _nextId;

    public List<SkylightFrame> Frames { get; } = [new("100", "Kitchen")];

    public Dictionary<string, List<SkylightMessage>> Messages { get; } = new() { ["100"] = [] };

    public List<(string FrameId, string MessageId)> Deleted { get; } = [];

    public int Listings { get; private set; }

    /// <summary>Runs before every message listing, with the listing's 1-based number; lets a test make photos "arrive".</summary>
    public Action<int>? BeforeListing { get; set; }

    public Exception? FailDeletesWith { get; set; }

    public Task<IReadOnlyList<SkylightFrame>> ListFramesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkylightFrame>>(Frames.ToList());

    public Task<IReadOnlyList<SkylightMessage>> ListMessagesAsync(string frameId, CancellationToken ct)
    {
        Listings++;
        BeforeListing?.Invoke(Listings);
        return Task.FromResult<IReadOnlyList<SkylightMessage>>(Messages.TryGetValue(frameId, out var list) ? list.ToList() : []);
    }

    public Task DeleteMessageAsync(string frameId, string messageId, CancellationToken ct)
    {
        if (FailDeletesWith is not null)
        {
            throw FailDeletesWith;
        }

        Deleted.Add((frameId, messageId));
        Messages[frameId].RemoveAll(m => m.Id == messageId);
        return Task.CompletedTask;
    }

    /// <summary>Adds <paramref name="count"/> photos from <paramref name="from"/>, one minute apart, ending at <paramref name="newest"/>.</summary>
    public FakeSkylightClient Add(string from, int count, DateTimeOffset newest, string frameId = "100", string idPrefix = "m")
    {
        if (!Messages.TryGetValue(frameId, out var list))
        {
            Messages[frameId] = list = [];
        }

        for (var i = 0; i < count; i++)
        {
            list.Add(new SkylightMessage($"{idPrefix}{++_nextId}", newest.AddMinutes(-(count - 1 - i)), from, Destroyed: false));
        }

        return this;
    }
}
