using System.Buffers;
using OneImage.Compression;
using OneImage.Kernels;

namespace OneImage.Codecs.Tiff;

/// <summary>
/// Writes classic TIFF or BigTIFF, little-endian, one directory per frame. Every frame is stored as strips of roughly 512 KiB
/// of raw samples, each compressed on its own. Output is a pure function of the input pixels, metadata and options.
/// </summary>
internal sealed class TiffEncoder : IImageEncoder
{
    private const int TargetStripBytes = 512 * 1024;

    public IReadOnlyList<PixelFormat> SupportedFormats { get; } =
    [
        PixelFormat.Rgb8, PixelFormat.Rgba8, PixelFormat.Gray8, PixelFormat.GrayAlpha8,
        PixelFormat.Rgb16, PixelFormat.Rgba16, PixelFormat.Gray16,
        PixelFormat.RgbF32, PixelFormat.RgbaF32, PixelFormat.GrayF32,
        PixelFormat.Cmyk8, PixelFormat.Cmyk16,
    ];

    public bool SupportsMultipleFrames => true;

    public void Encode(ImageEncodeRequest request, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        TiffEncoderOptions options = request.Options switch
        {
            null => new TiffEncoderOptions(),
            TiffEncoderOptions o => o,
            _ => throw new ArgumentException("TIFF encoding needs TiffEncoderOptions.", nameof(request)),
        };
        bool big = options.BigTiff;
        long pos = big ? 16 : 8;
        Span<byte> header = stackalloc byte[16];
        header.Clear();
        header[0] = header[1] = (byte)'I';
        if (big)
        {
            TiffMetadataIo.PutU16(header[2..], 43, false);
            TiffMetadataIo.PutU16(header[4..], 8, false);
            TiffMetadataIo.PutU64(header[8..], 16, false);
        }
        else
        {
            TiffMetadataIo.PutU16(header[2..], 42, false);
            TiffMetadataIo.PutU32(header[4..], 8, false);
        }

        destination.Write(header[..(int)pos]);
        List<TiffTreeEntry>? exif = request.Metadata.Exif.IsEmpty ? null : TiffMetadataIo.ParseBlob(request.Metadata.Exif, request.Limits);
        int count = request.Frames.Count;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pos = WriteFrame(request, options, request.Frames[i], i, count, i == 0, exif, destination, pos, cancellationToken);
        }
    }

    private static long WriteFrame(ImageEncodeRequest request, TiffEncoderOptions options, ImageFrameReader frame, int index, int count, bool first, List<TiffTreeEntry>? exif, Stream destination, long pos, CancellationToken ct)
    {
        PixelFormat fmt = frame.Format;
        bool big = options.BigTiff;
        int spp = fmt.ChannelCount;
        int bits = fmt.BytesPerSample * 8;
        bool isFloat = fmt.Sample == SampleType.F32;
        int photometric = fmt.Model switch
        {
            ColorModel.Gray => 1,
            ColorModel.Rgb => 2,
            ColorModel.Cmyk => 5,
            _ => throw new ImageOperationException("The pixel format cannot be written as TIFF."),
        };
        int rowBytes = checked(frame.Width * fmt.BytesPerPixel);
        int rps = options.RowsPerStrip > 0 ? options.RowsPerStrip : Math.Max(1, TargetStripBytes / Math.Max(rowBytes, 1));
        rps = Math.Min(rps, frame.Height);
        TiffCompression comp = options.Compression;
        int predictor = 1;
        if (options.UsePredictor && comp is TiffCompression.Lzw or TiffCompression.Deflate)
        {
            predictor = isFloat ? 3 : 2;
        }

        using var data = new TiffChunkSink();
        int strips = (frame.Height + rps - 1) / rps;
        long[] offsets = new long[strips];
        long[] counts = new long[strips];
        byte[] raw = ArrayPool<byte>.Shared.Rent(checked(rowBytes * rps));
        byte[] scratch = predictor == 3 ? ArrayPool<byte>.Shared.Rent(rowBytes) : [];
        byte[] packBuf = comp == TiffCompression.PackBits ? ArrayPool<byte>.Shared.Rent(rowBytes + (rowBytes / 128) + 2) : [];
        try
        {
            for (int s = 0; s < strips; s++)
            {
                ct.ThrowIfCancellationRequested();
                int y = s * rps;
                int rows = Math.Min(rps, frame.Height - y);
                Span<byte> strip = raw.AsSpan(0, rows * rowBytes);
                frame.ReadRows(y, rows, strip);
                if (predictor == 2)
                {
                    TiffKernels.ApplyHorizontal(strip, rowBytes, spp, bits);
                }
                else if (predictor == 3)
                {
                    TiffKernels.ApplyFloat(strip, rowBytes, spp, scratch);
                }

                long before = data.WrittenCount;
                switch (comp)
                {
                    case TiffCompression.None:
                        data.Write(strip);
                        break;
                    case TiffCompression.Lzw:
                    {
                        LzwEncoder enc = CodecCache.RentTiffLzwEncoder(data);
                        enc.Write(strip);
                        enc.Finish();
                        CodecCache.Return(enc);
                        break;
                    }

                    case TiffCompression.Deflate:
                    {
                        Deflater def = CodecCache.RentDeflater(data, options.DeflateLevel, zlib: true);
                        def.Write(strip);
                        def.Finish();
                        CodecCache.Return(def);
                        break;
                    }

                    case TiffCompression.PackBits:
                        for (int r = 0; r < rows; r++)
                        {
                            int n = TiffKernels.PackBitsEncode(strip.Slice(r * rowBytes, rowBytes), packBuf);
                            data.Write(packBuf.AsSpan(0, n));
                        }

                        break;
                }

                offsets[s] = before;
                counts[s] = data.WrittenCount - before;
                if ((data.WrittenCount & 1) != 0)
                {
                    data.GetSpan(1)[0] = 0;
                    data.Advance(1);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
            if (scratch.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }

            if (packBuf.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(packBuf);
            }
        }

        // Layout of one frame: [directory + out-of-line values][strip data]. A trial pass with placeholder offsets
        // gives the directory size, which fixes where the strip data starts.
        List<TiffTreeEntry> entries = BuildEntries(request, options, frame, index, count, first, exif, comp, predictor, rps, spp, bits, isFloat, photometric, fmt);
        TiffTreeEntry offEntry = entries.Find(e => e.Tag == TiffTag.StripOffsets)!;
        TiffTreeEntry cntEntry = entries.Find(e => e.Tag == TiffTag.StripByteCounts)!;
        SetArray(offEntry, offsets, big);
        SetArray(cntEntry, counts, big);
        var trial = new TiffByteBuilder();
        TiffMetadataIo.WriteIfd(trial, pos, entries, big, false, 0);
        long dataStart = pos + ((trial.Length + 1L) & ~1L);
        long end = dataStart + data.WrittenCount;
        if (!big && end > uint.MaxValue)
        {
            throw new ImageLimitException("The TIFF is larger than 4 GiB; set TiffEncoderOptions.BigTiff.");
        }

        for (int s = 0; s < strips; s++)
        {
            offsets[s] += dataStart;
        }

        SetArray(offEntry, offsets, big);
        var final = new TiffByteBuilder();
        TiffMetadataIo.WriteIfd(final, pos, entries, big, false, index + 1 < count ? end : 0);
        final.AlignEven();
        destination.Write(final.Written);
        data.WriteTo(destination);
        return end;
    }

    private static void SetArray(TiffTreeEntry e, long[] values, bool big)
    {
        int w = big ? 8 : 4;
        byte[] v = new byte[values.Length * w];
        for (int i = 0; i < values.Length; i++)
        {
            if (big)
            {
                TiffMetadataIo.PutU64(v.AsSpan(i * 8), (ulong)values[i], false);
            }
            else
            {
                TiffMetadataIo.PutU32(v.AsSpan(i * 4), (uint)values[i], false);
            }
        }

        e.Value = v;
        e.Type = big ? 16 : 4;
        e.Count = values.Length;
    }

    private static TiffTreeEntry Shorts(int tag, params int[] values)
    {
        byte[] v = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            TiffMetadataIo.PutU16(v.AsSpan(i * 2), (ushort)values[i], false);
        }

        return new TiffTreeEntry(tag, 3, values.Length, v);
    }

    private static TiffTreeEntry Long(int tag, long value, bool big)
    {
        byte[] v = new byte[big ? 8 : 4];
        if (big)
        {
            TiffMetadataIo.PutU64(v, (ulong)value, false);
        }
        else
        {
            TiffMetadataIo.PutU32(v, (uint)value, false);
        }

        return new TiffTreeEntry(tag, big ? 16 : 4, 1, v);
    }

    private static List<TiffTreeEntry> BuildEntries(ImageEncodeRequest request, TiffEncoderOptions options, ImageFrameReader frame, int index, int count, bool first, List<TiffTreeEntry>? exif, TiffCompression comp, int predictor, int rps, int spp, int bits, bool isFloat, int photometric, PixelFormat fmt)
    {
        bool big = options.BigTiff;
        int compressionTag = comp switch
        {
            TiffCompression.None => 1,
            TiffCompression.Lzw => 5,
            TiffCompression.Deflate => 8,
            _ => 32773,
        };
        var list = new List<TiffTreeEntry>
        {
            Long(TiffTag.ImageWidth, frame.Width, false),
            Long(TiffTag.ImageLength, frame.Height, false),
            Shorts(TiffTag.BitsPerSample, Enumerable.Repeat(bits, spp).ToArray()),
            Shorts(TiffTag.Compression, compressionTag),
            Shorts(TiffTag.Photometric, photometric),
            new TiffTreeEntry(TiffTag.StripOffsets, 4, 1, new byte[4]),
            Shorts(TiffTag.SamplesPerPixel, spp),
            Long(TiffTag.RowsPerStrip, rps, false),
            new TiffTreeEntry(TiffTag.StripByteCounts, 4, 1, new byte[4]),
            Shorts(TiffTag.PlanarConfiguration, 1),
        };
        _ = big;
        if (predictor != 1)
        {
            list.Add(Shorts(TiffTag.Predictor, predictor));
        }

        if (fmt.HasAlpha)
        {
            list.Add(Shorts(TiffTag.ExtraSamples, fmt.Alpha == AlphaMode.Premultiplied ? 1 : 2));
        }

        if (isFloat)
        {
            list.Add(Shorts(TiffTag.SampleFormat, Enumerable.Repeat(3, spp).ToArray()));
        }

        if (count > 1)
        {
            list.Add(Shorts(TiffTag.PageNumber, index, count));
        }

        if (first)
        {
            ImageMetadata m = request.Metadata;
            if (m.Orientation is > 1 and <= 8)
            {
                list.Add(Shorts(TiffTag.Orientation, m.Orientation));
            }

            if (!m.IccProfile.IsEmpty)
            {
                list.Add(new TiffTreeEntry(TiffTag.IccProfile, 7, m.IccProfile.Length, m.IccProfile.ToArray()));
            }

            if (!m.Xmp.IsEmpty)
            {
                list.Add(new TiffTreeEntry(TiffTag.Xmp, 1, m.Xmp.Length, m.Xmp.ToArray()));
            }

            if (!m.Iptc.IsEmpty)
            {
                byte[] iptc = m.Iptc.ToArray();
                bool asLong = iptc.Length % 4 == 0;
                list.Add(new TiffTreeEntry(TiffTag.Iptc, asLong ? 4 : 1, asLong ? iptc.Length / 4 : iptc.Length, iptc));
            }

            if (exif is not null)
            {
                list.AddRange(exif);
            }
        }

        list.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        return list;
    }
}
