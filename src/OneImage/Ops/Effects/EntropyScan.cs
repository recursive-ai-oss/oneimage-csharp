using System.Buffers;
using System.Runtime.ExceptionServices;

namespace OneImage;

/// <summary>Finds the box of pixels whose edge strength reaches a threshold, for <see cref="Image.EntropyCrop"/>.</summary>
internal static class EntropyScan
{
    private const int Band = Tile.Size;

    public static ImageRect Find(Node node, float threshold, RenderContext context)
    {
        Node edges = new EdgeDetectNode(node, EdgeDetector.Sobel, grayscale: false, EdgeMode.Clamp);
        int bands = (edges.Height + Band - 1) / Band;
        var boxes = new (int MinX, int MaxX, int MinY, int MaxY)[bands];
        if (edges.Streaming || context.Degree <= 1 || bands == 1)
        {
            // A streaming source has to be read top to bottom.
            for (int b = 0; b < bands; b++)
            {
                boxes[b] = ScanBand(edges, b * Band, threshold, context);
            }
        }
        else
        {
            RenderContext nested = context.Nested;
            var options = new ParallelOptions { MaxDegreeOfParallelism = context.Degree, CancellationToken = context.CancellationToken };
            try
            {
                Parallel.For(0, bands, options, b => boxes[b] = ScanBand(edges, b * Band, threshold, nested));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                throw;
            }
        }

        int minX = int.MaxValue;
        int maxX = -1;
        int minY = int.MaxValue;
        int maxY = -1;
        foreach ((int bx0, int bx1, int by0, int by1) in boxes)
        {
            if (bx1 >= 0)
            {
                minX = Math.Min(minX, bx0);
                maxX = Math.Max(maxX, bx1);
                minY = Math.Min(minY, by0);
                maxY = Math.Max(maxY, by1);
            }
        }

        return maxX < 0 ? new ImageRect(0, 0, 0, 0) : new ImageRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // The bounding box of the detail pixels in the 128 rows starting at y (MaxX = -1 when there are none).
    private static (int MinX, int MaxX, int MinY, int MaxY) ScanBand(Node edges, int y, float threshold, RenderContext context)
    {
        int w = edges.Width;
        int rows = Math.Min(Band, edges.Height - y);
        byte[] buf = Pools<byte>.Pool.Rent(w * rows * 4);
        try
        {
            Render.ReadRegion(edges, new ImageRect(0, y, w, rows), buf, w * 4, PixelFormat.Rgba8, context);

            // Compared as an 8-bit luma, like thresholding an 8-bit picture of the edges.
            float level = threshold * 255f;
            int minX = int.MaxValue;
            int maxX = -1;
            int minY = int.MaxValue;
            int maxY = -1;
            for (int r = 0; r < rows; r++)
            {
                ReadOnlySpan<byte> row = buf.AsSpan(r * w * 4, w * 4);
                for (int x = 0; x < w; x++)
                {
                    int luma = (int)((0.2126f * row[x * 4]) + (0.7152f * row[(x * 4) + 1]) + (0.0722f * row[(x * 4) + 2]) + 0.5f);
                    if (luma >= level)
                    {
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y + r);
                        maxY = Math.Max(maxY, y + r);
                    }
                }
            }

            return (minX, maxX, minY, maxY);
        }
        finally
        {
            Pools<byte>.Pool.Return(buf);
        }
    }
}
