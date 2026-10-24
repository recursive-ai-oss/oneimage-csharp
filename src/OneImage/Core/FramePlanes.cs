using OneImage.Kernels;

namespace OneImage;

/// <summary>
/// What an <see cref="ImageFrame"/> keeps so that repeated region reads of a sequential decode cost a copy instead of a decode.
/// <para>
/// A frame whose pixels come from a streaming sequential decode (a JPEG or PNG of at least <see cref="DecodeSourceNode.StreamingMinBytes"/>
/// in its own format) only holds a sliding window of decoded rows, so reading a region it has already moved past decodes the image
/// again. From the second region read on, the frame instead keeps a <see cref="Plane"/>: the rows read so far, filled strictly top
/// to bottom so the decoder only ever runs forward and never decodes a row twice. The first plane is in the format the caller asked
/// for (a Gray8 plane is a third of an RGB frame). If a later read asks for another format, the frame switches once to a plane in its
/// own format and converts rows as they are read, as a materialized decode would. A plane is one array of exactly its size (the
/// large object heap frees and reuses such arrays promptly), bounded by <see cref="ImageEngine.RegionCacheBytes"/> per frame.
/// </para>
/// </summary>
internal sealed class FramePlanes
{
    /// <summary>Tile rows read per step of a fill (the streaming window moves in the same steps).</summary>
    private const int ChunkRows = 2;

    /// <summary>Planes a frame builds at most: a frame read in many formats that never fit together stops caching instead of decoding again for each.</summary>
    private const int MaxCreations = 3;

    private readonly Node _node;
    private readonly object _gate = new();
    private readonly List<Plane> _planes = [];
    private int _reads;
    private int _creations;
    private int _applies;
    private List<DecodeSourceNode>? _sources;

    public FramePlanes(Node node) => _node = node;

    /// <summary>Gets a value indicating whether this frame's pixels come from a streaming sequential decode, the only case planes serve.</summary>
    public bool Applies
    {
        get
        {
            int state = Volatile.Read(ref _applies);
            if (state == 0)
            {
                List<DecodeSourceNode> found = Node.DecodeSources(_node);
                bool streaming = _node.Streaming && found.Exists(d => d.IsStreamingDecode);
                _sources = found;
                state = streaming ? 1 : 2;
                Volatile.Write(ref _applies, state);
            }

            return state == 1;
        }
    }

    /// <summary>
    /// Serves a region read from a plane, creating the plane on the second region read of the frame. Returns false, touching
    /// nothing, when this read should go through the normal path (first read, a frame bigger than the budget, planes off).
    /// </summary>
    public bool TryRead(PixelFormat format, ImageRect rect, Span<byte> destination, RenderContext context, bool create = true)
    {
        if (!Applies)
        {
            return false;
        }

        // A caller that opted out of planes still reads from one that already exists, but neither counts toward nor builds a new one.
        int reads = create ? Interlocked.Increment(ref _reads) : 0;
        Plane plane;
        lock (_gate)
        {
            Plane? found = FindLocked(format);
            if (found == null)
            {
                if (!create || reads < 2 || _creations >= MaxCreations)
                {
                    return false;
                }

                found = CreateLocked(format);
                if (found == null)
                {
                    return false;
                }
            }

            plane = found;
        }

        plane.EnsureFilled(rect.Bottom, context);
        plane.CopyTo(rect, destination, format);
        return true;
    }

    /// <summary>Serves a whole-frame read from a finished plane; false when there is none.</summary>
    public bool TryReadWhole(PixelFormat format, Span<byte> destination)
    {
        Plane? plane;
        lock (_gate)
        {
            plane = FindLocked(format);
        }

        if (plane == null || !plane.Complete)
        {
            return false;
        }

        plane.CopyTo(new ImageRect(0, 0, _node.Width, _node.Height), destination, format);
        return true;
    }

    /// <summary>Gets the bytes of the planes held now.</summary>
    public long PlaneBytes
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (Plane p in _planes)
                {
                    total += p.Bytes;
                }

