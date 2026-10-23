using System.Buffers.Binary;
using OneImage.Layers;

namespace OneImage.Psd;

/// <summary>Maps the binary adjustment blocks of a PSD file to and from <see cref="Adjustment"/>.</summary>
internal static class AdjustmentCodec
{
    public static readonly string[] Keys = ["levl", "curv", "hue2", "hue ", "brit", "expA", "nvrt", "thrs", "post", "mixr", "vibA", "blnc", "blwh", "phfl", "clrL", "grdm", "selc", "GdFl", "PtFl"];

    public static bool IsAdjustmentKey(string key) => Array.IndexOf(Keys, key) >= 0 && key is not ("GdFl" or "PtFl");

    /// <summary>Reads an adjustment block. Returns null when the block is malformed or holds settings the model cannot reproduce.</summary>
    public static Adjustment? Read(string key, ReadOnlySpan<byte> data, PsdColorMode mode)
    {
        // Photoshop applies these to the ink or Lab channels; the model works on the RGB document, so they are kept but not reproduced.
        if ((mode is PsdColorMode.Cmyk or PsdColorMode.Lab or PsdColorMode.Multichannel) && key is "levl" or "curv" or "hue2" or "brit" or "expA")
        {
            return null;
        }

        bool gray = mode != PsdColorMode.Rgb;
        try
        {
            return key switch
            {
                "levl" => ReadLevels(data, gray),
                "curv" => ReadCurves(data, gray),
                "hue2" => ReadHueSaturation(data),
                "brit" => ReadBrightness(data),
                "expA" => ReadExposure(data),
                "nvrt" => Adjustment.Invert(),
                "thrs" => data.Length >= 2 ? Adjustment.Threshold(Math.Clamp((int)BinaryPrimitives.ReadInt16BigEndian(data), 1, 255) / 255.0) : null,
                "post" => data.Length >= 2 ? Adjustment.Posterize(Math.Clamp((int)BinaryPrimitives.ReadInt16BigEndian(data), 2, 255)) : null,
                _ => null,
            };
        }
        catch (ImageFormatException)
        {
            return null;
        }
    }

    private static Adjustment? ReadLevels(ReadOnlySpan<byte> data, bool gray)
    {
        var r = new SpanReader(data);
        if (r.U16() != 2)
        {
            return null;
        }

        // Up to 29 sets of five shorts: the master set first, then one per channel (red, green, blue). Sets that change nothing are skipped.
        var steps = new List<Adjustment>();
        int sets = Math.Min(4, r.Remaining / 10);
        for (int i = 0; i < sets; i++)
        {
            int inFloor = r.I16();
            int inCeil = r.I16();
            int outFloor = r.I16();
            int outCeil = r.I16();
            int gamma = r.I16();
            if (inFloor == 0 && inCeil == 255 && outFloor == 0 && outCeil == 255 && gamma == 100)
            {
                continue;
            }

            if (inCeil <= inFloor || inCeil > 255 || inFloor < 0 || gamma is < 10 or > 999 || outFloor is < 0 or > 255 || outCeil is < 0 or > 255)
            {
                return null;
            }

            if (gray && i > 1)
            {
                continue;
            }

            ColorChannel ch = gray || i == 0 ? ColorChannel.All : i == 1 ? ColorChannel.Red : i == 2 ? ColorChannel.Green : ColorChannel.Blue;
            steps.Add(Adjustment.Levels(inFloor / 255.0, inCeil / 255.0, gamma / 100.0, outFloor / 255.0, outCeil / 255.0, ch));
        }

        // Per-channel sets act before the master set.
        if (steps.Count > 1 && steps[0].Parameters.Any(static q => q.Name == "channel" && q.Value is "all"))
        {
            Adjustment master = steps[0];
            steps.RemoveAt(0);
            steps.Add(master);
        }

        return Combine("levels", steps);
    }

    private static Adjustment Combine(string name, List<Adjustment> steps)
    {
        if (steps.Count == 0)
        {
            return Adjustment.Levels();
        }

        if (steps.Count == 1)
        {
            return steps[0];
        }

        Adjustment[] chain = [.. steps];
        return Adjustment.Custom("psd-" + name, image =>
        {
            Image result = image;
            foreach (Adjustment a in chain)
            {
                result = a.Apply(result);
            }

            return result;
        });
    }

