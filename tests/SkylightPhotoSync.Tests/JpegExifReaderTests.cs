using System.Buffers.Binary;
using System.Text;
using SkylightPhotoSync.Sources;
using SkylightPhotoSync.Tests.TestSupport;

namespace SkylightPhotoSync.Tests;

public class JpegExifReaderTests
{
    private static readonly DateTime _fixtureDate = new(2020, 1, 2, 3, 4, 5);

    [Fact]
    public void Reads_DateTimeOriginal_from_little_endian_fixture()
    {
        var jpeg = File.ReadAllBytes(Fixtures.Image("tiny.jpg"));

        Assert.Equal(_fixtureDate, JpegExifReader.ReadDateTimeOriginal(jpeg));
    }

    [Fact]
    public void Reads_DateTimeOriginal_from_big_endian_exif()
    {
        var jpeg = BuildJpeg(bigEndian: true, "2021:12:31 23:59:58");

        Assert.Equal(new DateTime(2021, 12, 31, 23, 59, 58), JpegExifReader.ReadDateTimeOriginal(jpeg));
    }

    [Fact]
    public void Returns_null_when_exif_has_no_DateTimeOriginal()
    {
        var jpeg = BuildJpeg(bigEndian: false, dateTimeOriginal: null);

        Assert.Null(JpegExifReader.ReadDateTimeOriginal(jpeg));
    }

    [Fact]
    public void Returns_null_for_non_jpeg_bytes()
    {
        var png = File.ReadAllBytes(Fixtures.Image("tiny.png"));

        Assert.Null(JpegExifReader.ReadDateTimeOriginal(png));
    }

    [Fact]
    public void Returns_null_for_truncated_jpeg_instead_of_throwing()
    {
        var jpeg = File.ReadAllBytes(Fixtures.Image("tiny.jpg"));

        for (var length = 0; length < 80; length++)
        {
            Assert.Null(JpegExifReader.ReadDateTimeOriginal(jpeg.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Returns_null_for_unparseable_date()
    {
        var jpeg = BuildJpeg(bigEndian: false, "not a date         ");

        Assert.Null(JpegExifReader.ReadDateTimeOriginal(jpeg));
    }

    /// <summary>
    /// A JPEG header (SOI, APP1 Exif, SOS) with IFD0 = {Orientation, ExifIFD pointer} and an
    /// Exif IFD that holds DateTimeOriginal when <paramref name="dateTimeOriginal"/> is given.
    /// </summary>
    private static byte[] BuildJpeg(bool bigEndian, string? dateTimeOriginal)
    {
        var tiff = new List<byte>();
        void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            }

            tiff.AddRange(b.ToArray());
        }

        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            }

            tiff.AddRange(b.ToArray());
        }

        tiff.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        U32(8);                                  // IFD0 offset
        const uint exifIfdOffset = 8 + 2 + 12 * 2 + 4;
        U16(2);
        U16(0x0112); U16(3); U32(1); U16(1); U16(0);          // Orientation = 1
        U16(0x8769); U16(4); U32(1); U32(exifIfdOffset);      // ExifIFD pointer
        U32(0);
        if (dateTimeOriginal is null)
        {
            U16(0);
            U32(0);
        }
        else
        {
            var date = Encoding.ASCII.GetBytes(dateTimeOriginal + "\0");
            U16(1);
            U16(0x9003); U16(2); U32((uint)date.Length); U32(exifIfdOffset + 2 + 12 + 4);
            U32(0);
            tiff.AddRange(date);
        }

        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        jpeg.Add((byte)((app1.Length + 2) >> 8));
        jpeg.Add((byte)((app1.Length + 2) & 0xFF));
        jpeg.AddRange(app1);
        jpeg.AddRange([0xFF, 0xDA, 0x00, 0x02]);   // SOS: nothing after this matters here
        return jpeg.ToArray();
    }
}
