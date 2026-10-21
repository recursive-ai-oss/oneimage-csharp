using System;
using OneImage;

// Exercises the library from a NativeAOT-published app; grows as the library does.
if (string.IsNullOrEmpty(Library.Name))
{
    Console.Error.WriteLine("library name missing");
    return 1;
}

using var canvas = new Canvas(300, 200, PixelFormat.Rgba8);
canvas[10, 20] = new Rgba32(1, 2, 3, 255);
using (PixelLock<Rgba32> l = canvas.Lock<Rgba32>(new ImageRect(100, 100, 200, 50), LockAccess.Write))
{
    for (int y = 0; y < l.Height; y++)
    {
        l.GetRow(y).Fill(new Rgba32(9, 8, 7, 255));
    }
}

Image snapshot = canvas.Snapshot();
canvas[10, 20] = new Rgba32(99, 99, 99, 255);
Image edited = snapshot.Crop(5, 15, 100, 100).Invert().ConvertTo(PixelFormat.Gray8);
Gray8[] gray = edited.ToArray<Gray8>();
Rgba32[] px = snapshot.Crop(5, 15, 100, 100).ToArray<Rgba32>();
Console.WriteLine(edited.DescribeGraph().TrimEnd());

bool ok = px[(5 * 100) + 5] == new Rgba32(1, 2, 3, 255) && gray.Length == 10000 && canvas[10, 20].R == 99
    && !Image.TryLoad(new byte[] { 1, 2, 3 }, out _);
if (!ok)
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

Console.WriteLine("smoke ok: " + Library.Name);
return 0;
