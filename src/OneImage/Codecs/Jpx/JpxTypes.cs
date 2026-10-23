namespace OneImage.Codecs.Jpx;

/// <summary>The colour space a JP2/JPX file states for its image (the enumerated values of the colour specification box).</summary>
internal enum JpxColorSpace
{
    /// <summary>The file states none (a raw codestream, or a method this decoder does not interpret).</summary>
    Unspecified = 0,

    /// <summary>sRGB (enumerated value 16).</summary>
    Srgb = 16,

    /// <summary>Greyscale (17).</summary>
    Gray = 17,

    /// <summary>sYCC (18): YCbCr with sRGB primaries. Converted to <see cref="Srgb"/> unless the caller asks otherwise.</summary>
    Sycc = 18,

    /// <summary>CMYK (12). Samples keep the file's convention: 0 means no ink.</summary>
    Cmyk = 12,

    /// <summary>CIELab (14).</summary>
    CieLab = 14,

    /// <summary>e-sRGB (20).</summary>
    ESrgb = 20,

    /// <summary>ROMM-RGB (22).</summary>
    RommRgb = 22,

    /// <summary>The colour space is described by an embedded ICC profile (see <see cref="JpxImage.IccProfile"/>).</summary>
    Icc = 1000,
}

/// <summary>What a channel carries after the channel-definition box (if any) has been applied.</summary>
internal enum JpxChannelKind
{
    /// <summary>A colour channel.</summary>
    Color = 0,

    /// <summary>An opacity (alpha) channel, 0 = transparent.</summary>
    Opacity = 1,

    /// <summary>An opacity channel whose colour channels are already multiplied by it.</summary>
    PremultipliedOpacity = 2,

    /// <summary>A channel the file does not describe.</summary>
    Unspecified = 65535,
}

/// <summary>Options of <see cref="JpxCodec.Decode(ReadOnlyMemory{byte}, JpxDecodeOptions?, CancellationToken)"/>.</summary>
internal sealed class JpxDecodeOptions
{
    /// <summary>The shared defaults.</summary>
    public static JpxDecodeOptions Default { get; } = new();

    /// <summary>
    /// Limits checked against the header before anything is allocated: pixels per frame, dimension, metadata bytes and the
    /// expansion guard (see <see cref="ImageLimits.MaxExpansionRatio"/>; blank images may still expand up to 64 MiB of samples).
    /// </summary>
    public ImageLimits Limits { get; init; } = ImageLimits.Default;

    /// <summary>
    /// How many resolution levels to drop: the result is 1/2^n of the full size per axis (rounded up), like JPEG's DCT scaling.
    /// Clamped to what every component offers; <see cref="JpxImage.Reduction"/> reports the value used. Default 0.
    /// </summary>
    public int ReductionLevels { get; init; }

    /// <summary>
    /// A region of the (reduced) output to deliver, in output pixels. Tiles that do not touch it are not decoded, so only
    /// multi-tile files get cheaper. The result has the size of the region. Default: the whole image.
    /// </summary>
    public ImageRect? Region { get; init; }

    /// <summary>
    /// When false (the default), a codestream that is cut short or damaged inside tile data yields the part that decoded (the
    /// rest stays at mid-grey or whatever the finished passes gave) and <see cref="JpxImage.Damaged"/> is set. When true any
    /// damage throws <see cref="ImageFormatException"/>. Damage in the main header always throws.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Convert sYCC images to sRGB (default true).</summary>
    public bool ConvertSycc { get; init; } = true;

    /// <summary>Expand palette (pclr/cmap) images to their colour channels (default true).</summary>
    public bool ApplyPalette { get; init; } = true;

    /// <summary>Most threads one decode may use. Output never depends on it. Default min(4, cores).</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Min(4, Environment.ProcessorCount);

    /// <summary>Most components a codestream may declare (default 256; the standard allows 16384).</summary>
    public int MaxComponents { get; init; } = 256;

    /// <summary>Most bytes of sample planes plus per-tile coefficient buffers a decode may hold (default 1 GiB).</summary>
    public long MaxWorkingBytes { get; init; } = 1L << 30;

    /// <summary>Most code-blocks or precincts one tile may declare (default 2^21).</summary>
    public int MaxCodeBlocksPerTile { get; init; } = 1 << 21;
}

/// <summary>Header-level facts about a JPEG 2000 file, available without decoding any tile.</summary>
internal sealed class JpxInfo
{
    /// <summary>Width of the image area on the reference grid.</summary>
    public int Width { get; init; }

    /// <summary>Height of the image area on the reference grid.</summary>
    public int Height { get; init; }

    /// <summary>Number of codestream components.</summary>
    public int Components { get; init; }

    /// <summary>Bit depth of each codestream component.</summary>
    public int[] BitDepths { get; init; } = [];

    /// <summary>Whether each codestream component holds signed samples.</summary>
    public bool[] Signed { get; init; } = [];

    /// <summary>Number of tiles.</summary>
    public int Tiles { get; init; }

    /// <summary>The most resolution levels that can be dropped (the smallest decomposition depth over components).</summary>
    public int MaxReduction { get; init; }

    /// <summary>Quality layers of the main-header coding style.</summary>
    public int Layers { get; init; }

