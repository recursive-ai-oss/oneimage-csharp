using System;
using OneImage;

// Exercises the library from a NativeAOT-published app; grows as the library does.
if (string.IsNullOrEmpty(Library.Name))
{
    Console.Error.WriteLine("library name missing");
    return 1;
}

Console.WriteLine("smoke ok: " + Library.Name);
return 0;
