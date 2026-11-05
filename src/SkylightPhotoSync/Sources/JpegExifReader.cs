using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SkylightPhotoSync.Sources;

/// <summary>
/// Reads EXIF <c>DateTimeOriginal</c> from a JPEG's APP1 segment without an imaging library.
/// Anything malformed yields <see langword="null"/>; it never throws on bad input.
/// </summary>
internal static class JpegExifReader
{
    private const int _tagExifIfdPointer = 0x8769;
    private const int _tagDateTimeOriginal = 0x9003;
    private const int _typeAscii = 2;
    private const string _dateFormat = "yyyy:MM:dd HH:mm:ss";

    public static DateTime? ReadDateTimeOriginal(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9)
            {
                break;   // start of scan or end of image: no more metadata segments
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2, 2));
            if (length < 2 || pos + 2 + length > jpeg.Length)
            {
                return null;
            }

            var payload = jpeg.Slice(pos + 4, length - 2);
            if (marker == 0xE1 && payload.StartsWith("Exif\0\0"u8))
            {
                return ReadFromTiff(payload[6..]);
            }

            pos += 2 + length;
        }

        return null;
    }

    private static DateTime? ReadFromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        bool bigEndian;
        if (tiff.StartsWith("MM"u8))
        {
            bigEndian = true;
        }
        else if (tiff.StartsWith("II"u8))
        {
            bigEndian = false;
        }
        else
        {
            return null;
        }

        if (ReadU16(tiff, 2, bigEndian) != 42)
        {
            return null;
        }

        var ifd0 = ReadU32(tiff, 4, bigEndian);
        var exifIfd = FindEntry(tiff, ifd0, _tagExifIfdPointer, bigEndian);
        if (exifIfd is null)
        {
            return null;
        }

        var dateEntry = FindEntry(tiff, ReadU32(tiff, exifIfd.Value, bigEndian), _tagDateTimeOriginal, bigEndian);
        if (dateEntry is null || ReadU16(tiff, dateEntry.Value - 6, bigEndian) != _typeAscii)
        {
            return null;
        }

        var count = ReadU32(tiff, dateEntry.Value - 4, bigEndian);
        var offset = count <= 4 ? dateEntry.Value : ReadU32(tiff, dateEntry.Value, bigEndian);
        if (count == 0 || offset > int.MaxValue || count > int.MaxValue || offset + count > (uint)tiff.Length)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(tiff.Slice((int)offset, (int)count)).TrimEnd('\0');
        return DateTime.TryParseExact(text, _dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Returns the offset of the entry's 4-byte value field, or null when the tag is absent.</summary>
    private static uint? FindEntry(ReadOnlySpan<byte> tiff, uint ifdOffset, int tag, bool bigEndian)
    {
        if (ifdOffset > int.MaxValue || ifdOffset + 2 > (uint)tiff.Length)
        {
            return null;
        }

        var count = ReadU16(tiff, ifdOffset, bigEndian);
        for (uint i = 0; i < count; i++)
        {
            var entry = ifdOffset + 2 + 12 * i;
            if (entry + 12 > (uint)tiff.Length)
            {
                return null;
            }

            if (ReadU16(tiff, entry, bigEndian) == tag)
            {
                return entry + 8;
            }
        }

        return null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, uint offset, bool bigEndian)
    {
        var slice = data.Slice((int)offset, 2);
        return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(slice) : BinaryPrimitives.ReadUInt16LittleEndian(slice);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, uint offset, bool bigEndian)
    {
        var slice = data.Slice((int)offset, 4);
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(slice) : BinaryPrimitives.ReadUInt32LittleEndian(slice);
    }
}