    // Curves in a layer: a pad byte, the version (1), a bit mask of the curves present, then each curve as a point count and
    // (output, input) pairs, 0..255. A trailing 'Crv ' block (version 4) lists further curves with their channel index.
    private static Adjustment? ReadCurves(ReadOnlySpan<byte> data, bool gray)
    {
        var r = new SpanReader(data);
        r.U8();
        int version = r.U16();
        if (version is not (1 or 4))
        {
            return null;
        }

        uint mask = r.U32();
        var curves = new List<(int Channel, CurvePoint[] Points)>();
        for (int c = 0; c < 32 && mask >> c != 0; c++)
        {
            if ((mask >> c & 1) == 0)
            {
                continue;
            }

            curves.Add((c, ReadCurve(ref r)));
        }

        if (r.Remaining >= 10 && r.U32() == Fourcc.Of("Crv "))
        {
            r.U16();
            uint n = r.U32();
            for (uint i = 0; i < n && r.Remaining >= 4; i++)
            {
                int ch = r.U16();
                curves.RemoveAll(x => x.Channel == ch);
                curves.Add((ch, ReadCurve(ref r)));
            }
        }

        var steps = new List<Adjustment>();
        // The per-channel curves act before the master (composite) curve.
        foreach ((int channel, CurvePoint[] points) in curves.OrderBy(static c => c.Channel == 0 ? int.MaxValue : c.Channel))
        {
            if (IsIdentity(points) || channel > (gray ? 1 : 3))
            {
                continue;
            }

            ColorChannel ch = gray || channel == 0 ? ColorChannel.All : channel == 1 ? ColorChannel.Red : channel == 2 ? ColorChannel.Green : ColorChannel.Blue;
            steps.Add(Adjustment.Curves(points, ch));
        }

        return Combine("curves", steps);
    }

    private static CurvePoint[] ReadCurve(ref SpanReader r)
    {
        int count = r.U16();
        if (count is < 2 or > 19)
        {
            throw new ImageFormatException("A curve has an invalid point count.");
        }

        var pts = new CurvePoint[count];
        for (int i = 0; i < count; i++)
        {
            int output = r.U16();
            int input = r.U16();
            if (input > 255 || output > 255)
            {
                throw new ImageFormatException("A curve point lies outside 0..255.");
            }

            pts[i] = new CurvePoint(input / 255.0, output / 255.0);
        }

        // The curve needs distinct inputs.
        if (pts.Select(static p => p.X).Distinct().Count() != pts.Length)
        {
            throw new ImageFormatException("A curve has two points with the same input.");
        }

        return pts;
    }

    private static bool IsIdentity(CurvePoint[] pts) => pts.Length == 2 && pts[0] == new CurvePoint(0, 0) && pts[1] == new CurvePoint(1, 1);

    private static Adjustment? ReadHueSaturation(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        if (r.U16() != 2)
        {
            return null;
        }

        bool colorize = r.U8() != 0;
        r.U8();
        r.Skip(6);
        int hue = r.I16();
        int sat = r.I16();
        int light = r.I16();
        if (colorize)
        {
            return null;
        }

        // Per-range settings (the six hextants) are not modelled: a file that uses one is kept but not reproduced.
        for (int i = 0; i < 6 && r.Remaining >= 14; i++)
        {
            r.Skip(8);
            if (r.I16() != 0 || r.I16() != 0 || r.I16() != 0)
            {
                return null;
            }
        }

        return Adjustment.HueSaturation(hue, sat / 100.0, light / 100.0);
    }

    private static Adjustment? ReadBrightness(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        int brightness = r.I16();
        int contrast = r.I16();
        return Adjustment.BrightnessContrast(Math.Clamp(brightness, -150, 150) / 150.0, Math.Clamp(contrast, -50, 100) / 100.0);
    }

