using System.Runtime.ExceptionServices;

namespace OneImage;

/// <summary>
/// One top-to-bottom decode pass of a sequential decoder, run on its own thread and handed to consumers a tile row at a time
/// through a small sliding window, so memory depends on image width and not height.
/// <para>
/// The decoder pushes rows into a <see cref="StripToTileBuffer"/>, which publishes finished tile rows here. The window keeps the
/// rows from <see cref="Keep"/> rows behind the furthest row any consumer has asked for up to <see cref="Ahead"/> rows beyond it.
/// The decoder thread blocks when it would run further ahead, so it advances in lock step with the consumer.
/// A consumer asking for a row that already left the window gets a miss and the owner decides: start a new pass or give up
/// streaming (see <see cref="DecodeSourceNode"/>).
/// </para>
/// </summary>
internal sealed class StreamingSession : ITileRowReceiver
{
    /// <summary>Tile rows kept behind the furthest requested row. Covers a render chunk of two tile rows plus the extra row a shifted crop reads.</summary>
    public const int Keep = 4;

    /// <summary>Tile rows the decoder may run ahead of the furthest requested row.</summary>
    public const int Ahead = 1;

    private static int s_liveThreads;

    private readonly object _gate = new();
    private readonly Dictionary<int, Tile[]> _rows = [];
    private readonly DecodeSession _decode;
    private readonly int _frame;
    private readonly int _scale;
    private readonly ImageRect _request;
    private readonly int _width;
    private readonly int _height;
    private readonly PixelFormat _format;
    private readonly int _tilesY;
    private readonly bool _retain;
    private int _maxRequested;
    private int _lowest;
    private int _nextPublish;
    private volatile bool _aborted;
    private ExceptionDispatchInfo? _fault;

    public StreamingSession(DecodeSession decode, int frame, int scale, ImageRect request, int width, int height, PixelFormat format, int startRow, bool retain = false)
    {
        _retain = retain;
        _decode = decode;
        _frame = frame;
        _scale = scale;
        _request = request;
        _width = width;
        _height = height;
        _format = format;
        _tilesY = (height + Tile.Size - 1) >> Tile.Shift;
        _maxRequested = startRow;
        Interlocked.Increment(ref s_liveThreads);
        new Thread(Run) { IsBackground = true, Name = "OneImage sequential decode" }.Start();
    }

    /// <summary>
    /// Gets a value indicating whether this pass keeps every row it has decoded (so no consumer ever misses), instead of a sliding
    /// window. The decoder still runs only as far as the furthest row asked for.
    /// </summary>
    public bool Retaining => _retain;

    /// <summary>Gets the number of decode threads currently running (test statistic).</summary>
    public static int LiveThreads => Volatile.Read(ref s_liveThreads);

    /// <summary>
    /// Returns a retained tile of the window, waiting for the decoder if the row is not decoded yet; null when the row already
    /// left the window (or the session was abandoned), meaning the caller must start another pass.
    /// </summary>
    public Tile? TryGetTile(int tx, int ty, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_rows.TryGetValue(ty, out Tile[]? row))
                {
                    return row[tx].Retain();
                }

                if (_aborted)
                {
                    return null;
                }

                if (ty < _nextPublish)
                {
                    return null;
                }

                _fault?.Throw();
                if (ty > _maxRequested)
                {
                    _maxRequested = ty;
                    EvictLocked();
                    Monitor.PulseAll(_gate);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 50);
            }
        }
    }

    /// <summary>Stops the decoder thread and drops the window. Tiles consumers still hold stay valid.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            if (_aborted)
            {
                return;
            }

            _aborted = true;
            foreach (Tile[] row in _rows.Values)
            {
                ReleaseAll(row);
            }

            _rows.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Called on the decoder thread with each finished tile row; blocks while the decoder is too far ahead.</summary>
    void ITileRowReceiver.Accept(int ty, Tile[] tiles)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_aborted)
                {
                    ReleaseAll(tiles);
                    throw new OperationCanceledException();
                }

                if (!_retain && ty < _maxRequested - Keep)
                {
                    // Nobody will ask for this row any more (a pass restarted further down the image).
                    ReleaseAll(tiles);
                    _nextPublish = ty + 1;
                    return;
                }

                if (ty <= _maxRequested + Ahead)
                {
                    _rows[ty] = tiles;
                    _nextPublish = ty + 1;
                    Monitor.PulseAll(_gate);
                    return;
                }

                Monitor.Wait(_gate);
            }
        }
    }

    private static void ReleaseAll(Tile[] row)
    {
        foreach (Tile t in row)
        {
            t.Release();
        }
    }

    private void EvictLocked()
    {
        if (_retain)
        {
            return;
        }

        int floor = _maxRequested - Keep;
        for (; _lowest < floor && _lowest < _tilesY; _lowest++)
        {
            if (_rows.Remove(_lowest, out Tile[]? row))
            {
                ReleaseAll(row);
            }
        }
    }

    private void Run()
    {
        try
        {
            using Stream stream = _decode.Source.Open();
            using var sink = new StripToTileBuffer(_width, _height, _format, this);
            _decode.Decoder.DecodeFrame(stream, _decode.Info, _frame, new DecodeRequest(_request, _scale), sink, _decode.Limits, CancellationToken.None);
            if (sink.RowsWritten != _height)
            {
                throw new ImageFormatException($"Image data ended after {sink.RowsWritten} of {_height} rows.");
            }
        }
        catch (OperationCanceledException) when (_aborted)
        {
            // Aborted on purpose: nobody is waiting for this pass any more.
        }
        catch (Exception ex)
        {
            // This thread has no caller to throw to: hand the original exception to whichever consumer waits for the missing rows.
            lock (_gate)
            {
                if (!_aborted)
                {
                    _fault = ExceptionDispatchInfo.Capture(ex);
                }

                Monitor.PulseAll(_gate);
            }
        }
        finally
        {
            Interlocked.Decrement(ref s_liveThreads);
        }
    }
}

/// <summary>
/// Owned only by the node that streams, so when the node becomes unreachable its finalizer stops the decoder thread (which
/// references the session but not the node) instead of leaving it blocked forever.
/// </summary>
internal sealed class StreamingSessionHandle : IDisposable
{
    public StreamingSessionHandle(StreamingSession session) => Session = session;

    public StreamingSession Session { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    ~StreamingSessionHandle() => Dispose(disposing: false);

    private void Dispose(bool disposing)
    {
        _ = disposing;
        Session.Abort();
    }
}
