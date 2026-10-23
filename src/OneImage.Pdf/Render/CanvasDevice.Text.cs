using OneImage.Drawing;
using OnePdf;
using DrawingPath = OneImage.Drawing.Path;

namespace OneImage.Pdf.Render;

internal sealed partial class CanvasDevice
{
    /// <summary>
    /// Shows a string. Every glyph outline of the run is placed (OnePDF gives outlines in em units, <c>GlyphMatrix</c> maps them to user
    /// space) and the run is filled as one shape, so overlapping glyphs of one run never blend twice. Stroke modes stroke the outlines in
    /// user space, so the line width follows the CTM; clip modes add the glyphs to the text clip taken by the next <c>PushTextClip</c>.
    /// </summary>
    public void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        PdfTextRenderMode mode = run.RenderMode;
        if (mode == PdfTextRenderMode.Invisible)
        {
            return;
        }

        bool fill = mode is PdfTextRenderMode.Fill or PdfTextRenderMode.FillStroke or PdfTextRenderMode.FillClip or PdfTextRenderMode.FillStrokeClip;
        bool stroke = mode is PdfTextRenderMode.Stroke or PdfTextRenderMode.FillStroke or PdfTextRenderMode.StrokeClip or PdfTextRenderMode.FillStrokeClip;
        bool clip = (int)mode >= 4;
        if (Skip && !clip)
        {
            return;
        }

        _ct.ThrowIfCancellationRequested();
        PdfMatrix total = state.Ctm.Then(_view);
        ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
        var devicePath = new PathBuilder();
        PathBuilder? userPath = stroke ? new PathBuilder() : null;
        bool any = false;
        for (int i = 0; i < glyphs.Length; i++)
        {
            PdfPath outline = glyphs[i].Outline;
            if (outline.IsEmpty)
            {
                continue;
            }

            PdfMatrix glyphToUser = run.GlyphMatrix(i);
            if (!glyphToUser.IsFinite)
            {
                continue;
            }

            if (PathConvert.Append(devicePath, outline, glyphToUser.Then(total), clamp: true))
            {
                any = true;
            }

            if (userPath is not null)
            {
                PathConvert.Append(userPath, outline, glyphToUser, clamp: false);
            }
        }

        if (!any)
        {
            if (clip)
            {
                _textClip ??= new PathBuilder();
            }

            return;
        }

        DrawingPath dev = devicePath.Build();
        if (fill && !Skip && !dev.IsEmpty)
        {
            Paint(dev, IntersectionRule.NonZero, state.FillColor, state.FillAlpha, state);
        }

        if (stroke && !Skip && userPath is not null)
        {
            DrawingPath user = userPath.Build();
            double scale = PathConvert.MeanScale(total);
            if (!user.IsEmpty && scale > 1e-9 && double.IsFinite(scale))
            {
                DrawingPath? outline = StrokeUserPath(user, state, total, scale);
                if (outline is not null)
                {
                    Paint(outline, IntersectionRule.NonZero, state.StrokeColor, state.StrokeAlpha, state);
                }
            }
        }

        if (clip)
        {
            _textClip ??= new PathBuilder();
            _textClip.AddPath(dev);
        }
    }
}
