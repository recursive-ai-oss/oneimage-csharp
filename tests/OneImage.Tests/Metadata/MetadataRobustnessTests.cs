using System.Buffers.Binary;
using System.Diagnostics;

namespace OneImage.Tests;

/// <summary>Truncated, mutated and crafted ICC and EXIF data must produce typed errors, never other exceptions, crashes or long stalls.</summary>
public class MetadataRobustnessTests
{
    private static byte[][] IccSeeds() =>
    [
        ColorProfile.Srgb.IccBytes.ToArray(),
        ColorProfile.GenericCmyk.IccBytes.ToArray(),
        ColorProfile.Gray.IccBytes.ToArray(),
        File.ReadAllBytes(ColorTestUtil.TestDataPath("icc/sRGB_v4_ICC_preference.icc")),
        File.ReadAllBytes(ColorTestUtil.TestDataPath("icc/sRGB2014.icc")),
    ];

    private static void Exercise(byte[] icc)
    {
        try
        {
            ColorProfile p = ColorProfile.FromIcc(icc);
            foreach (RenderingIntent intent in new[] { RenderingIntent.Perceptual, RenderingIntent.RelativeColorimetric })
            {
                try
                {
                    ColorTransform t = ColorTransform.Get(p, ColorProfile.Srgb, intent, true);
                    if (p.Model != ColorModel.Lab)
                    {
                        byte[] src = new byte[16 * (p.Model == ColorModel.Cmyk ? 4 : p.Model == ColorModel.Gray ? 1 : 3)];
                        byte[] dst = new byte[48];
                        t.ConvertRow(src, new PixelFormat(p.Model, SampleType.U8), dst, PixelFormat.Rgb8, 16);
                    }

                    _ = ColorTransform.Get(ColorProfile.Srgb, p, intent, false);
                }
                catch (ImageException)
                {
                    // Typed errors are the contract.
                }
            }
        }
        catch (ImageException)
        {
        }
    }

    [Fact]
    public void MutatedAndTruncatedIccGivesOnlyTypedErrors()
    {
        var rnd = new Random(12345);
        var sw = Stopwatch.StartNew();
        int cases = 0;
        foreach (byte[] seed in IccSeeds())
        {
            for (int len = 0; len < Math.Min(seed.Length, 400); len += 7)
            {
                Exercise(seed.AsSpan(0, len).ToArray());
                cases++;
            }

            for (int i = 0; i < 25; i++)
            {
                Exercise(seed.AsSpan(0, rnd.Next(seed.Length)).ToArray());
                cases++;
            }

            // Byte flips: the header and tag table get a third of the damage, the rest is spread over the data.
            for (int i = 0; i < 120; i++)
            {
                byte[] m = (byte[])seed.Clone();
                int flips = 1 + rnd.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int pos = rnd.Next(3) == 0 ? rnd.Next(Math.Min(m.Length, 400)) : rnd.Next(m.Length);
                    m[pos] = (byte)rnd.Next(256);
                }

                Exercise(m);
                cases++;
            }
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), $"{cases} cases took {sw.Elapsed}");
    }

    [Fact]
    public void GarbageIccIsRejected()
    {
        Assert.Throws<ImageFormatException>(() => ColorProfile.FromIcc(new byte[10]));
        Assert.Throws<ImageFormatException>(() => ColorProfile.FromIcc(new byte[200]));
        var rnd = new Random(1);
        byte[] junk = new byte[4096];
        rnd.NextBytes(junk);
        Assert.Throws<ImageFormatException>(() => ColorProfile.FromIcc(junk));
        Assert.False(ColorProfile.TryFromIcc(junk, out _));
    }

    [Fact]
    public void CraftedHeadersDoNotOverAllocate()
    {
        // A lookup table with 255 grid points on 4 inputs would need 4 billion entries: rejected from the header, before any allocation.
        byte[] p = ColorProfile.GenericCmyk.IccBytes.ToArray();
        int a2b = FindTag(p, "A2B0");
        p[a2b + 10] = 255;
        Assert.Throws<ImageLimitException>(() => ColorProfile.FromIcc(p));

        // A tag count that cannot fit the data.
        byte[] q = ColorProfile.Srgb.IccBytes.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(q.AsSpan(128), uint.MaxValue);
        Assert.Throws<ImageFormatException>(() => ColorProfile.FromIcc(q));

        // A curve claiming 2^32-1 entries.
        byte[] g = ColorProfile.Gray.IccBytes.ToArray();
        int ktrc = FindTag(g, "kTRC");
        Span<byte> t = g.AsSpan(ktrc);
        t[0] = (byte)'c';
        t[1] = (byte)'u';
        t[2] = (byte)'r';
        t[3] = (byte)'v';
        BinaryPrimitives.WriteUInt32BigEndian(t[8..], uint.MaxValue);
        Assert.Throws<ImageFormatException>(() => ColorProfile.FromIcc(g));

        // A profile whose size field exceeds the data.
        byte[] s = ColorProfile.Srgb.IccBytes.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)s.Length + 100);
        Assert.Throws<ImageFormatException>(() => ColorProfile.FromIcc(s));
    }

    private static int FindTag(byte[] p, string sig)
    {
        uint n = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(128));
        for (int i = 0; i < n; i++)
        {
            int e = 132 + (i * 12);
            if (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(e)) == IccData.Sig(sig))
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(e + 4));
            }
        }

        throw new InvalidOperationException(sig);
    }

    [Fact]
    public void MutatedAndTruncatedExifGivesOnlyTypedErrors()
    {
        var rnd = new Random(777);
        var sw = Stopwatch.StartNew();
        foreach (bool le in new[] { true, false })
        {
            byte[] seed = MetadataTests.BuildExif(le, 6, prefix: le);
            for (int len = 0; len <= seed.Length; len++)
            {
                ExerciseExif(seed.AsSpan(0, len).ToArray());
            }

            for (int i = 0; i < 3000; i++)
            {
                byte[] m = (byte[])seed.Clone();
                int flips = 1 + rnd.Next(5);
                for (int f = 0; f < flips; f++)
                {
                    m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
                }

                ExerciseExif(m);
            }
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
    }

    private static void ExerciseExif(byte[] blob)
    {
        try
        {
            _ = ExifData.Parse(blob);
        }
        catch (ImageFormatException)
        {
            // Typed errors are the contract.
        }

        try
        {
            _ = ExifWriter.Patch(blob, 100, 50, 1);
            _ = ExifWriter.Patch(blob, 100, 50, 6);
        }
        catch (ImageFormatException)
        {
        }
    }

    [Fact]
    public void ExifLoopsAndHugeCountsAreHandled()
    {
        // The Exif pointer of IFD0 points back at IFD0, and an entry claims 4 billion values.
        byte[] b = MetadataTests.BuildExif(true, 1);
        int ifd0 = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(ifd0 + 2 + (4 * 12) + 8), (uint)ifd0);
        _ = ExifData.Parse(b);

        byte[] h = MetadataTests.BuildExif(true, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(ifd0 + 2 + 4), uint.MaxValue);
        Assert.Throws<ImageFormatException>(() => ExifData.Parse(h));

        byte[] t = MetadataTests.BuildExif(true, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(ifd0), 65535);
        Assert.Throws<ImageFormatException>(() => ExifData.Parse(t));
        Assert.Throws<ImageFormatException>(() => ExifData.Parse(new byte[] { 1, 2, 3 }));
        Assert.False(new ImageMetadata { Exif = new byte[] { 1, 2, 3 } }.TryReadExif(out _));
    }
}
