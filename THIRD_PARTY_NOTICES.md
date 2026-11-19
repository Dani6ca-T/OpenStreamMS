# Third-party notices

OpenStreamMS source code is licensed under the MIT License (see `LICENSE`).

This repository and its installer redistribute the following unmodified third-party
binaries. They are separate programs launched as independent processes (mere aggregation,
not linked into OpenStreamMS), so the MIT license does not apply to them. Each component
remains under its own license; the full license text ships next to the binaries.

| Component | Version | License | License file | Source code |
|---|---|---|---|---|
| [Sunshine](https://github.com/LizardByte/Sunshine) (LizardByte), [sunshine-webrtc](https://github.com/mlopezsegura/Sunshine-Web-RTC) build: upstream Sunshine plus Moonlight WebRTC for Samsung TVs | 2026.1006.152353-16-gbcde822b (commit `bcde822bd8f5d750be1eb71c8ccbcb87c98f0b7a`, upstream base `0594f62d4cc6179aa055f0363043adbc8849b62b`) | GPL-3.0 | `Sunshine/LICENSE.txt` | https://github.com/mlopezsegura/Sunshine-Web-RTC/tree/bcde822bd8f5d750be1eb71c8ccbcb87c98f0b7a |
| [FreeRDP](https://github.com/FreeRDP/FreeRDP), [SlimFreeRDP](https://github.com/mlopezsegura/SlimFreeRDP) build: `wfreerdp.exe` only, static, with the patch in `FreeRDP/patches` | 3.33.0 (SlimFreeRDP `v3.33.0-slim.1`) | Apache-2.0 | `FreeRDP/LICENSE.txt` | https://github.com/mlopezsegura/SlimFreeRDP/tree/v3.33.0-slim.1 |
| [OpenSSL](https://www.openssl.org) (statically linked into `wfreerdp.exe`) | 3.6.5 | Apache-2.0 | `FreeRDP/LICENSE.txt` | https://github.com/openssl/openssl/tree/openssl-3.6.5 |
| [zlib](https://zlib.net) (bundled with Sunshine) | — | zlib | `Sunshine/zlib-LICENSE.txt` | https://github.com/madler/zlib |

Sunshine's web UI (`Sunshine/assets/web`) contains compiled front-end libraries that are
distributed as part of Sunshine under the terms of its release.

## Source code offer (GPL-3.0)

The corresponding source code for the Sunshine binaries is available at the commit
linked above. If that link ever becomes unavailable, open an issue in this repository
and the maintainer will provide the corresponding source for the distributed version.

## libvirtualgamepad (OsmsPad)

The OsmsPad virtual gamepad driver (`Native/OsmsPad`) is an independent, reduced
adaptation of ideas from [libvirtualgamepad](https://github.com/Nonary/libvirtualgamepad):
a UMDF filter above Windows' Virtual HID Framework, controllers owned by the handle
that created them, and the Xbox Series HID report descriptor and hardware IDs that
put the device on the XInput path (`XboxSeries.cpp`, `OsmsPad.inf`). Those parts
are used under the MIT License:

> Copyright (c) 2026 Chase Payne
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this
> software and associated documentation files (the "Software"), to deal in the Software
> without restriction, including without limitation the rights to use, copy, modify,
> merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to the following
> conditions:
>
> The above copyright notice and this permission notice shall be included in all copies
> or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
> INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
> PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
> HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
> CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
> OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Nefarius.ViGEm.Client

Seats deliver captured controllers to their session as ViGEmBus Xbox 360 controllers
through the [Nefarius.ViGEm.Client](https://github.com/ViGEm/ViGEm.NET) library
(`Nefarius.ViGEm.Client.dll`, version 1.21.256, unmodified), used under the MIT License:

> Copyright © Nefarius Software Solutions e.U. 2017-2023
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this
> software and associated documentation files (the "Software"), to deal in the Software
> without restriction, including without limitation the rights to use, copy, modify,
> merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
> permit persons to whom the Software is furnished to do so, subject to the following
> conditions:
>
> The above copyright notice and this permission notice shall be included in all copies
> or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
> INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
> PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
> HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
> CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
> OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Inno Setup language resources

The installer uses official Inno Setup language files and additional unmodified
translations from https://github.com/jrsoftware/issrc at the commit recorded in
`Core/Installer/Languages/sources.json`. Copyright and translator attribution are
retained in those files. Redistribution is under the Inno Setup License, included
as `Core/Installer/Languages/InnoSetup-LICENSE.txt`. British English and Vietnamese
are maintained unofficial translations; Chinese Simplified and Traditional are
listed as official on https://jrsoftware.org/files/istrans/.

## Trademarks

Sunshine, Moonlight, FreeRDP, Windows and Remote Desktop are trademarks of their
respective owners. OpenStreamMS is not affiliated with or endorsed by them.