    /// <summary>The colour space the file states.</summary>
    public JpxColorSpace ColorSpace { get; init; }

    /// <summary>Whether the file has a palette box.</summary>
    public bool HasPalette { get; init; }

    /// <summary>Whether the data was wrapped in a JP2/JPX file (false for a raw codestream).</summary>
    public bool IsFileFormat { get; init; }
}

/// <summary>
/// A decoded JPEG 2000 image: one plane of unsigned 16-bit samples per output channel, all on the same grid.
/// </summary>
/// <remarks>
/// <para>Channels are ordered colour first (in the order the colour space defines), then at most one opacity channel
/// (see <see cref="AlphaChannel"/>). Components that were subsampled in the codestream are replicated to the full grid.
/// Signed components are shifted to unsigned by adding 2^(depth-1); <see cref="Signed"/> says which.</para>
/// <para>A sample of channel <c>c</c> lies in <c>0 .. 2^BitDepths[c]-1</c>. Planes are row-major, <see cref="Width"/> samples
/// per row, no padding.</para>
/// </remarks>
internal sealed class JpxImage
{
    internal JpxImage(int width, int height, ushort[][] planes, int[] bitDepths, bool[] signed)
    {
        Width = width;
        Height = height;
        Planes = planes;
        BitDepths = bitDepths;
        Signed = signed;
    }

    /// <summary>Pixels per row of the output (after any reduction or region).</summary>
    public int Width { get; }

    /// <summary>Rows of the output.</summary>
    public int Height { get; }

    /// <summary>Number of channels, colour plus opacity.</summary>
    public int ChannelCount => Planes.Length;

    /// <summary>Samples of each channel, row-major.</summary>
    public ushort[][] Planes { get; }

    /// <summary>Bits per sample of each channel (1..16).</summary>
    public int[] BitDepths { get; }

    /// <summary>True for channels whose samples were signed in the file (now offset to unsigned).</summary>
    public bool[] Signed { get; }

    /// <summary>The colour space the file states, <see cref="JpxColorSpace.Unspecified"/> when none.</summary>
    public JpxColorSpace ColorSpace { get; internal set; }

    /// <summary>The embedded ICC profile when <see cref="ColorSpace"/> is <see cref="JpxColorSpace.Icc"/>; otherwise empty.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; internal set; }

    /// <summary>Number of colour channels (the first <see cref="ColorChannelCount"/> planes).</summary>
    public int ColorChannelCount { get; internal set; }

    /// <summary>Index of the opacity channel, or -1.</summary>
    public int AlphaChannel { get; internal set; } = -1;

    /// <summary>True when the colour channels are already multiplied by the opacity channel.</summary>
    public bool AlphaPremultiplied { get; internal set; }

    /// <summary>Resolution levels dropped from the full size.</summary>
    public int Reduction { get; internal set; }

    /// <summary>True when the codestream was cut short or damaged and only part of it decoded.</summary>
    public bool Damaged { get; internal set; }

    /// <summary>True when the multiple-component transform (and sYCC conversion, if requested) already ran.</summary>
    public bool ConvertedFromSycc { get; internal set; }

    /// <summary>Interleaves the channels into bytes: 8-bit samples when every channel is at most 8 bits deep after scaling, else big-endian 16-bit.</summary>
    /// <param name="bitsPerSample">8 or 16. Samples are scaled to that depth by bit replication (down-scaling drops low bits).</param>
    /// <param name="channels">How many leading channels to emit (0 for all).</param>
    /// <returns>Rows of <c>Width * channels * bitsPerSample / 8</c> bytes.</returns>
    public byte[] ToInterleaved(int bitsPerSample, int channels = 0)
    {
        if (bitsPerSample is not (8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        int n = channels <= 0 ? ChannelCount : Math.Min(channels, ChannelCount);
        int bytes = bitsPerSample / 8;
        long total = (long)Width * Height * n * bytes;
        if (total > Array.MaxLength)
        {
            throw new ImageLimitException("Output too large.");
        }

        byte[] result = new byte[total];
        int count = Width * Height;
        for (int c = 0; c < n; c++)
        {
            ushort[] plane = Planes[c];
            int depth = BitDepths[c];
            int dst = c * bytes;
            int stride = n * bytes;
            if (bitsPerSample == 8)
            {
                if (depth == 8)
                {
                    for (int i = 0; i < count; i++, dst += stride)
                    {
                        result[dst] = (byte)plane[i];
                    }
                }
                else if (depth > 8)
                {
                    int shift = depth - 8;
                    for (int i = 0; i < count; i++, dst += stride)
                    {
                        result[dst] = (byte)(plane[i] >> shift);
                    }
                }
                else
                {
                    int max = (1 << depth) - 1;
                    for (int i = 0; i < count; i++, dst += stride)
                    {
                        result[dst] = (byte)((plane[i] * 255 + (max >> 1)) / max);
                    }
                }
            }
            else
            {
                int max = (1 << depth) - 1;
                for (int i = 0; i < count; i++, dst += stride)
                {
                    int v = depth == 16 ? plane[i] : (plane[i] * 65535 + (max >> 1)) / max;
                    result[dst] = (byte)(v >> 8);
                    result[dst + 1] = (byte)v;
                }
            }
        }

        return result;
    }
}
