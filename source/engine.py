"""ConvErtaLocAA's offline conversion worker. JSON on stdin/stdout; no server."""
from __future__ import annotations

import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid
import warnings


def deny_network(event, args):
    if event in {"socket.connect", "socket.bind", "socket.getaddrinfo", "socket.sendto"}:
        raise PermissionError("ConvErtaLocAA does not use network connections.")


sys.addaudithook(deny_network)

from PIL import Image, ImageOps, features
from pypdf import PdfReader, PdfWriter
import pypdfium2 as pdfium
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader

warnings.simplefilter("error", Image.DecompressionBombWarning)
ROOT = Path(__file__).resolve().parent.parent
HEIF_CONVERT = ROOT / "codecs" / "heif-convert.exe"
SUPPORTED = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".avif", ".heic", ".heif", ".gif", ".pdf"}
IMAGE_FORMATS = {"JPG": (".jpg", "JPEG"), "PNG": (".png", "PNG"), "WebP": (".webp", "WEBP"), "BMP": (".bmp", "BMP"), "TIFF": (".tiff", "TIFF"), "AVIF": (".avif", "AVIF"), "HEIC": (".heic", "HEIF")}
MAX_PAGES = 2000


def emit(message):
    print(json.dumps(message, ensure_ascii=True), flush=True)


def capabilities():
    Image.init()
    # An optional, locally installed encoder can extend this build; never download one.
    try:
        from pillow_heif import register_heif_opener
        register_heif_opener(thumbnails=False)
    except ImportError:
        try:
            from pi_heif import register_heif_opener
            register_heif_opener(thumbnails=False)
        except ImportError:
            pass
    formats = [key for key, (_, fmt) in IMAGE_FORMATS.items() if fmt in Image.SAVE]
    return {"formats": formats, "heic_input": HEIF_CONVERT.exists() or "HEIF" in Image.OPEN}


def opened_image(item):
    path = Path(item.get("decoded") or item["path"])
    with Image.open(path) as source:
        source.seek(int(item.get("frame", 0)))
        source.load()
        result = ImageOps.exif_transpose(source).copy()
    if int(item.get("rotation", 0)) % 360:
        result = result.rotate(-int(item["rotation"]), expand=True)
    return result


def white_rgb(image):
    if image.mode in ("RGBA", "LA") or "transparency" in image.info:
        rgba = image.convert("RGBA")
        result = Image.new("RGB", rgba.size, "white")
        result.paste(rgba, mask=rgba.getchannel("A"))
        return result
    return image.convert("RGB")


def thumbnail(image, cache):
    image.thumbnail((760, 760), Image.Resampling.LANCZOS)
    path = cache / (uuid.uuid4().hex + ".png")
    white_rgb(image).save(path, "PNG")
    return str(path)


def decode_heic(path, cache):
    if "HEIF" in Image.OPEN:
        return str(path)
    if not HEIF_CONVERT.is_file():
        raise ValueError("The HEIC decoder is missing from the app's codecs folder.")
    out = cache / (uuid.uuid4().hex + ".png")
    result = subprocess.run(
        [str(HEIF_CONVERT), "--quiet", "--png-compression-level", "1", str(path), str(out)],
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), timeout=180,
    )
    if result.returncode:
        detail = result.stderr.decode("utf-8", errors="replace").strip()[-700:]
        raise ValueError("Could not decode HEIC. " + detail)
    if not out.exists():
        # Some HEIF containers contain several top-level pictures. Choose the first
        # main picture, never a depth map or auxiliary image.
        matches = sorted(cache.glob(out.stem + "-*.png"))
        if not matches:
            raise ValueError("The HEIC decoder did not produce an image.")
        return str(matches[0])
    return str(out)


def pdf_reader(path):
    reader = PdfReader(str(path))
    if reader.is_encrypted and not reader.decrypt(""):
        reader.close()
        raise ValueError("This PDF needs a password. Save an unlocked copy before adding it.")
    return reader


