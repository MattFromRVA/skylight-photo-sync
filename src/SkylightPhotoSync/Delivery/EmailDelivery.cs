using SkylightPhotoSync.Batching;
using SkylightPhotoSync.Config;
using SkylightPhotoSync.Mail;
using SkylightPhotoSync.Models;
using SkylightPhotoSync.State;

namespace SkylightPhotoSync.Delivery;

/// <summary>
/// Emails the photos to the frame (spec §6.5): packs them into batches that fit one email, sends one
/// email per batch per recipient, and marks a batch's items sent only after the SMTP server accepted it.
/// </summary>
public sealed class EmailDelivery(EmailConfig email, SourceConfig source, IMailer mailer, StateStore store, Func<DateTimeOffset> now)
    : IPhotoDelivery
{
    public Task<IReadOnlyList<DeliveryDestination>> DestinationsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DeliveryDestination>>(email.To.Select(address => new DeliveryDestination(address, address)).ToList());

    public async Task<DeliveryResult> DeliverAsync(IReadOnlyList<(Item Item, string File)> prepared, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var batches = new List<DeliveredBatch>();
        var skipped = new List<DeliverySkip>();
        try
        {
            // Decided before anything is sent: sending to the first recipient must not change who else gets it.
            var resends = prepared
                .Where(p => email.To.All(recipient => store.WasSentTo(p.Item, recipient)))
                .Select(p => p.Item.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var recipient in email.To)
            {
                var pending = prepared.Where(p => resends.Contains(p.Item.Id) || !store.WasSentTo(p.Item, recipient)).ToList();
                var pack = BatchPacker.Pack(pending, email.MaxAttachments, email.MaxTotalBytes);
                foreach (var (item, reason) in pack.Skipped)
                {
                    var file = pending.First(p => p.Item.Id == item.Id).File;
                    skipped.Add(new DeliverySkip(
                        item, reason, $"{new FileInfo(file).Length:N0} bytes exceeds email.max_total_bytes ({email.MaxTotalBytes:N0})"));
                }

                var destination = new DeliveryDestination(recipient, recipient);
                foreach (var batch in pack.Batches)
                {
                    ct.ThrowIfCancellationRequested();
                    var message = MessageBuilder.Build(batch, recipient, email.From, source.Mode, source.AlbumName, now());
                    if (!dryRun)
                    {
                        await mailer.SendAsync(message, ct);
                        store.MarkSent(batch.Items, recipient, batch.Id, now());
                    }

                    batches.Add(new DeliveredBatch(
                        destination,
                        batch.Items.Select((item, i) => new DeliveredItem(item, new FileInfo(batch.Files[i]).Length, MessageId: null)).ToList(),
                        batch.TotalBytes));
                }
            }
        }
        catch (Exception ex)
        {
            return new DeliveryResult(batches, skipped, ex);
        }

        return new DeliveryResult(batches, skipped);
    }
}
