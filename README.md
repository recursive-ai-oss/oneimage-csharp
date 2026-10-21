# OneImage

A pure-managed .NET image library: one-line thumbnail, crop and convert, and tiled, lazy, streaming load/edit/render on the same engine. No dependencies beyond the .NET base class library.

**Status:** early development. There is no usable API yet, and the name is provisional.

## Building

```
dotnet build -c Release
dotnet test -c Release
```

Build output goes outside the checkout: set `ONEIMAGE_ARTIFACTS` to choose the directory, otherwise it defaults to `../OneImage-artifacts` next to the repository.

## License

Licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
