using SkylightPhotoSync.Skylight;

namespace SkylightPhotoSync.Tests.TestSupport;

/// <summary>An in-memory Skylight account: frames, their photos, and a log of deletes and uploads.</summary>
public sealed class FakeSkylightClient : ISkylightClient
{
    private int _nextId;
    private int _nextUpload;

    public List<SkylightFrame> Frames { get; } = [new("100", "Kitchen")];

    public Dictionary<string, List<SkylightMessage>> Messages { get; } = new() { ["100"] = [] };

    public List<(string FrameId, string MessageId)> Deleted { get; } = [];

    /// <summary>Every upload that reached the account and was accepted, in order.</summary>
    public List<(IReadOnlyList<string> FrameIds, string FileName, string Mime)> Uploads { get; } = [];

    /// <summary>The bytes of every accepted upload, in order.</summary>
    public List<byte[]> UploadedFiles { get; } = [];

    /// <summary>Frames an accepted upload returns no photo id for.</summary>
    public HashSet<string> NoUploadIdFor { get; } = [];

    /// <summary>When set, uploads fail with this once <see cref="FailUploadsAfter"/> of them were accepted.</summary>
    public Exception? FailUploadsWith { get; set; }

    public int FailUploadsAfter { get; set; }

    public int Listings { get; private set; }

    /// <summary>Runs before every message listing, with the listing's 1-based number; lets a test make photos "arrive".</summary>
    public Action<int>? BeforeListing { get; set; }

    public Exception? FailDeletesWith { get; set; }

    /// <summary>When set, every call fails with this, the way a refused sign-in fails whatever is asked.</summary>
    public Exception? FailEverythingWith { get; set; }

    /// <summary>How many calls of any kind reached the account, failed ones included.</summary>
    public int Calls { get; private set; }

    public Task<IReadOnlyList<SkylightFrame>> ListFramesAsync(CancellationToken ct)
    {
        Arrive();
        return Task.FromResult<IReadOnlyList<SkylightFrame>>(Frames.ToList());
    }

    public Task<IReadOnlyList<SkylightMessage>> ListMessagesAsync(string frameId, CancellationToken ct)
    {
        Arrive();
        Listings++;
        BeforeListing?.Invoke(Listings);
        return Task.FromResult<IReadOnlyList<SkylightMessage>>(Messages.TryGetValue(frameId, out var list) ? list.ToList() : []);
    }

    public Task DeleteMessageAsync(string frameId, string messageId, CancellationToken ct)
    {
        Arrive();
        if (FailDeletesWith is not null)
        {
            throw FailDeletesWith;
        }

        Deleted.Add((frameId, messageId));
        Messages[frameId].RemoveAll(m => m.Id == messageId);
        return Task.CompletedTask;
    }

    /// <summary>What the account reports as <c>created_at</c> for an uploaded photo.</summary>
    public DateTimeOffset UploadTime { get; set; } = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>When false, an accepted upload does not show up in the frame's listing (as if Skylight were slow to list it).</summary>
    public bool ListUploads { get; set; } = true;

    /// <summary>
    /// Accepts the photo for every frame asked for and puts it on those frames, with no sender;
    /// message ids are <c>up1</c>, <c>up2</c>, … in upload order.
    /// </summary>
    public Task<IReadOnlyList<SkylightUpload>> UploadPhotoAsync(IReadOnlyList<string> frameIds, string path, string mime, CancellationToken ct)
    {
        Arrive();
        if (FailUploadsWith is not null && Uploads.Count >= FailUploadsAfter)
        {
            throw FailUploadsWith;
        }

        Uploads.Add((frameIds.ToList(), Path.GetFileName(path), mime));
        UploadedFiles.Add(File.ReadAllBytes(path));
        var uploads = frameIds.Where(frameId => !NoUploadIdFor.Contains(frameId)).Select(frameId => new SkylightUpload(frameId, $"up{++_nextUpload}")).ToList();
        if (ListUploads)
        {
            foreach (var upload in uploads)
            {
                if (!Messages.TryGetValue(upload.FrameId, out var list))
                {
                    Messages[upload.FrameId] = list = [];
                }

                list.Add(new SkylightMessage(upload.MessageId, UploadTime, FromEmail: null, Destroyed: false));
            }
        }

        return Task.FromResult<IReadOnlyList<SkylightUpload>>(uploads);
    }

    private void Arrive()
    {
        Calls++;
        if (FailEverythingWith is not null)
        {
            throw FailEverythingWith;
        }
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
