namespace OneImage.Drawing;

/// <summary>Receives the dashes cut from a polyline.</summary>
internal interface IDashSink
{
    /// <summary>
    /// One dash: <paramref name="count"/> points of <paramref name="xy"/> (interleaved). A dash of zero length has one point; its
    /// direction (for square caps) is <paramref name="dirX"/>, <paramref name="dirY"/>. A <paramref name="closed"/> piece is the whole figure, undashed.
    /// </summary>
    void Dash(double[] xy, int count, bool closed, double dirX, double dirY);
}

/// <summary>
/// Cuts a polyline into dashes by walking along it with a repeating on/off pattern. A closed figure that is "on" at its start
/// and at its end gets one dash through the start point, not two touching ones.
/// </summary>
internal sealed class DashSplitter
{
    /// <summary>The most on/off switches one stroke may make; beyond it the stroke fails with <see cref="ImageLimitException"/>.</summary>
    public const int MaxDashes = 1 << 22;

    private readonly double[] _pattern;
    private readonly double _period;
    private readonly double _offset;
    private int _switches;
    private double[] _cur = new double[32];
    private int _curCount;
    private double[]? _first;
    private int _firstCount;
    private double _firstDx, _firstDy;

    public DashSplitter(ReadOnlySpan<float> pattern, double offset)
    {
        // An odd-length pattern repeats once so on and off alternate (SVG).
        int n = pattern.Length % 2 == 1 ? pattern.Length * 2 : pattern.Length;
        _pattern = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            _pattern[i] = pattern[i % pattern.Length];
            sum += _pattern[i];
        }

        _period = sum;
        double o = offset % sum;
        _offset = o < 0 ? o + sum : o;
    }

    /// <summary>Splits one figure. <paramref name="xy"/> holds <paramref name="n"/> distinct consecutive points; a closed figure has an implicit last segment back to the first.</summary>
    public void Split(double[] xy, int n, bool closed, IDashSink sink)
    {
        _curCount = 0;
        _first = null;
        _firstTaken = false;
        int idx = 0;
        double remaining = _pattern[0];
        double skip = _offset;
        // Advance into the pattern by the offset; entries of zero length are passed over.
        while (skip > 0 && skip >= remaining)
        {
            skip -= remaining;
            idx = (idx + 1) % _pattern.Length;
            remaining = _pattern[idx];
            if (skip == 0 && remaining == 0)
            {
                break;
            }
        }

        remaining -= skip;
        bool on = (idx & 1) == 0;
        bool startedOn = on;
        bool everOff = false;
        double dx = 0, dy = 0;
        if (on)
        {
            Begin(xy[0], xy[1]);
        }

        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % n;
            double ax = xy[2 * i], ay = xy[(2 * i) + 1], bx = xy[2 * j], by = xy[(2 * j) + 1];
            double len = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            dx = (bx - ax) / len;
            dy = (by - ay) / len;
            double pos = 0;
            while (len - pos > remaining)
            {
                pos += remaining;
                double px = ax + (dx * pos), py = ay + (dy * pos);
                if (++_switches > MaxDashes)
                {
                    throw new ImageLimitException($"The dash pattern cuts the stroke into more than {MaxDashes} dashes.");
                }

                if (on)
                {
                    Append(px, py);
                    Finish(sink, closed, startedOn, dx, dy, last: false);
                    everOff = true;
                }
                else
                {
                    Begin(px, py);
                }

                on = !on;
                idx = (idx + 1) % _pattern.Length;
                remaining = _pattern[idx];
            }

            remaining -= len - pos;
            if (on)
            {
                Append(bx, by);
            }
        }

        if (on)
        {
            if (closed && !everOff)
            {
                sink.Dash(_cur, _curCount, true, dx, dy);
            }
            else if (closed && startedOn && _first != null)
            {
                // The last dash runs into the first through the start point.
                int total = _curCount + _firstCount - 1;
                var merged = new double[2 * total];
                Array.Copy(_cur, merged, 2 * _curCount);
                Array.Copy(_first, 2, merged, 2 * _curCount, 2 * (_firstCount - 1));
                sink.Dash(merged, total, false, dx, dy);
                _first = null;
            }
            else
            {
                Finish(sink, closed, startedOn, dx, dy, last: true);
            }
        }

        if (_first != null)
        {
            sink.Dash(_first, _firstCount, false, _firstDx, _firstDy);
            _first = null;
        }
    }

    private void Begin(double x, double y)
    {
        _curCount = 0;
        Append(x, y);
    }

    private void Append(double x, double y)
    {
        if (2 * (_curCount + 1) > _cur.Length)
        {
            Array.Resize(ref _cur, _cur.Length * 2);
        }

        _cur[2 * _curCount] = x;
        _cur[(2 * _curCount) + 1] = y;
        _curCount++;
    }

    // Ends the current dash. In a closed figure the dash that began at the start is held back: the last dash may continue it.
    private void Finish(IDashSink sink, bool closed, bool startedOn, double dx, double dy, bool last)
    {
        bool isFirst = closed && startedOn && _first == null && !_firstTaken;
        if (isFirst && !last)
        {
            _first = new double[2 * _curCount];
            Array.Copy(_cur, _first, 2 * _curCount);
            _firstCount = _curCount;
            _firstDx = dx;
            _firstDy = dy;
            _firstTaken = true;
            return;
        }

        sink.Dash(_cur, _curCount, false, dx, dy);
    }

    private bool _firstTaken;
}
