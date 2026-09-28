"""Collect upstream decoder sources on the build machine, never at app runtime."""
from __future__ import annotations
import hashlib
import importlib.metadata
import io
import json
from pathlib import Path
import re
import sys
import tarfile
import urllib.request
import zipfile

def download(url):
    request = urllib.request.Request(url, headers={"User-Agent": "ConvErtaLocAA-release-build"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()

def main():
    import pi_heif
    info = pi_heif.libheif_info()
    version = importlib.metadata.version("pi-heif")
    metadata = json.loads(download("https://pypi.org/pypi/pi-heif/" + version + "/json"))
    source = next(item for item in metadata["urls"] if item["packagetype"] == "sdist")
    archive = download(source["url"])
    if hashlib.sha256(archive).hexdigest() != source["digests"]["sha256"]:
        raise RuntimeError("pi-heif source checksum mismatch")
    heif_version = info["libheif"]
    decoder_names = " ".join(info["decoders"].values())
    match = re.search(r"libde265[^0-9]*(\d+\.\d+\.\d+)", decoder_names, re.I)
    if not match:
        raise RuntimeError("Cannot determine the bundled libde265 version: " + repr(info))
    de265_version = match.group(1)
    destination = Path(sys.argv[1])
    destination.parent.mkdir(parents=True, exist_ok=True)
    manifest = {"decoder_runtime": info, "files": []}
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as result:
        def add(name, url, data):
            result.writestr(name, data)
            manifest["files"].append({"file": name, "url": url, "sha256": hashlib.sha256(data).hexdigest()})
        add(source["filename"], source["url"], archive)
        for name, release in [("libheif", heif_version), ("libde265", de265_version)]:
            url = "https://github.com/strukturag/" + name + "/releases/download/v" + release + "/" + name + "-" + release + ".tar.gz"
            add(name + "-" + release + ".tar.gz", url, download(url))
        # Preserve the upstream Windows build recipe and wrapper/native notices.
        with tarfile.open(fileobj=io.BytesIO(archive)) as upstream:
            for item in upstream.getmembers():
                if item.isfile() and (item.name.endswith("PKGBUILD") or item.name.endswith("LICENSE.txt") or item.name.endswith("LICENSES_bundled.txt")):
                    result.writestr("upstream-notices/" + item.name, upstream.extractfile(item).read())
        additional = {
            "gcc-COPYING3": "https://raw.githubusercontent.com/gcc-mirror/gcc/releases/gcc-15.2.0/COPYING3",
            "gcc-RUNTIME-LIBRARY-EXCEPTION": "https://raw.githubusercontent.com/gcc-mirror/gcc/releases/gcc-15.2.0/COPYING.RUNTIME",
            "mingw-winpthreads-COPYING": "https://raw.githubusercontent.com/mingw-w64/mingw-w64/v13.0.0/mingw-w64-libraries/winpthreads/COPYING",
        }
        for name, url in additional.items():
            add("runtime-notices/" + name + ".txt", url, download(url))
        result.writestr("MANIFEST.json", json.dumps(manifest, indent=2) + "\n")
        result.writestr("README.txt",
            "Corresponding upstream source for the unmodified pi-heif, libheif and libde265 components.\n"
            "Actual decoder versions are recorded in MANIFEST.json.\n"
            "The pi-heif source archive includes its wrapper source and native build scripts.\n"
            "Use the Windows PKGBUILD and decoder-only build settings from that archive.\n"
            "The application loads the decoder as replaceable Python/native modules.\n"
            "MinGW GCC runtime libraries use the GCC Runtime Library Exception; winpthreads has its own notice.\n"
            "No personal images or test outputs are included.\n")
    print(json.dumps(info, indent=2))
    print("Decoder source bundle:", destination)

if __name__ == "__main__":
    main()
