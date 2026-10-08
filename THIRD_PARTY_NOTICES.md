# Third-party notices

Northpass is independent software. It does not claim authorship of Zapret2 or WinDivert. The original starter ZIP and this source distribution contain no bundled engine/driver binaries.

- **Zapret2** — Copyright (c) 2016–2026 bol-van, MIT. Official [license](https://github.com/bol-van/zapret2/blob/master/docs/LICENSE.txt). A copy from the reviewed revision is retained in `docs/licenses/Zapret2-MIT.txt`. The adapter's option metadata was reviewed against `nfq2/nfqws.c` and `docs/manual.en.md` at `a1adf7b868e65c8a77aab608c8bbd9ec8b30a256`. The external bundle is installed separately from [bol-van/zapret-win-bundle](https://github.com/bol-van/zapret-win-bundle).
- **WinDivert** — a separate third-party dependency supplied by the official Windows engine bundle. Review its [license and distribution terms](https://reqrypt.org/windivert.html) and preserve the exact notices from the version you distribute. Northpass does not redistribute it.
- **.NET / WPF / Windows Forms** — Microsoft and contributors; preserve notices/licenses included in the self-contained .NET runtime used for publication. See [dotnet/runtime](https://github.com/dotnet/runtime), [dotnet/wpf](https://github.com/dotnet/wpf), and [dotnet/winforms](https://github.com/dotnet/winforms).
- **xUnit.net** and **Microsoft.NET.Test.Sdk** — development/test dependencies restored from NuGet with pinned versions and content hashes in the test lockfiles; not included in the application publish.
- **Inno Setup** — optional installer compiler installed separately from its official source. The installer template includes Northpass only, not engine assets.

If a later release distributes Zapret2 binaries, Lua, WinDivert, Cygwin or other engine components, include the exact versions' full licenses, attribution and provenance. Do not treat this notice as permission to omit their requirements. No project-wide license for original Northpass code has been chosen here; the repository owner must choose one before public redistribution.
