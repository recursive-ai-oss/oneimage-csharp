using OneImage.Text.Fonts;

namespace OneImage.Text.Tests;

/// <summary>The committed test fonts and one shared collection of them.</summary>
internal static class TestFonts
{
    public const string Regular = "NotoSans-Subset-Regular.ttf";
    public const string BoldKern = "NotoSans-Subset-Bold-kern.ttf";
    public const string Collection = "NotoSans-Subset.ttc";
    public const string Woff = "NotoSans-Subset-Regular.woff";
    public const string Cff = "NotoSansCJK-Subset.otf";

    private static readonly Lazy<FontCollection> Shared = new(() =>
    {
        var c = new FontCollection();
        c.Add(PathOf(Regular));
        c.Add(PathOf(BoldKern));
        c.Add(PathOf(Cff));
        return c;
    });

    public static string PathOf(string file) => System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "fonts", file);

    public static byte[] Bytes(string file) => File.ReadAllBytes(PathOf(file));

    public static FontCollection Fonts => Shared.Value;

    public static Font Noto(float size, FontStyle style = FontStyle.Regular) => Fonts.Get("Noto Sans").CreateFont(size, style);

    public static Font NotoCjk(float size) => Fonts.Get("Noto Sans CJK JP").CreateFont(size);

    public static FontFace Face(string file, int index = 0) => FontFile.Parse(Bytes(file))[index];
}
