# Microsoft .NET self-contained runtime

The Lyra zip is built with `dotnet publish --self-contained true` and
contains Microsoft.NETCore.App, Microsoft.WindowsDesktop.App, and the Windows
SDK .NET projection required by the application.

At publish time, the official `LICENSE.txt` and `ThirdPartyNotices.txt` beside
the selected `dotnet` executable are copied without modification to this
directory's `dotnet/` subdirectory in the final zip. `Lyra.deps.json` records
the exact runtime-pack and package versions used for that archive.

Official references:

- .NET redistribution and licensing:
  https://dotnet.microsoft.com/platform/free
- .NET runtime notices:
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- Windows SDK .NET projection licence:
  https://aka.ms/WinSDKLicenseURL

If this file is read from a source checkout, the `dotnet/` subdirectory is
expected to be absent; `tools/publish.ps1` creates it only inside the audited
release staging directory.
