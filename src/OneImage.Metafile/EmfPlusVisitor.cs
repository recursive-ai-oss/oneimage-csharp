namespace OneImage.Metafile;

public abstract partial class DrawingCommandVisitor
{
    /// <summary>Handles <see cref="ClearCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitClear(ClearCommand command)
    {
    }

    /// <summary>Handles <see cref="RenderingHintsCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitRenderingHints(RenderingHintsCommand command)
    {
    }

    /// <summary>Handles <see cref="DrawStringCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitDrawString(DrawStringCommand command)
    {
    }

    /// <summary>Handles <see cref="DrawGlyphsCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitDrawGlyphs(DrawGlyphsCommand command)
    {
    }

    /// <summary>Handles <see cref="DrawImageCommand"/>.</summary>
    /// <param name="command">The command.</param>
    public virtual void VisitDrawImage(DrawImageCommand command)
    {
    }
}
