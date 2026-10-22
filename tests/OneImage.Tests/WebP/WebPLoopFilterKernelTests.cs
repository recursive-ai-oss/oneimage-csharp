using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using OneImage.Kernels;
using Xunit;
using Xunit.Sdk;

namespace OneImage.Tests.WebP;

/// <summary>The vector loop filter against its scalar reference, over random and smooth data (smooth data makes the filter masks pass).</summary>
public sealed class WebPLoopFilterKernelTests
{
    private const int Stride = 48;
    private const int Rows = 48;

    private static byte[] Plane(int seed, int mode)
    {
        var rnd = new Random(seed);
        byte[] p = new byte[Stride * Rows];
        for (int i = 0; i < p.Length; i++)
        {
            int x = i % Stride;
            int y = i / Stride;
            p[i] = mode switch
            {
                0 => (byte)rnd.Next(256),
                1 => (byte)Math.Clamp(128 + (x / 3) + (y / 2) + rnd.Next(-3, 4), 0, 255),
                2 => (byte)(((x / 4) + (y / 4)) % 2 == 0 ? 100 + rnd.Next(0, 12) : 112 + rnd.Next(0, 12)),
                _ => (byte)(rnd.Next(4) == 0 ? (rnd.Next(2) == 0 ? 0 : 255) : 128 + rnd.Next(-2, 3)),
            };
        }

        return p;
    }

    // The reference runs the edges in the order the kernel promises.
    private static void ReferenceMacroblock(byte[] y, int yo, int ys, byte[] u, byte[] v, int uvo, int uvs, bool left, bool top, bool inner, int limit, int ilevel, int hev, bool simple)
    {
        int tMb = (2 * (limit + 4)) + 1;
        int tIn = (2 * limit) + 1;
        if (left)
        {
            WebPLoopFilterKernel.ReferenceEdge(y, yo, 1, ys, 16, tMb, ilevel, hev, true, simple);
            if (!simple)
            {
                WebPLoopFilterKernel.ReferenceEdge(u, uvo, 1, uvs, 8, tMb, ilevel, hev, true, false);
                WebPLoopFilterKernel.ReferenceEdge(v, uvo, 1, uvs, 8, tMb, ilevel, hev, true, false);
            }
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                WebPLoopFilterKernel.ReferenceEdge(y, yo + k, 1, ys, 16, tIn, ilevel, hev, false, simple);
            }

            if (!simple)
            {
                WebPLoopFilterKernel.ReferenceEdge(u, uvo + 4, 1, uvs, 8, tIn, ilevel, hev, false, false);
                WebPLoopFilterKernel.ReferenceEdge(v, uvo + 4, 1, uvs, 8, tIn, ilevel, hev, false, false);
            }
        }

        if (top)
        {
            WebPLoopFilterKernel.ReferenceEdge(y, yo, ys, 1, 16, tMb, ilevel, hev, true, simple);
            if (!simple)
            {
                WebPLoopFilterKernel.ReferenceEdge(u, uvo, uvs, 1, 8, tMb, ilevel, hev, true, false);
                WebPLoopFilterKernel.ReferenceEdge(v, uvo, uvs, 1, 8, tMb, ilevel, hev, true, false);
            }
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                WebPLoopFilterKernel.ReferenceEdge(y, yo + (k * ys), ys, 1, 16, tIn, ilevel, hev, false, simple);
            }

