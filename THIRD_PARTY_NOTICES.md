# Third-party components

The portable release contains the following components. Their license files remain with the installed packages under `runtime/Lib/site-packages/*.dist-info` and with Python itself.

| Component | Version | Purpose | Upstream |
| --- | --- | --- | --- |
| Python | 3.12.x | Private interpreter and standard library | https://www.python.org/ |
| Pillow | 12.3.0 | Common image formats | https://github.com/python-pillow/Pillow |
| pypdf | 6.10.0 | PDF page assembly | https://github.com/py-pdf/pypdf |
| ReportLab | 4.4.9 | Photo PDF generation | https://www.reportlab.com/opensource/ |
| pypdfium2 | 5.13.0 | PDF previews and rasterization | https://github.com/pypdfium2-team/pypdfium2 |
| charset-normalizer | 3.5.1 | ReportLab dependency | https://github.com/jawah/charset_normalizer |
| pi-heif | 1.4.0 | HEIC decoding | https://github.com/bigcat88/pi_heif |

pi-heif binary wheels include LGPL-licensed libheif and libde265. Their upstream notices and available native source/build scripts are included in the companion `ConvErtaLocAA-decoder-sources.zip` release asset. The source collector records versions, URLs, and SHA-256 hashes. The pi-heif wrapper source distribution is included as well.

These libraries remain separate, replaceable components in the portable folder. They are not hidden inside the C# executable. Reverse engineering and modification necessary to debug modifications to LGPL components are not prohibited by this project.

PDFium, Pillow, and Python include additional component notices; retain those files when redistributing them. Native codec binaries are supplied by their upstream package maintainers, not independently audited by this project.

Website addresses in license files are attribution and source links. The application does not open those addresses or check for updates.

The Windows decoder also includes MinGW GCC runtime libraries (with the GCC Runtime Library Exception) and winpthreads. Their additional license texts are included under `third-party-runtime-notices` in the portable release.
