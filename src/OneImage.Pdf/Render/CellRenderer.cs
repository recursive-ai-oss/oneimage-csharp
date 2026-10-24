using System.Buffers;
using OnePdf;

namespace OneImage.Pdf.Render;

/// <summary>
/// Draws a rectangle of a page by drawing fixed 128 x 128 cells on the cell grid and cutting the rectangle out of them. Because a cell is
/// always drawn the same way (same origin, same display list items, same arithmetic), a pixel does not depend on which rectangle was
/// asked for, or on how many threads were asking: a region is byte for byte the same as that part of a whole render.
/// </summary>
internal static class CellRenderer
{
    public const int Cell = 128;

    public static void Render(PageRender page, int targetWidth, int targetHeight, ImageRect region, IRowSink sink, CancellationToken ct)
    {
        page.Prepare(ct);
        PdfPageInfo info = page.Info;
        double sx = targetWidth / info.Width, sy = targetHeight / info.Height;
        int firstCol = region.X / Cell, lastCol = (region.Right - 1) / Cell;
        int rowBytes = region.Width * 4;
        byte[] cell = Pools<byte>.Pool.Rent(Cell * Cell * 4);
        byte[] band = Pools<byte>.Pool.Rent(Math.Min(Cell, region.Height) * rowBytes);
        try
        {
            for (int cy = (region.Y / Cell) * Cell; cy < region.Bottom; cy += Cell)
            {
                ct.ThrowIfCancellationRequested();
                int y0 = Math.Max(cy, region.Y), y1 = Math.Min(cy + Cell, region.Bottom);
                int rows = y1 - y0;
                for (int col = firstCol; col <= lastCol; col++)
                {
                    int cx = col * Cell;
                    DrawCell(page, sx, sy, cx, cy, cell, ct);
                    int x0 = Math.Max(cx, region.X), x1 = Math.Min(cx + Cell, region.Right);
                    int bytes = (x1 - x0) * 4;
                    for (int r = 0; r < rows; r++)
                    {
                        cell.AsSpan((((y0 - cy) + r) * Cell + (x0 - cx)) * 4, bytes).CopyTo(band.AsSpan((r * rowBytes) + ((x0 - region.X) * 4)));
                    }
                }

                sink.WriteRows(y0 - region.Y, band.AsSpan(0, rows * rowBytes), rows);
            }
        }
        finally
        {
            Pools<byte>.Pool.Return(cell);
            Pools<byte>.Pool.Return(band);
        }
    }

    /// <summary>Draws the cell whose top-left pixel is (<paramref name="cx"/>, <paramref name="cy"/>) into <paramref name="output"/> (128 x 128 straight RGBA).</summary>
    internal static void DrawCell(PageRender page, double sx, double sy, int cx, int cy, byte[] output, CancellationToken ct)
    {
        // A pixel of slack on every side keeps anti-aliased edges of items just outside the cell.
        var cull = new PdfBounds((cx - 1.5) / sx, (cy - 1.5) / sy, (cx + Cell + 1.5) / sx, (cy + Cell + 1.5) / sy);
        var view = new PdfMatrix(sx, 0, 0, sy, -cx, -cy);
        using var canvas = new Canvas(Cell, Cell, PixelFormat.Rgba8);
        Rgba32 bg = page.Options.Background;
        if (bg.A != 0)
        {
            canvas.Clear(bg);
        }

        var device = new CanvasDevice(page, view, canvas, depth: 0, tint: null, ct);
        try
        {
            page.List.Replay(device, cull);
        }
        finally
        {
            device.Dispose();
        }

        canvas.CopyToBytes(output.AsSpan(0, Cell * Cell * 4), new ImageRect(0, 0, Cell, Cell));
    }
}