            if (!simple)
            {
                WebPLoopFilterKernel.ReferenceEdge(u, uvo + (4 * uvs), uvs, 1, 8, tIn, ilevel, hev, false, false);
                WebPLoopFilterKernel.ReferenceEdge(v, uvo + (4 * uvs), uvs, 1, 8, tIn, ilevel, hev, false, false);
            }
        }
    }

    [Fact]
    public void MatchesScalarReference()
    {
        Console.WriteLine($"Vector128.IsHardwareAccelerated={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Avx2={Avx2.IsSupported}");
        int checks = 0;
        int changed = 0;
        for (int mode = 0; mode < 4; mode++)
        {
            for (int seed = 0; seed < 25; seed++)
            {
                var rnd = new Random((mode * 1000) + seed);
                int level = rnd.Next(1, 64);
                int ilevel = rnd.Next(1, 10);
                int limit = (2 * level) + ilevel;
                int hev = rnd.Next(3);
                foreach (bool simple in new[] { false, true })
                {
                    foreach (int flags in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
                    {
                        bool left = (flags & 1) != 0;
                        bool top = (flags & 2) != 0;
                        bool inner = (flags & 4) != 0;
                        byte[] y1 = Plane(seed, mode);
                        byte[] u1 = Plane(seed + 1, mode);
                        byte[] v1 = Plane(seed + 2, mode);
                        byte[] y2 = (byte[])y1.Clone();
                        byte[] u2 = (byte[])u1.Clone();
                        byte[] v2 = (byte[])v1.Clone();
                        int yo = (8 * Stride) + 8;
                        int uvo = (8 * Stride) + 8;
                        if (simple)
                        {
                            WebPLoopFilterKernel.FilterSimple(y1, yo, Stride, left, top, inner, limit);
                        }
                        else
                        {
                            WebPLoopFilterKernel.FilterNormal(y1, yo, Stride, u1, v1, uvo, Stride, left, top, inner, limit, ilevel, hev);
                        }

                        ReferenceMacroblock(y2, yo, Stride, u2, v2, uvo, Stride, left, top, inner, limit, ilevel, hev, simple);
                        Assert.True(y1.AsSpan().SequenceEqual(y2), $"luma mode {mode} seed {seed} simple {simple} flags {flags}");
                        Assert.True(u1.AsSpan().SequenceEqual(u2), $"u mode {mode} seed {seed} flags {flags}");
                        Assert.True(v1.AsSpan().SequenceEqual(v2), $"v mode {mode} seed {seed} flags {flags}");
                        checks++;
                        if (!y1.AsSpan().SequenceEqual(Plane(seed, mode)))
                        {
                            changed++;
                        }
                    }
                }
            }
        }

        // The comparison is only meaningful if the filters actually fired in a good share of the cases.
        Assert.True(changed > checks / 4, $"{changed} of {checks} cases changed pixels");
    }

    [Fact]
    public void RejectsMacroblocksOutsideThePlane()
    {
        byte[] p = new byte[Stride * Rows];
        Assert.Throws<ArgumentException>(() => WebPLoopFilterKernel.FilterSimple(p, 0, Stride, left: true, top: false, inner: false, limit: 10));
        Assert.Throws<ArgumentException>(() => WebPLoopFilterKernel.FilterSimple(p, (Rows - 8) * Stride, Stride, left: false, top: false, inner: true, limit: 10));
        Assert.Throws<ArgumentException>(() => WebPLoopFilterKernel.FilterNormal(p, 3 * Stride, Stride, p, p, 4 * Stride, Stride, false, true, false, 10, 3, 1));
    }
}

public sealed class WebPPredictKernelTests
{
    [Fact]
    public void ScoreTileMatchesPerPixelResiduals()
    {
        var rnd = new Random(5);
        const int w = 37, h = 19;
        uint[] a = new uint[w * h];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = (uint)rnd.Next() | (rnd.Next(3) == 0 ? 0xff000000u : 0);
        }

        for (int mode = 0; mode < 14; mode++)
        {
            int[] hist = new int[1024];
            WebPPredictKernels.ScoreTile(a, w, h, 3, 2, 35, 17, mode, hist);
            Assert.Equal((35 - 3) * (17 - 2), hist.Take(256).Sum());
            Assert.Equal(hist.Take(256).Sum(), hist.Skip(768).Sum());
        }

        Assert.Throws<ArgumentException>(() => WebPPredictKernels.ScoreTile(a, w, h, 0, 0, w + 1, h, 0, new int[1024]));
        Assert.Throws<ArgumentException>(() => WebPPredictKernels.ScoreTile(a, w, h, 0, 0, w, h, 14, new int[1024]));
    }
}

public sealed class WebPIdctKernelTests
{
    [Fact]
    public void VectorMatchesScalarReference()
    {
        var rnd = new Random(77);
        for (int iter = 0; iter < 20000; iter++)
        {
            short[] coef = new short[16];
            int range = iter % 3 == 0 ? 40 : iter % 3 == 1 ? 2000 : 32767;
            for (int i = 0; i < 16; i++)
            {
                coef[i] = rnd.Next(4) == 0 ? (short)0 : (short)rnd.Next(-range, range + 1);
            }

            byte[] a = new byte[(4 * 32) + 8];
            rnd.NextBytes(a);
            byte[] b = (byte[])a.Clone();
            WebPIdctKernel.AddScalar(coef, a, 5, 32);
            WebPIdctKernel.Add(coef, b, 5, 32);
            Assert.True(a.AsSpan().SequenceEqual(b), $"iteration {iter}");
        }

        Assert.Throws<ArgumentException>(() => WebPIdctKernel.Add(new short[16], new byte[10], 0, 32));
    }
}
