using System.Buffers;

namespace OneImage;

internal sealed partial class EqualizeNode
{
    // Window edge handling measured against ImageSharp: left and top reflect without repeating the edge pixel; the right
    // edge repeats the edge pixel; the bottom reflects with the edge pixel repeated.
    private static int MapLeadingAxis(int v, int n, bool repeatEdgeBeyondEnd, bool reflectBeyondEnd)
    {
        if (v < 0)
        {
            v = -v;
        }
        else if (v >= n)
        {
            v = reflectBeyondEnd ? (2 * n) - 1 - v : (repeatEdgeBeyondEnd ? n - 1 : v);
        }

        return v < 0 ? 0 : (v >= n ? n - 1 : v);
    }

    private Tile ComputeWindowTile(ImageRect o, RenderContext context)
    {
        int s = _window;
        int half = s / 2;
        int winRows = 2 * half;
        long n = (long)s * s;
        int w = Width;
        int h = Height;
        int levels = _levels;

        // Source pixels that any window of this tile can reach (reflection stays inside it, see the maps below).
        int nx0 = Math.Max(0, o.X - half);
        int nx1 = Math.Min(w, o.Right + s);
        int ny0 = Math.Max(0, o.Y - half);
        int ny1 = Math.Min(h, o.Bottom + s);
        int needW = nx1 - nx0;
        int needH = ny1 - ny0;
        int padW = o.Width + s - 1;
        int padH = o.Height + winRows - 1;
        int wbpp = _wbpp;
        int obpp = Format.BytesPerPixel;
        bool convert = _work != Format;

        byte[] region = ArrayPool<byte>.Shared.Rent(needW * needH * wbpp);
        ushort[] regionBins = ArrayPool<ushort>.Shared.Rent(needW * needH);
        int[] colIdx = ArrayPool<int>.Shared.Rent(padW);
        int[] rowOff = ArrayPool<int>.Shared.Rent(padH);
        int[] hist = ArrayPool<int>.Shared.Rent(levels);
        int[] clipScratch = _clip ? ArrayPool<int>.Shared.Rent(levels) : [];
        int[] vals = ArrayPool<int>.Shared.Rent(o.Width);
        ushort[] ownBins = ArrayPool<ushort>.Shared.Rent(o.Width);
        byte[]? convRow = convert ? ArrayPool<byte>.Shared.Rent(o.Width * wbpp) : null;
        Tile tile = Tile.Rent(Tile.ByteSize(Format), clear: false);
        try
        {
            Render.ReadRegion(Inputs[0], new ImageRect(nx0, ny0, needW, needH), region, needW * wbpp, _work, context);
            for (int y = 0; y < needH; y++)
            {
                FillBins(region.AsSpan(y * needW * wbpp, needW * wbpp), needW, regionBins.AsSpan(y * needW, needW));
            }

            for (int i = 0; i < padW; i++)
            {
                colIdx[i] = Math.Clamp(MapLeadingAxis(o.X - half + i, w, repeatEdgeBeyondEnd: true, reflectBeyondEnd: false) - nx0, 0, needW - 1);
            }

            for (int j = 0; j < padH; j++)
            {
                rowOff[j] = Math.Clamp(MapLeadingAxis(o.Y - half + j, h, repeatEdgeBeyondEnd: false, reflectBeyondEnd: true) - ny0, 0, needH - 1) * needW;
            }

            Array.Clear(hist, 0, levels);
            for (int r = 0; r < winRows; r++)
            {
                for (int c = 0; c < s; c++)
                {
                    hist[regionBins[rowOff[r] + colIdx[c]]]++;
                }
            }

            int ow = o.Width;
            bool rightward = true;
            for (int yl = 0; yl < o.Height; yl++)
            {
                // The pixel's own bin comes from the dividing luminance variant, the window counts from the multiplying one.
                FillBins(region.AsSpan((((o.Y + yl - ny0) * needW) + (o.X - nx0)) * wbpp, ow * wbpp), ow, ownBins, divide: true);
                if (rightward)
                {
                    for (int xl = 0; xl < ow; xl++)
                    {
                        vals[xl] = Quantize(Query(hist, ownBins[xl], n, winRows * s, clipScratch));
                        if (xl + 1 < ow)
                        {
                            AddColumn(hist, regionBins, rowOff, colIdx[xl], yl, winRows, -1);
                            AddColumn(hist, regionBins, rowOff, colIdx[xl + s], yl, winRows, 1);
                        }
                    }
                }
                else
                {
                    for (int xl = ow - 1; xl >= 0; xl--)
                    {
                        vals[xl] = Quantize(Query(hist, ownBins[xl], n, winRows * s, clipScratch));
                        if (xl > 0)
                        {
                            AddColumn(hist, regionBins, rowOff, colIdx[xl + s - 1], yl, winRows, -1);
                            AddColumn(hist, regionBins, rowOff, colIdx[xl - 1], yl, winRows, 1);
                        }
                    }
                }

                // Move the window one row down at the column where this row ended.
                if (yl + 1 < o.Height)
                {
                    int xe = rightward ? ow - 1 : 0;
                    int offOld = rowOff[yl];
                    int offNew = rowOff[yl + winRows];
                    for (int c = 0; c < s; c++)
                    {
                        int ci = colIdx[xe + c];
                        hist[regionBins[offOld + ci]]--;
                        hist[regionBins[offNew + ci]]++;
                    }
                }

                rightward = !rightward;

                // Source pixels of this output row, for the alpha channel.
                int srcRow = ((o.Y + yl - ny0) * needW) + (o.X - nx0);
                Span<byte> dst = convert ? convRow.AsSpan(0, ow * wbpp) : tile.Span.Slice(yl * Tile.Size * obpp, ow * obpp);
                StoreRow(dst, region.AsSpan(srcRow * wbpp, ow * wbpp), vals, ow);
                if (convert)
                {
                    Kernels.ConvertKernel.ConvertRow(dst, _work, tile.Span.Slice(yl * Tile.Size * obpp, ow * obpp), Format, ow);
                }
            }

            return tile;
        }
        catch
        {
            tile.Release();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(region);
            ArrayPool<ushort>.Shared.Return(regionBins);
            ArrayPool<int>.Shared.Return(colIdx);
            ArrayPool<int>.Shared.Return(rowOff);
            ArrayPool<int>.Shared.Return(hist);
            if (_clip)
            {
                ArrayPool<int>.Shared.Return(clipScratch);
            }

            ArrayPool<int>.Shared.Return(vals);
            ArrayPool<ushort>.Shared.Return(ownBins);
            if (convRow != null)
            {
                ArrayPool<byte>.Shared.Return(convRow);
            }
        }
    }

