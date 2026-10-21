using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using ImageMagick.Formats;

namespace OneImage.Tests;

public class MetadataTests
{
    /// <summary>Builds a camera-style EXIF blob (TIFF structure) by hand: IFD0, Exif IFD and GPS IFD with out-of-line values.</summary>
    internal static byte[] BuildExif(bool littleEndian, int orientation, bool prefix = false)
    {
        var data = new List<byte>();
        void P16(List<byte> l, int v) { Span<byte> b = stackalloc byte[2]; if (littleEndian) { BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); } else { BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); } l.AddRange(b.ToArray()); }
        void P32(List<byte> l, uint v) { Span<byte> b = stackalloc byte[4]; if (littleEndian) { BinaryPrimitives.WriteUInt32LittleEndian(b, v); } else { BinaryPrimitives.WriteUInt32BigEndian(b, v); } l.AddRange(b.ToArray()); }

        // Layout: header(8) | IFD0 | Exif IFD | GPS IFD | value area
        var ascii = new Dictionary<string, int>();
        var values = new List<byte>();
        int ifd0Count = 6, exifCount = 4, gpsCount = 6;
        int ifd0Off = 8;
        int exifOff = ifd0Off + 2 + (ifd0Count * 12) + 4;
        int gpsOff = exifOff + 2 + (exifCount * 12) + 4;
        int valueBase = gpsOff + 2 + (gpsCount * 12) + 4;
        int PutValue(byte[] bytes)
        {
            int off = valueBase + values.Count;
            values.AddRange(bytes);
            if ((values.Count & 1) != 0) { values.Add(0); }
            return off;
        }

        byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");
        byte[] Rationals(params (uint n, uint d)[] r)
        {
            var l = new List<byte>();
            foreach ((uint n, uint d) in r) { P32(l, n); P32(l, d); }
            return l.ToArray();
        }

        void Entry(List<byte> l, int tag, int type, int count, object value)
        {
            P16(l, tag); P16(l, type); P32(l, (uint)count);
            switch (value)
            {
                case int shortVal when type == 3: P16(l, shortVal); P16(l, 0); break;
                case uint longVal when type == 4: P32(l, longVal); break;
                case byte[] bytes when bytes.Length <= 4: l.AddRange(bytes); for (int i = bytes.Length; i < 4; i++) { l.Add(0); } break;
                case byte[] bytes: P32(l, (uint)PutValue(bytes)); break;
                default: throw new InvalidOperationException();
            }
        }

        var ifd0 = new List<byte>(); P16(ifd0, ifd0Count);
        Entry(ifd0, 0x010F, 2, 6, Ascii("Acme"));
        Entry(ifd0, 0x0110, 2, 11, Ascii("Camera 100"));
        Entry(ifd0, 0x0112, 3, 1, orientation);
        Entry(ifd0, 0x0132, 2, 20, Ascii("2023:05:17 10:20:30"));
        Entry(ifd0, 0x8769, 4, 1, (uint)exifOff);
        Entry(ifd0, 0x8825, 4, 1, (uint)gpsOff);
        P32(ifd0, 0);
        var exif = new List<byte>(); P16(exif, exifCount);
        Entry(exif, 0x9003, 2, 20, Ascii("2023:05:17 10:20:30"));
        Entry(exif, 0x927C, 7, 24, new byte[24]);
        Entry(exif, 0xA002, 4, 1, 4000u);
        Entry(exif, 0xA003, 4, 1, 3000u);
        P32(exif, 0);
        var gps = new List<byte>(); P16(gps, gpsCount);
        Entry(gps, 1, 2, 2, Ascii("N"));
        Entry(gps, 2, 5, 3, Rationals((47, 1), (36, 1), (3000, 100)));
        Entry(gps, 3, 2, 2, Ascii("W"));
        Entry(gps, 4, 5, 3, Rationals((122, 1), (19, 1), (1200, 100)));
        Entry(gps, 5, 1, 1, new byte[] { 0 });
        Entry(gps, 6, 5, 1, Rationals((1520, 10)));
        P32(gps, 0);

        data.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        P16(data, 42);
        P32(data, 8);
        data.AddRange(ifd0); data.AddRange(exif); data.AddRange(gps); data.AddRange(values);
        byte[] r = data.ToArray();
        if (!prefix) { return r; }
        return [.. "Exif\0\0"u8.ToArray(), .. r];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypedTagsAreRead(bool littleEndian)
    {
        ExifData e = ExifData.Parse(BuildExif(littleEndian, 6, prefix: littleEndian));
        Assert.Equal(6, e.Orientation);
        Assert.Equal("Acme", e.Make);
        Assert.Equal("Camera 100", e.Model);
        Assert.Equal(new DateTime(2023, 5, 17, 10, 20, 30), e.CaptureTime);
        Assert.Equal(4000, e.PixelWidth);
        Assert.Equal(3000, e.PixelHeight);
        Assert.NotNull(e.Gps);
        Assert.Equal(47 + (36 / 60.0) + (30 / 3600.0), e.Gps!.Latitude, 9);
        Assert.Equal(-(122 + (19 / 60.0) + (12 / 3600.0)), e.Gps.Longitude, 9);
        Assert.Equal(152.0, e.Gps.Altitude);
    }

    [Fact]
    public void AllOrientationsParse()
    {
        for (int o = 1; o <= 8; o++)
        {
            Assert.Equal(o, ExifData.Parse(BuildExif(true, o)).Orientation);
            var meta = new ImageMetadata { Exif = BuildExif(false, o), Orientation = o };
            Assert.Equal(o, meta.ReadExif()!.Orientation);
        }

        // Out-of-range orientation values are ignored rather than trusted.
        Assert.Null(ExifData.Parse(BuildExif(true, 9)).Orientation);
        Assert.Null(ExifData.Parse(BuildExif(true, 0)).Orientation);
    }

    [Fact]
    public void OrientationOpsDescribeTheEightCases()
    {
        (bool flip, int rot)[] expect = [(false, 0), (true, 0), (false, 180), (true, 180), (true, 270), (false, 90), (true, 90), (false, 270)];
        for (int o = 1; o <= 8; o++)
        {
            OrientationOps ops = OrientationOps.For(o);
            Assert.Equal(expect[o - 1], (ops.FlipHorizontal, ops.RotateClockwise));
            Assert.Equal(o >= 5, ops.SwapsAxes);
        }

        Assert.True(OrientationOps.For(0).IsIdentity);
        Assert.True(OrientationOps.For(99).IsIdentity);
    }

    [Fact]
    public void MagickGeneratedExifIsRead()
    {
        using var img = new MagickImage(MagickColors.Red, 8, 8);
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.Orientation, (ushort)8);
        profile.SetValue(ExifTag.Make, "Contoso");
        profile.SetValue(ExifTag.Model, "X-1");
        profile.SetValue(ExifTag.DateTimeOriginal, "2021:12:31 23:59:58");
        profile.SetValue(ExifTag.GPSLatitudeRef, "S");
        profile.SetValue(ExifTag.GPSLatitude, [new Rational(33, 1), new Rational(51, 1), new Rational(0, 1)]);
        profile.SetValue(ExifTag.GPSLongitudeRef, "E");
        profile.SetValue(ExifTag.GPSLongitude, [new Rational(151, 1), new Rational(12, 1), new Rational(0, 1)]);
        byte[] blob = profile.ToByteArray()!;
        ExifData e = ExifData.Parse(blob);
        Assert.Equal(8, e.Orientation);
        Assert.Equal("Contoso", e.Make);
        Assert.Equal("X-1", e.Model);
        Assert.Equal(new DateTime(2021, 12, 31, 23, 59, 58), e.CaptureTime);
        Assert.Equal(-(33 + (51 / 60.0)), e.Gps!.Latitude, 9);
        Assert.Equal(151 + (12 / 60.0), e.Gps.Longitude, 9);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExifForOutputCorrectsOrientationAndDimensions(bool littleEndian)
    {
        byte[] src = BuildExif(littleEndian, 6, prefix: true);
        var meta = new ImageMetadata { Exif = src, Orientation = 6 };
        byte[] outBlob = meta.ExifForOutput(1500, 2000, 1).ToArray();
        ExifData e = ExifData.Parse(outBlob);
        Assert.Equal(1, e.Orientation);
        Assert.Equal(1500, e.PixelWidth);
        Assert.Equal(2000, e.PixelHeight);
        Assert.Equal("Acme", e.Make);
        Assert.Equal(152.0, e.Gps!.Altitude);
        Assert.True(outBlob.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8));

        // Unchanged values give an identical blob; the source is never modified.
        byte[] same = new ImageMetadata { Exif = src }.ExifForOutput(4000, 3000, 6).ToArray();
        Assert.Equal(6, ExifData.Parse(same).Orientation);
        Assert.Equal(src.Length, same.Length);
        Assert.Equal(6, ExifData.Parse(src).Orientation);
    }

    [Fact]
    public void ExifForOutputAddsMissingTagsAndKeepsOldDataInPlace()
    {
        // Minimal blob: IFD0 with only Make. Orientation and dimensions are added without moving the Make string.
        var b = new List<byte>(); b.AddRange("MM"u8.ToArray()); b.AddRange(new byte[] { 0, 42, 0, 0, 0, 8 });
        b.AddRange(new byte[] { 0, 1, 0x01, 0x0F, 0, 2, 0, 0, 0, 6, 0, 0, 0, 26 }); b.AddRange(new byte[] { 0, 0, 0, 0 });
        b.AddRange(Encoding.ASCII.GetBytes("Acme\0\0"));
        byte[] src = b.ToArray();
        byte[] o = new ImageMetadata { Exif = src }.ExifForOutput(10, 20, 3).ToArray();
        ExifData e = ExifData.Parse(o);
        Assert.Equal(3, e.Orientation);
        Assert.Equal(10, e.PixelWidth);
        Assert.Equal(20, e.PixelHeight);
        Assert.Equal("Acme", e.Make);
        Assert.True(o.AsSpan(26, 6).SequenceEqual(src.AsSpan(26, 6)));

        // Orientation 1 on a blob without the tag adds nothing for orientation.
        byte[] o1 = new ImageMetadata { Exif = src }.ExifForOutput(10, 20, 1).ToArray();
        Assert.Null(ExifData.Parse(o1).Orientation);
        Assert.Empty(new ImageMetadata().ExifForOutput(1, 1).ToArray());
    }

    [Fact]
    public void StripMetadataRemovesBlobsAndHonorsKeepIcc()
    {
        var meta = new ImageMetadata
        {
            IccProfile = ColorProfile.DisplayP3.IccBytes,
            Exif = BuildExif(true, 6),
            Xmp = Encoding.UTF8.GetBytes("<x/>"),
            Iptc = new byte[] { 1, 2, 3 },
            Orientation = 6,
        };
        Image img = Image.FromPixels<Rgb24>(new Rgb24[4], 2, 2).WithMetadata(meta);
        Image stripped = img.StripMetadata();
        Assert.True(stripped.Metadata.Exif.IsEmpty && stripped.Metadata.Xmp.IsEmpty && stripped.Metadata.Iptc.IsEmpty);
        Assert.True(stripped.Metadata.IccProfile.IsEmpty);
        Assert.Equal(6, stripped.Metadata.Orientation);
        Image keep = img.StripMetadata(keepIcc: true);
        Assert.True(keep.Metadata.IccProfile.Span.SequenceEqual(ColorProfile.DisplayP3.IccBytes.Span));
        Assert.True(keep.Metadata.Exif.IsEmpty);
        Assert.True(img.Metadata.Exif.Length > 0);
    }

    [Fact]
    public void BrokenEmbeddedProfileFallsBackToDefault()
    {
        Image img = Image.FromPixels<Rgb24>(new Rgb24[4], 2, 2).WithMetadata(new ImageMetadata { IccProfile = new byte[] { 1, 2, 3, 4 } });
        Assert.Equal(ColorProfile.Srgb, img.ColorProfile);
        Assert.Null(img.Metadata.EmbeddedColorProfile);
    }
}
