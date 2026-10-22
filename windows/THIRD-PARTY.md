# Third-party components

Exact NuGet versions and dependency hashes are recorded in `Morphy.Windows/packages.lock.json`. Package licenses/notices available in those packages are copied into the installed `ThirdParty` directory by the packaging script.

- Magick.NET / ImageMagick: Apache-2.0 / ImageMagick License. https://github.com/dlemstra/Magick.NET
- PDFium and bblanchon PDFium binaries: PDFium BSD-style licenses and bundled third-party notices / Apache-2.0 packaging. https://github.com/bblanchon/pdfium-binaries
- Svg.Skia and related SVG libraries: MIT. https://github.com/wieslawsoltes/Svg.Skia
- SkiaSharp / Skia and HarfBuzzSharp / HarfBuzz: MIT, BSD-style and bundled component notices. https://github.com/mono/SkiaSharp
- Markdig: BSD-2-Clause. https://github.com/xoofx/markdig
- .NET runtime: MIT and bundled third-party notices. https://github.com/dotnet/runtime
- WicHeic v1.1.16, with libheif, libde265 and libkvazaar: LGPL and component licenses in `WicHeic-LICENSE.txt`. https://github.com/prsyahmi/wic_heic/tree/v1.1.16 . The DLL is kept separate and replaceable, and is used only for encoding already decoded pixels. No system codec registration is performed. Corresponding source and build instructions, including pinned submodule commits, are in that repository. Release packaging must include the corresponding source archive alongside the installer (see `scripts/package-codec-sources.ps1`).

LibreOffice is not bundled with the installer. On consent, Morphy downloads the official Windows MSI, verifies its pinned SHA-256 and The Document Foundation signature, extracts its files, and retains the license/source notices included by LibreOffice. https://www.libreoffice.org/about-us/licenses/

No additional restrictions are imposed on replacing or debugging the separately shipped LGPL libraries.
