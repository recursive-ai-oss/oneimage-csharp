namespace OneImage.Codecs.Jpeg;

/// <summary>Drives a full decode: headers, scans, and the output pipeline.</summary>
internal static class JpegDecoderCore
{
    public static void Decode(Stream source, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken ct, Func<JpegFrame, JpegHeaders, JpegColorPlan>? planOverride = null)
    {
        int scale = request.ScaleDenominator;
        if (scale is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Scale denominator must be 1, 2, 4 or 8.");
        }

        using var reader = new JpegReader(source);
        var tables = new JpegTables();
        JpegHeaders h = JpegParser.ReadHeaders(reader, limits, tables, collectMetadata: false);
        JpegFrame f = h.Frame!;
        var plan = planOverride is null ? JpegColorPlan.Decide(f, h) : planOverride(f, h);
        if (f.Precision == 8)
        {
            Run<byte>(reader, h, f, plan, tables, request, sink, limits, ct);
        }
        else
        {
            Run<ushort>(reader, h, f, plan, tables, request, sink, limits, ct);
        }
    }

    private static void Run<T>(JpegReader reader, JpegHeaders h, JpegFrame f, JpegColorPlan plan, JpegTables tables, in DecodeRequest request, IRowSink sink, ImageLimits limits, CancellationToken ct)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        using var pipe = new JpegPipeline<T>(f, plan, request.ScaleDenominator, request.Region, sink);
        bool dcOnly = pipe.DcOnly;
        JpegScan scan = JpegParser.ParseSos(reader, f);

        // A sequential image with a single scan over all components streams MCU row by MCU row; everything else
        // (progressive, or components split over several scans) needs all coefficients before the first pixel row.
        bool streaming = !f.Progressive && scan.Components.Length == f.Components.Length;
        using var store = new JpegCoefficientStore(f, streaming, reader, limits);
        var decoder = new JpegScanDecoder(reader, f, tables, store) { DcOnly = dcOnly };
        Func<int, bool>? onRow = streaming ? row => pipe.AddMcuRow(row, store) : null;
        bool more = true;
        while (more)
        {
            LatchQuantTables(scan, tables);
            if (f.Progressive && dcOnly && scan.Ss > 0)
            {
                // Only the DC image matters at 1/8 size, so skip the AC scans without decoding them.
                reader.SkipEntropyData();
            }
            else
            {
                decoder.Decode(scan, onRow, ct);
            }

            if (streaming)
            {
                break;
            }

            more = NextScan(reader, h, f, tables, limits, out scan);
        }

        if (!streaming)
        {
            for (int r = 0; r < f.McusY; r++)
            {
                ct.ThrowIfCancellationRequested();
                if (!pipe.AddMcuRow(r, store))
                {
                    break;
                }
            }
        }

        if (pipe.RowsWritten != request.Region.Height)
        {
            throw new ImageFormatException("JPEG data ended before all rows were decoded.");
        }
    }

    private static bool NextScan(JpegReader reader, JpegHeaders h, JpegFrame f, JpegTables tables, ImageLimits limits, out JpegScan scan)
    {
        while (true)
        {
            int m = reader.NextMarker();
            if (m is JpegConstants.Eoi or < 0)
            {
                scan = null!;
                return false;
            }

            if (m == JpegConstants.Sos)
            {
                scan = JpegParser.ParseSos(reader, f);
                return true;
            }

            if (m is JpegConstants.Soi or 0x01 || m is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            JpegParser.ParseSegment(reader, m, h, tables, limits, null);
        }
    }

    private static void LatchQuantTables(JpegScan scan, JpegTables tables)
    {
        foreach (JpegComponent c in scan.Components)
        {
            c.Quant ??= tables.Quant[c.Tq] ?? throw new ImageFormatException("JPEG scan uses an undefined quantization table.");
        }
    }
}