    private static Adjustment? ReadExposure(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        if (r.U16() != 1)
        {
            return null;
        }

        float exposure = BitConverter.Int32BitsToSingle(r.I32());
        float offset = BitConverter.Int32BitsToSingle(r.I32());
        float gamma = BitConverter.Int32BitsToSingle(r.I32());
        if (offset != 0 || gamma != 1 || !float.IsFinite(exposure) || Math.Abs(exposure) > 16)
        {
            return null;
        }

        return Adjustment.Exposure(exposure);
    }

    /// <summary>Writes the block for an adjustment built in memory, or returns null if it has no PSD equivalent.</summary>
    public static PsdBlock? Write(Adjustment a)
    {
        double Get(string name, double fallback = 0)
        {
            foreach (OpParameter p in a.Parameters)
            {
                if (p.Name == name && p.Value is double d)
                {
                    return d;
                }
            }

            return fallback;
        }

        string Str(string name)
        {
            foreach (OpParameter p in a.Parameters)
            {
                if (p.Name == name && p.Value is string s)
                {
                    return s;
                }
            }

            return "";
        }

        var ms = new MemoryStream();
        void U16(int v)
        {
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }

        switch (a.Name)
        {
            case "levels":
                {
                    U16(2);
                    int index = Str("channel") switch { "red" => 1, "green" => 2, "blue" => 3, _ => 0 };
                    for (int i = 0; i < 29; i++)
                    {
                        if (i == index)
                        {
                            U16((int)Math.Round(Get("in-black") * 255));
                            U16((int)Math.Round(Get("in-white", 1) * 255));
                            U16((int)Math.Round(Get("out-black") * 255));
                            U16((int)Math.Round(Get("out-white", 1) * 255));
                            U16((int)Math.Round(Math.Clamp(Get("gamma", 1), 0.1, 9.99) * 100));
                        }
                        else
                        {
                            U16(0);
                            U16(255);
                            U16(0);
                            U16(255);
                            U16(100);
                        }
                    }

                    return new PsdBlock("levl", ms.ToArray());
                }

            case "curves":
                {
                    int index = Str("channel") switch { "red" => 1, "green" => 2, "blue" => 3, _ => 0 };
                    ms.WriteByte(0);
                    U16(1);
                    U16(0);
                    U16(1 << index);
                    string[] pts = Str("points").Split(';', StringSplitOptions.RemoveEmptyEntries);
                    U16(pts.Length);
                    foreach (string p in pts)
                    {
                        string[] xy = p.Split(',');
                        U16((int)Math.Round(double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture) * 255));
                        U16((int)Math.Round(double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture) * 255));
                    }

                    return new PsdBlock("curv", ms.ToArray());
                }

            case "hue-saturation":
                {
                    U16(2);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                    for (int i = 0; i < 3; i++)
                    {
                        U16(0);
                    }

                    U16((int)Math.Round(Get("hue")));
                    U16((int)Math.Round(Get("saturation") * 100));
                    U16((int)Math.Round(Get("lightness") * 100));
                    for (int range = 0; range < 6; range++)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            U16(0);
                        }

                        U16(0);
                        U16(0);
                        U16(0);
                    }

                    return new PsdBlock("hue2", ms.ToArray());
                }

            case "brightness-contrast":
                U16((int)Math.Round(Get("brightness") * 150));
                U16((int)Math.Round(Get("contrast") * 100));
                U16(127);
                ms.WriteByte(0);
                return new PsdBlock("brit", ms.ToArray());
            case "exposure":
                {
                    U16(1);
                    foreach (float f in new[] { (float)Get("stops"), 0f, 1f })
                    {
                        int bits = BitConverter.SingleToInt32Bits(f);
                        U16(bits >> 16 & 0xFFFF);
                        U16(bits & 0xFFFF);
                    }

                    return new PsdBlock("expA", ms.ToArray());
                }

            case "invert":
                return new PsdBlock("nvrt", Array.Empty<byte>());
            case "threshold":
                U16((int)Math.Round(Get("level", 0.5) * 255));
                return new PsdBlock("thrs", ms.ToArray());
            case "posterize":
                {
                    foreach (OpParameter p in a.Parameters)
                    {
                        if (p.Name == "levels" && p.Value is long l)
                        {
                            U16((int)l);
                        }
                    }

                    return new PsdBlock("post", ms.ToArray());
                }

            default:
                return null;
        }
    }
}