    private static void AddColumn(int[] hist, ushort[] bins, int[] rowOff, int col, int firstRow, int rows, int sign)
    {
        for (int r = 0; r < rows; r++)
        {
            hist[bins[rowOff[firstRow + r] + col]] += sign;
        }
    }

    /// <summary>
    /// The mapped value (sample units) of a pixel in <paramref name="bin"/> given its window histogram. <paramref name="n"/> is the
    /// count the histogram stands for (the window area); <paramref name="counted"/> is how many pixels the bins really hold.
    /// </summary>
    private float Query(int[] histogram, int bin, long n, int counted, int[] clipScratch)
    {
        ReadOnlySpan<int> hist = histogram.AsSpan(0, _levels);
        double cdf;
        double min;
        if (!_clip)
        {
            int first = 0;
            while (hist[first] == 0)
            {
                first++;
            }

            min = hist[first];
            // Sum the shorter side of the histogram.
            if (bin <= _levels / 2)
            {
                long sum = 0;
                for (int i = 0; i <= bin; i++)
                {
                    sum += hist[i];
                }

                cdf = sum;
            }
            else
            {
                long above = 0;
                for (int i = bin + 1; i < hist.Length; i++)
                {
                    above += hist[i];
                }

                cdf = counted - above;
            }
        }
        else
        {
            Span<int> work = clipScratch.AsSpan(0, _levels);
            hist.CopyTo(work);
            ClipAndRedistribute(work, _limit);
            long sum = 0;
            for (int i = 0; i <= bin; i++)
            {
                sum += work[i];
            }

            int first = 0;
            while (work[first] == 0)
            {
                first++;
            }

            cdf = sum;
            min = work[first];
        }

        return n <= min ? 0f : (float)(Math.Max(0, cdf - min) / (n - min) * _max);
    }
}
