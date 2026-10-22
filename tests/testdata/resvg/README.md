# resvg test files

SVG files and reference PNGs from the public test collection of the resvg project
(https://github.com/linebender/resvg, directory `crates/resvg/tests`, commit
5192641d4fd67aef5c0c4d667d570e338da15361), plus the shared `resources` folder the tests refer to.
The reference PNGs are 300 px wide renderings of the 200x200 test files.

The collection is part of the resvg repository, which is licensed under either of the MIT license or the
Apache License 2.0 (`LICENSE-MIT`, `LICENSE-APACHE` in this folder; copyright 2017 the Resvg Authors).
The fonts the text tests use are not copied here; `tools/fetch-resvg-tests.sh` downloads the full
collection with its fonts into a cache folder, and the conformance tests use it when `ONEIMAGE_RESVG_TESTS` points there.
