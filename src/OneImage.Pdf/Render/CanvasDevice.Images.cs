using OneImage.Drawing;
using OnePdf;
using DrawingPath = OneImage.Drawing.Path;

namespace OneImage.Pdf.Render;

internal sealed partial class CanvasDevice
{
    /// <summary>
    /// Draws an image into the unit square of the CTM. The picture is reduced by area averaging when the page shows it smaller than it
    /// is (once per size, shared by all cells), then sampled bilinearly through the placement with its border pixels extended.
    /// A stencil mask is painted in the fill color.
    /// </summary>
    public void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
        if (Skip)
        {
            return;
        }

        _ct.ThrowIfCancellationRequested();
        PdfMatrix m = state.Ctm.Then(_view);
        if (!m.IsFinite || !(Math.Abs(m.Determinant) > 1e-12))
        {
            return;
        }

        PdfImageData? data = _page.GetImageData(image, _ct);
        if (data is null || !data.IsDecoded)
        {
            return;
        }

        double devW = Math.Sqrt((m.A * m.A) + (m.B * m.B)), devH = Math.Sqrt((m.C * m.C) + (m.D * m.D));
        int dw = (int)Math.Clamp(Math.Ceiling(devW), 1, 1 << 20), dh = (int)Math.Clamp(Math.Ceiling(devH), 1, 1 << 20);
        Raster? raster = _page.GetRaster(data, dw, dh, _ct);
        if (raster is null)
        {
            return;
        }

        // A picture enlarged to at least twice its size keeps its pixels square unless it asks for interpolation (/Interpolate true): the
        // reference viewers do, and smoothing would blur a pixel-art picture or a barcode. A smaller enlargement is sampled bilinearly.
        bool magnified = devW >= 2 * raster.Width && devH >= 2 * raster.Height && !data.Interpolate;

        // Picture pixel (column, row) to the unit square: x = column / width, and row 0 is the top, at y = 1.
        var toUnit = new PdfMatrix(1.0 / raster.Width, 0, 0, -1.0 / raster.Height, 0, 1);
        PdfMatrix placement = toUnit.Then(m);
        Rgba32 stencil = default;
        if (raster.Stencil)
        {
            if (_tint is { } t)
            {
                stencil = t;
            }
            else if (state.FillColor.Space.Kind == PdfColorSpaceKind.Pattern)
            {
                // A stencil painted with a pattern: the pattern's own paint is used through the coverage.
                PaintStencilWithPattern(raster, placement, m, state, magnified);
                return;
            }
            else
            {
                stencil = PdfColors.ToRgba(state.FillColor);
            }
        }

        DrawingPath? quad = PathConvert.ToPath(UnitSquare, m);
        if (quad is null)
        {
            return;
        }

        var brush = new PixelBrush(raster, placement, nearest: magnified, stencil);
        PaintBrush(brush, quad, IntersectionRule.NonZero, state.FillAlpha, state);
    }

    private static readonly PdfPath UnitSquare = new(
        new[] { (byte)PdfPathVerb.Move, (byte)PdfPathVerb.Line, (byte)PdfPathVerb.Line, (byte)PdfPathVerb.Line, (byte)PdfPathVerb.Close },
        new double[] { 0, 0, 1, 0, 1, 1, 0, 1 });

    /// <summary>Paints a pattern through a stencil's coverage: the pattern is drawn into a layer, and the layer is masked by the stencil.</summary>
    private void PaintStencilWithPattern(Raster stencil, in PdfMatrix placement, in PdfMatrix m, in PdfGraphicsState state, bool nearest)
    {
        DrawingPath? quad = PathConvert.ToPath(UnitSquare, m);
        Brush? paint = MakeBrush(state.FillColor);
        if (quad is null || paint is null || !(state.FillAlpha > 0))
        {
            return;
        }

        using var patternLayer = new Canvas(_w, _h, PixelFormat.Rgba8);
        patternLayer.Fill(paint, quad, true, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver, false, null, _clip.Path, _clip.Rule);
        using var coverage = new Canvas(_w, _h, PixelFormat.Rgba8);
        var cover = new PixelBrush(stencil, placement, nearest: nearest, new Rgba32(255, 255, 255, 255));
        coverage.Fill(cover, quad, true, IntersectionRule.NonZero, 1f, BlendMode.Normal, AlphaComposition.SrcOver);
        Image masked = patternLayer.View().ApplyMask(coverage.View(), MaskChannel.Alpha);
        var brush = new ImageBrush(masked, null, 0, 0, null, tile: false, pooled: true);
        try
        {
            Canvas? layer = BeginMasked(state);
            Canvas target = layer ?? _target;
            target.Fill(brush, DrawingPath.Rectangle(0, 0, _w, _h), false, IntersectionRule.NonZero, (float)Math.Min(state.FillAlpha, 1), layer is null ? ParseBlend(state.BlendMode) : BlendMode.Normal, AlphaComposition.SrcOver);
            if (layer is not null)
            {
                CompositeLayer(layer, 1, ParseBlend(state.BlendMode), state.SoftMaskId);
            }
        }
        finally
        {
            brush.Release();
        }
    }

    /// <summary>The <c>sh</c> operator: the shading paints everything inside the current clip.</summary>
    public void PaintShading(PdfShading shading, in PdfGraphicsState state)
    {
        if (Skip)
        {
            return;
        }

        _ct.ThrowIfCancellationRequested();
        PdfMatrix m = state.Ctm.Then(_view);
        if (!m.IsFinite)
        {
            return;
        }

        var brush = new ShadingBrush(shading, m, shading.ShadingType is 2 or 3 ? _page.GetLut(shading) : null, null, _w, _h);
        PaintBrush(brush, DrawingPath.Rectangle(0, 0, _w, _h), IntersectionRule.NonZero, state.FillAlpha, state);
    }
}