def inspect_files(paths, cache_dir, progress=None):
    cache = Path(cache_dir)
    cache.mkdir(parents=True, exist_ok=True)
    items, errors = [], []
    for file_number, raw_path in enumerate(paths):
        path = Path(raw_path).resolve()
        try:
            if not path.exists():
                raise ValueError("This file no longer exists at its original location.")
            if not path.is_file():
                raise ValueError("Choose files, not folders.")
            if path.suffix.lower() not in SUPPORTED:
                raise ValueError("Unsupported type. Add an image or PDF.")
            if path.suffix.lower() == ".pdf":
                with contextlib.closing(pdf_reader(path)) as reader:
                    count = len(reader.pages)
                if count > MAX_PAGES or len(items) + count > MAX_PAGES:
                    raise ValueError("Add at most 2,000 pages at a time.")
                if not count:
                    raise ValueError("This PDF has no pages.")
                with pdfium.PdfDocument(str(path)) as doc:
                    for page_index in range(count):
                        with contextlib.closing(doc[page_index]) as page:
                            width, height = page.get_size()
                            scale = min(760 / max(width, height), 2)
                            bitmap = page.render(scale=scale)
                            try:
                                image = bitmap.to_pil().copy()
                            finally:
                                bitmap.close()
                        items.append({"id": uuid.uuid4().hex, "kind": "pdf", "path": str(path), "page": page_index, "frame": 0, "rotation": 0, "width": width, "height": height, "preview": thumbnail(image, cache)})
                        if progress:
                            progress("Reading " + path.name + " — page " + str(page_index + 1) + "/" + str(count))
            else:
                decoded = decode_heic(path, cache) if path.suffix.lower() in {".heic", ".heif"} else str(path)
                with Image.open(decoded) as source:
                    # Photos use their primary still; multi-page TIFFs expand to pages.
                    count = getattr(source, "n_frames", 1) if path.suffix.lower() in {".tif", ".tiff"} else 1
                    if count > MAX_PAGES or len(items) + count > MAX_PAGES:
                        raise ValueError("Add at most 2,000 pages at a time.")
                    for frame in range(count):
                        source.seek(frame)
                        image = ImageOps.exif_transpose(source).copy()
                        width, height = image.size
                        items.append({"id": uuid.uuid4().hex, "kind": "image", "path": str(path), "decoded": decoded, "page": frame, "frame": frame, "rotation": 0, "width": width, "height": height, "preview": thumbnail(image, cache)})
            if progress:
                progress("Added " + path.name)
        except Exception as exc:
            errors.append(path.name + ": " + str(exc))
    return {"items": items, "errors": errors}


def raster_page(item, dpi=150):
    if item["kind"] == "image":
        return opened_image(item)
    with pdfium.PdfDocument(item["path"]) as doc:
        with contextlib.closing(doc[int(item["page"])]) as page:
            width, height = page.get_size()
            if width * height * (dpi / 72) ** 2 > 80_000_000:
                raise ValueError("PDF page is too large to render. Use a lower resolution.")
            bitmap = page.render(scale=dpi / 72, rotation=int(item.get("rotation", 0)))
            try:
                return bitmap.to_pil().copy()
            finally:
                bitmap.close()


def clean_image(image, target, strip_metadata=True):
    # Apply orientation before dropping camera metadata. Preserve ICC color profile.
    info = image.info.copy()
    icc = info.get("icc_profile")
    if target in {"JPG", "BMP"}:
        image = white_rgb(image)
    elif image.mode not in {"RGB", "RGBA", "L", "LA", "I;16"}:
        image = image.convert("RGBA" if "transparency" in info else "RGB")
    if target in {"WebP", "AVIF", "HEIC"} and image.mode not in {"RGB", "RGBA"}:
        image = image.convert("RGBA" if "A" in image.getbands() else "RGB")
    image.info.clear()
    if icc:
        image.info["icc_profile"] = icc
    if not strip_metadata:
        for key in ("exif", "xmp"):
            if info.get(key):
                image.info[key] = info[key]
    return image


def publish_unique(temp_path, folder, stem, suffix):
    """Publish a complete file, without replacing an original or previous output."""
    for index in range(10000):
        path = folder / (stem + (" (" + str(index) + ")" if index else "") + suffix)
        if os.name == "nt":
            try:
                os.rename(temp_path, path)  # Windows rename refuses to overwrite.
                return path
            except FileExistsError:
                continue
        else:
            try:
                os.link(temp_path, path)
                Path(temp_path).unlink()
                return path
            except FileExistsError:
                continue
    raise ValueError("Too many files with the same name in the output folder.")


def safe_name(name, default):
    name = "".join(c for c in str(name) if c not in '<>:"/\\|?*' and ord(c) >= 32).strip(" .")
    if name.lower().endswith(".pdf"):
        name = name[:-4].rstrip(" .")
    if not name:
        return default
    if name.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL", *["COM" + str(i) for i in range(1, 10)], *["LPT" + str(i) for i in range(1, 10)]}:
        name = "_" + name
    return name[:100]


def photo_pdf(image, page_size):
    image = white_rgb(image)
    width, height = image.size
    if page_size == "Letter":
        pw, ph, margin = 612, 792, 18
    elif page_size == "A4":
        pw, ph, margin = 595.2756, 841.8898, 18
    else:
        pw, ph, margin = width * 72 / 150, height * 72 / 150, 0
    if page_size != "Fit image" and width > height:
        pw, ph = ph, pw
    scale = min((pw - 2 * margin) / width, (ph - 2 * margin) / height)
    dw, dh = width * scale, height * scale
    buffer = io.BytesIO()
    doc = canvas.Canvas(buffer, pagesize=(pw, ph), pageCompression=1)
    doc.setCreator("ConvErtaLocAA")
    doc.setAuthor("")
    doc.drawImage(ImageReader(image), (pw - dw) / 2, (ph - dh) / 2, width=dw, height=dh)
    doc.showPage()
    doc.save()
    buffer.seek(0)
    return buffer