                return total;
            }
        }
    }

    /// <summary>Drops every plane (a reader in the middle of a copy keeps its own reference) and starts counting reads again.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _planes.Clear();
            _creations = 0;
            Volatile.Write(ref _reads, 0);
        }
    }

    // A plane in the requested format, else the one in the frame's own format (rows are converted as they are copied out).
    private Plane? FindLocked(PixelFormat format)
    {
        Plane? own = null;
        foreach (Plane p in _planes)
        {
            if (p.Format == format)
            {
                return p;
            }

            if (p.Format == _node.Format)
            {
                own = p;
            }
        }

        return own;
    }

    // Builds the plane for a read in `format`: in that format if it is the frame's first (or the frame's own), otherwise in the
    // frame's own format, which serves every format afterwards. Null when it does not fit the budget.
    private Plane? CreateLocked(PixelFormat format)
    {
        long budget = ImageEngine.RegionCacheBytes;
        PixelFormat chosen = _planes.Count == 0 && _creations == 0 ? format : _node.Format;
        long bytes = (long)_node.Width * _node.Height * chosen.BytesPerPixel;
        if (bytes > budget || bytes > Array.MaxLength)
        {
            return null;
        }

        // A plane is always replaced by one that can serve at least what it served: drop the old ones.
        _planes.Clear();
        _creations++;
        var plane = new Plane(_node, _sources!, chosen);
        _planes.Add(plane);
        return plane;
    }

    /// <summary>The rows of one frame read so far in one pixel format, filled from the top on demand.</summary>
    private sealed class Plane
    {
        private readonly Node _source;
        private readonly List<DecodeSourceNode> _sources;
        private readonly byte[] _data;
        private readonly int _stride;
        private readonly object _fill = new();
        private int _filledRows;

        public Plane(Node source, List<DecodeSourceNode> sources, PixelFormat format)
        {
            _source = source;
            _sources = sources;
            Format = format;
            _stride = checked(source.Width * format.BytesPerPixel);

            // Untouched pages of a fresh array are not resident, so the plane costs only what has been filled.
            _data = GC.AllocateUninitializedArray<byte>(checked(_stride * source.Height));
        }

        public PixelFormat Format { get; }

        public long Bytes => _data.Length;

        public bool Complete => Volatile.Read(ref _filledRows) >= _source.Height;

        /// <summary>Makes sure rows above <paramref name="bottom"/> hold pixels, decoding forward from the first unfilled row.</summary>
        public void EnsureFilled(int bottom, RenderContext context)
        {
            int need = Math.Min(_source.Height, ((bottom + Tile.Size - 1) >> Tile.Shift) << Tile.Shift);
            if (Volatile.Read(ref _filledRows) >= need)
            {
                return;
            }

            lock (_fill)
            {
                if (_filledRows == 0)
                {
                    // The first reads went through the sliding window, which has moved on; start a fresh pass at the top.
                    foreach (DecodeSourceNode d in _sources)
                    {
                        d.BeginForwardPass();
                    }
                }

                RenderContext fillContext = context.WithRetainDecode();
                while (_filledRows < need || (_filledRows < _source.Height && AnyRetaining()))
                {
                    // Once the decode keeps its rows (a rotated image needs the whole decode before its first tile), finishing the
                    // plane now costs nothing more and lets the decoded rows go.
                    int first = _filledRows;
                    int rows = Math.Min(ChunkRows * Tile.Size, _source.Height - first);
                    Render.ReadRegion(_source, new ImageRect(0, first, _source.Width, rows), _data.AsSpan(first * _stride, rows * _stride), _stride, Format, fillContext);
                    Volatile.Write(ref _filledRows, first + rows);
                }

                if (_filledRows >= _source.Height)
                {
                    foreach (DecodeSourceNode d in _sources)
                    {
                        d.ReleaseDecoded();
                    }
                }
            }
        }

        /// <summary>Copies a rectangle of filled rows, tightly packed and converted to <paramref name="destinationFormat"/> if it differs, into <paramref name="destination"/>.</summary>
        public void CopyTo(ImageRect rect, Span<byte> destination, PixelFormat destinationFormat)
        {
            int bpp = Format.BytesPerPixel;
            int dbpp = destinationFormat.BytesPerPixel;
            int rowBytes = rect.Width * dbpp;
            ConvertRowKernel? converter = destinationFormat != Format ? KernelRegistry.Get<ConvertRowKernel>("convert") : null;
            for (int y = 0; y < rect.Height; y++)
            {
                ReadOnlySpan<byte> source = _data.AsSpan(((rect.Y + y) * _stride) + (rect.X * bpp), rect.Width * bpp);
                Span<byte> row = destination.Slice(y * rowBytes, rowBytes);
                if (converter == null)
                {
                    source.CopyTo(row);
                }
                else
                {
                    converter(source, Format, row, destinationFormat, rect.Width);
                }
            }
        }

        private bool AnyRetaining()
        {
            foreach (DecodeSourceNode d in _sources)
            {
                if (d.Retaining)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
