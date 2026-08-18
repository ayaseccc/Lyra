# Third-Party Notices

Lyra redistributes the components listed below. Copyright remains with each
upstream author. This file is informational and does not replace the complete
license texts in `licenses/`.

## Managed components

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| WPF-UI / WPF-UI.Abstractions | 4.3.0 | MIT | https://github.com/lepoco/wpfui |
| ManagedBass / ManagedBass.Asio / ManagedBass.Mix / ManagedBass.Wasapi | 4.0.2 | MIT | https://github.com/ManagedBass/ManagedBass |
| Microsoft.Data.Sqlite | 8.0.30 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw.bundle_e_sqlite3 / batteries_v2 / core / provider.e_sqlite3 | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| TagLibSharp | 2.3.0 | LGPL-2.1-only | https://github.com/mono/taglib-sharp |
| Serilog | 4.4.0 | Apache-2.0 | https://github.com/serilog/serilog |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |

Copyright notices required by the MIT-licensed components include:

- CommunityToolkit.Mvvm: Copyright © .NET Foundation and Contributors.
- WPF-UI: Copyright © 2021-2025 Leszek Pomianowski and WPF UI Contributors.
- ManagedBass: Copyright © 2016 Mathew Sachin.
- Microsoft.Data.Sqlite: Copyright © .NET Foundation and Contributors.

The common MIT, Apache-2.0 and LGPL-2.1-only terms are included as
`licenses/MIT.txt`, `licenses/Apache-2.0.txt`, and `licenses/LGPL-2.1.txt`.
The original WPF-UI and CommunityToolkit third-party notices are also included
under `licenses/`.

TagLibSharp remains a separate dynamically linked DLL in the distribution. A
recipient may replace that DLL with a compatible modified build. The upstream
source for the exact distributed version is available at
https://github.com/mono/taglib-sharp/tree/2.3.0. Lyra itself is not covered by
the LGPL merely because it uses this separately distributed library; any
modification to TagLibSharp remains subject to LGPL-2.1-only.

SQLite's source code has been dedicated to the public domain. Its notice is
included as `licenses/SQLite-Public-Domain.txt`; the SQLitePCLRaw wrapper and
packaging remain Apache-2.0.

## BASS native components

The distribution contains exactly these nine x64 native libraries (versions
are taken from the shipped DLL metadata):

| File | Version | Provider / terms |
| --- | --- | --- |
| `bass.dll` | 2.4.18 | un4seen; BASS licence |
| `bassasio.dll` | 1.4.3 | un4seen; BASSASIO licence |
| `bassmix.dll` | 2.4.12 | un4seen; free to use with BASS |
| `basswasapi.dll` | 2.4.4 | un4seen; free to use with BASS |
| `bassflac.dll` | 2.4.6 | un4seen; free to use with BASS |
| `bassape.dll` | 2.4.1 | un4seen; free to use with BASS |
| `basswv.dll` | 2.4.7 | un4seen; free to use with BASS |
| `bassopus.dll` | 2.4.3 | un4seen; free to use with BASS |
| `bassalac.dll` | 2.4.1 | un4seen; free to use with BASS |

BASS and BASSASIO are free for non-commercial use; commercial use requires the
appropriate un4seen licence. The other listed add-ons are free to use with
BASS. Complete relevant upstream terms are included in `licenses/BASS.txt`,
`licenses/BASSASIO.txt`, and `licenses/BASS-ADDONS.txt`.

Lyra intentionally does **not** redistribute the third-party
`bass_aac.dll`, whose upstream distribution is GPLv2. On Windows 10 and later,
AAC and M4A playback uses the codecs exposed to BASS by Windows.

## Self-contained .NET and Windows projection

The portable zip contains a self-contained Microsoft .NET and Windows Desktop
runtime. `tools/publish.ps1` copies the official `LICENSE.txt` and
`ThirdPartyNotices.txt` from the exact `dotnet` installation used to build the
archive into `licenses/dotnet/`. The runtime-pack version can be verified in
`Lyra.deps.json`. Additional details and official links are recorded in
`licenses/DOTNET-RUNTIME.md`.

## Online services

GD Studio and ChKSz are network services, not code redistributed in the zip.
GD Studio describes its API as CC BY-NC; use is limited to personal,
non-commercial scenarios and must preserve attribution. ChKSz users provide
their own Key and remain responsible for the provider's current terms. Lyra
does not grant any licence to third-party audio or metadata.