def convert(request, progress=None):
    items = request.get("items", [])
    if not items:
        raise ValueError("Add at least one image or PDF page first.")
    if len(items) > MAX_PAGES:
        raise ValueError("Convert at most 2,000 pages at a time.")
    target = request.get("format", "PDF")
    folder = Path(request["output"]).resolve()
    if target != "PDF" and target not in capabilities()["formats"]:
        raise ValueError(target + " output is not available in this build.")
    folder.mkdir(parents=True, exist_ok=True)
    quality = max(1, min(100, int(request.get("quality", 92))))
    dpi = max(72, min(600, int(request.get("dpi", 150))))
    outputs, errors = [], []
    if target == "PDF":
        writer = PdfWriter()
        readers, buffers = {}, []
        try:
            for index, item in enumerate(items):
                if progress:
                    progress("Building PDF — page " + str(index + 1) + "/" + str(len(items)))
                if item["kind"] == "pdf":
                    key = item["path"]
                    if key not in readers:
                        readers[key] = pdf_reader(key)
                        if readers[key].get_fields():
                            readers[key].add_form_topname("source" + str(len(readers)))
                    reader = readers[key]
                    writer.reset_translation(reader)
                    writer.append(reader, pages=[int(item["page"])], import_outline=False)
                    rotation = int(item.get("rotation", 0)) % 360
                    if rotation:
                        writer.pages[-1].rotate(rotation)
                else:
                    buffer = photo_pdf(opened_image(item), request.get("page_size", "Fit image"))
                    buffers.append(buffer)
                    image_reader = PdfReader(buffer)
                    writer.add_page(image_reader.pages[0])
            writer.add_metadata({"/Producer": "ConvErtaLocAA", "/Creator": "ConvErtaLocAA"})
            with tempfile.NamedTemporaryFile(dir=folder, suffix=".partial", delete=False) as temp:
                temp_name = temp.name
            try:
                writer.write(temp_name)
                with contextlib.closing(PdfReader(temp_name)) as check:
                    if len(check.pages) != len(items):
                        raise ValueError("The output page count did not match the queue.")
                outputs.append(str(publish_unique(temp_name, folder, safe_name(request.get("name", "Combined"), "Combined"), ".pdf")))
            finally:
                Path(temp_name).unlink(missing_ok=True)
        finally:
            writer.close()
            for reader in readers.values():
                reader.close()
            for buffer in buffers:
                buffer.close()
    else:
        suffix, pil_format = IMAGE_FORMATS[target]
        for index, item in enumerate(items):
            temp_name = None
            try:
                if progress:
                    progress("Converting " + str(index + 1) + "/" + str(len(items)))
                image = clean_image(raster_page(item, dpi), target, request.get("strip_metadata", True))
                options = dict(image.info)
                if target in {"JPG", "WebP", "AVIF", "HEIC"}:
                    options["quality"] = quality
                if target == "JPG":
                    options.update(subsampling=0, optimize=True)
                if target == "TIFF":
                    options["compression"] = "tiff_deflate"
                with tempfile.NamedTemporaryFile(dir=folder, suffix=".partial", delete=False) as temp:
                    temp_name = temp.name
                image.save(temp_name, pil_format, **options)
                stem = safe_name(Path(item["path"]).stem, "Image")
                if item["kind"] == "pdf" or int(item.get("frame", 0)):
                    stem += "-page-" + str(int(item["page"]) + 1)
                outputs.append(str(publish_unique(temp_name, folder, stem, suffix)))
            except Exception as exc:
                errors.append(Path(item["path"]).name + ": " + str(exc))
            finally:
                if temp_name:
                    Path(temp_name).unlink(missing_ok=True)
    return {"outputs": outputs, "errors": errors}


def main():
    try:
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
        request = json.load(sys.stdin)
        available = capabilities()
        progress = lambda message: emit({"type": "progress", "message": message})
        if request["action"] == "capabilities":
            result = available
        elif request["action"] == "inspect":
            result = inspect_files(request["paths"], request["cache"], progress)
        elif request["action"] == "convert":
            result = convert(request, progress)
        else:
            raise ValueError("Unknown operation.")
        emit({"type": "result", "result": result})
    except Exception as exc:
        emit({"type": "error", "message": str(exc)})
        sys.exit(1)


if __name__ == "__main__":
    main()
