"""Offline integration tests. Usage: python test_engine.py TEST_DIRECTORY [HEIC_FILE]."""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import socket
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import engine
from PIL import Image
from pypdf import PdfReader, PdfWriter
from reportlab.pdfgen import canvas
import pypdfium2 as pdfium

TEST_DIR = Path(sys.argv[1]).resolve()
HEIC = Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else None
sys.argv = sys.argv[:1]
TEST_DIR.mkdir(parents=True, exist_ok=True)


def fixtures():
    Image.new("RGB", (300, 180), "red").save(TEST_DIR / "red.png")
    Image.new("RGB", (180, 300), "blue").save(TEST_DIR / "blue.png")
    Image.new("RGBA", (100, 80), (20, 40, 80, 0)).save(TEST_DIR / "transparent.png")
    pdf = canvas.Canvas(str(TEST_DIR / "sample.pdf"), pagesize=(400, 500))
    for name, color in [("PAGE A", (0.1, 0.6, 0.1)), ("PAGE B", (0.6, 0.1, 0.6))]:
        pdf.setFillColorRGB(*color)
        pdf.rect(0, 0, 400, 500, fill=1, stroke=0)
        pdf.setFillColorRGB(1, 1, 1)
        pdf.setFont("Helvetica", 35)
        pdf.drawString(35, 350, name)
        pdf.showPage()
    pdf.save()


def center_color(path, index):
    with pdfium.PdfDocument(str(path)) as doc:
        with contextlib.closing(doc[index]) as page:
            bitmap = page.render(scale=0.5)
            try:
                image = bitmap.to_pil().convert("RGB")
                return image.getpixel((image.width // 2, image.height // 2))
            finally:
                bitmap.close()


class EngineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        fixtures()
        engine.capabilities()

    def inspect(self, *names):
        result = engine.inspect_files([str(TEST_DIR / name) for name in names], TEST_DIR / "cache")
        self.assertEqual(result["errors"], [])
        return result["items"]

    def convert(self, items, target, **kwargs):
        result = engine.convert({"items": items, "format": target, "output": str(TEST_DIR / self._testMethodName), **kwargs})
        self.assertEqual(result["errors"], [])
        return [Path(path) for path in result["outputs"]]

    def test_common_formats_round_trip(self):
        item = self.inspect("red.png")[0]
        for fmt in engine.capabilities()["formats"]:
            output = self.convert([item], fmt)[0]
            with Image.open(output) as image:
                self.assertEqual(image.size, (300, 180))
                red, green, blue = image.convert("RGB").getpixel((100, 100))
                self.assertGreater(red, 240)
                self.assertLess(green, 12)
                self.assertLess(blue, 12)
            second = engine.inspect_files([str(output)], TEST_DIR / "cache")
            self.assertFalse(second["errors"])
            png = self.convert(second["items"], "PNG")[0]
            with Image.open(png) as image:
                self.assertEqual(image.size, (300, 180))

    def test_order_mixed_pdf_vector_text_and_rotation(self):
        items = self.inspect("red.png", "sample.pdf", "blue.png")
        items[3]["rotation"] = 90
        ordered = [items[3], items[2], items[0], items[1], dict(items[2])]
        output = self.convert(ordered, "PDF", name="Mixed ordered")[0]
        with contextlib.closing(PdfReader(output)) as pdf:
            self.assertEqual(len(pdf.pages), 5)
            self.assertIn("PAGE B", pdf.pages[1].extract_text())
            self.assertIn("PAGE A", pdf.pages[3].extract_text())
            self.assertIn("PAGE B", pdf.pages[4].extract_text())
            self.assertGreater(float(pdf.pages[0].mediabox.width), float(pdf.pages[0].mediabox.height))
        self.assertEqual(center_color(output, 0), (0, 0, 255))
        self.assertEqual(center_color(output, 2), (255, 0, 0))
        # Save representative rendered pages for visual QA.
        with pdfium.PdfDocument(str(output)) as doc:
            for index in [0, 1, 2, 3]:
                with contextlib.closing(doc[index]) as page:
                    bitmap = page.render(scale=1)
                    try:
                        bitmap.to_pil().save(TEST_DIR / ("pdf-preview-" + str(index + 1) + ".png"))
                    finally:
                        bitmap.close()

    def test_pdf_rotation_does_not_leak_to_duplicate(self):
        items = self.inspect("sample.pdf")
        first = dict(items[0], rotation=90)
        second = dict(items[0], rotation=0)
        output = self.convert([first, second], "PDF")[0]
        with contextlib.closing(PdfReader(output)) as pdf:
            self.assertEqual(pdf.pages[0].rotation, 90)
            self.assertEqual(pdf.pages[1].rotation, 0)

    def test_merge_multiple_pdf_files(self):
        doc = canvas.Canvas(str(TEST_DIR / "second.pdf"), pagesize=(300, 400))
        doc.drawString(40, 300, "PAGE C")
        doc.showPage()
        doc.drawString(40, 300, "PAGE D")
        doc.showPage()
        doc.save()
        items = self.inspect("sample.pdf", "second.pdf")
        output = self.convert([items[1], items[3], items[0], items[2]], "PDF")[0]
        with contextlib.closing(PdfReader(output)) as pdf:
            self.assertEqual(len(pdf.pages), 4)
            for page, expected in zip(pdf.pages, ["PAGE B", "PAGE D", "PAGE A", "PAGE C"]):
                self.assertIn(expected, page.extract_text())

    def test_pdf_to_image_with_rotation(self):
        item = self.inspect("sample.pdf")[0]
        item["rotation"] = 90
        output = self.convert([item], "PNG", dpi=144)[0]
        with Image.open(output) as image:
            self.assertEqual(image.size, (1000, 800))

    def test_transparency(self):
        item = self.inspect("transparent.png")[0]
        jpg = self.convert([item], "JPG")[0]
        png = self.convert([item], "PNG")[0]
        with Image.open(jpg) as image:
            self.assertEqual(image.getpixel((10, 10)), (255, 255, 255))
        with Image.open(png) as image:
            self.assertEqual(image.getpixel((10, 10))[3], 0)

    def test_camera_orientation_and_metadata(self):
        exif = Image.Exif()
        exif[274] = 6
        exif[270] = "PRIVATE CAMERA METADATA"
        image = Image.new("RGB", (100, 200), "green")
        image.save(TEST_DIR / "oriented.jpg", exif=exif)
        item = self.inspect("oriented.jpg")[0]
        self.assertEqual((item["width"], item["height"]), (200, 100))
        output = self.convert([item], "JPG")[0]
        with Image.open(output) as image:
            self.assertEqual(image.size, (200, 100))
            self.assertFalse(image.getexif())

    def test_tiff_pages(self):
        red = Image.new("RGB", (90, 70), "red")
        blue = Image.new("RGB", (70, 90), "blue")
        red.save(TEST_DIR / "multi.tiff", save_all=True, append_images=[blue])
        items = self.inspect("multi.tiff")
        self.assertEqual(len(items), 2)
        output = self.convert(items[::-1], "PDF")[0]
        self.assertEqual(center_color(output, 0), (0, 0, 255))
        self.assertEqual(center_color(output, 1), (255, 0, 0))

    def test_existing_outputs_and_originals_are_unchanged(self):
        original = TEST_DIR / "red.png"
        digest = hashlib.sha256(original.read_bytes()).hexdigest()
        items = self.inspect("red.png")
        first = self.convert(items, "PNG")[0]
        prior = first.read_bytes()
        second = self.convert(items, "PNG")[0]
        self.assertNotEqual(first, second)
        self.assertEqual(prior, first.read_bytes())
        self.assertEqual(digest, hashlib.sha256(original.read_bytes()).hexdigest())

    def test_corrupt_and_locked_files(self):
        (TEST_DIR / "corrupt.png").write_bytes(b"not an image")
        writer = PdfWriter()
        writer.add_blank_page(width=100, height=100)
        writer.encrypt("secret")
        writer.write(TEST_DIR / "locked.pdf")
        writer.close()
        result = engine.inspect_files([str(TEST_DIR / "corrupt.png"), str(TEST_DIR / "locked.pdf"), str(TEST_DIR / "red.png")], TEST_DIR / "cache")
        self.assertEqual(len(result["errors"]), 2)
        self.assertEqual(len(result["items"]), 1)
        self.assertTrue(any("password" in e.lower() for e in result["errors"]))

    def test_atomic_pdf_failure_and_batch_errors(self):
        item = self.inspect("red.png")[0]
        missing = dict(item, path=str(TEST_DIR / "gone.png"), decoded=str(TEST_DIR / "gone.png"))
        folder = TEST_DIR / self._testMethodName
        with self.assertRaises(FileNotFoundError):
            engine.convert({"items": [item, missing], "format": "PDF", "output": str(folder)})
        self.assertFalse(list(folder.glob("*.pdf")))
        self.assertFalse(list(folder.glob("*.partial")))
        result = engine.convert({"items": [missing, item], "format": "PNG", "output": str(folder)})
        self.assertEqual(len(result["outputs"]), 1)
        self.assertEqual(len(result["errors"]), 1)

    def test_unicode_paths_and_letter_page_fit(self):
        name = "Résumé 测试.png"
        Image.new("RGB", (180, 300), "blue").save(TEST_DIR / name)
        items = self.inspect(name)
        output = self.convert(items, "PDF", name="My photos", page_size="Letter")[0]
        with contextlib.closing(PdfReader(output)) as pdf:
            self.assertEqual(tuple(map(float, pdf.pages[0].mediabox[2:])), (612, 792))
        self.assertEqual(center_color(output, 0), (0, 0, 255))

    def test_network_denied_before_connection(self):
        with socket.socket() as sock:
            with self.assertRaisesRegex(PermissionError, "network"):
                sock.connect(("127.0.0.1", 9))

    def test_real_heic_input(self):
        if HEIC is None:
            self.skipTest("No real HEIC supplied")
        original_hash = hashlib.sha256(HEIC.read_bytes()).hexdigest()
        result = engine.inspect_files([str(HEIC)], TEST_DIR / "cache")
        self.assertEqual(result["errors"], [])
        self.assertEqual(len(result["items"]), 1)
        item = result["items"][0]
        self.assertGreater(item["width"], 0)
        self.assertGreater(item["height"], 0)
        for target in ["JPG", "PNG", "PDF"]:
            output = self.convert([item], target)[0]
            self.assertTrue(output.stat().st_size > 1000)
        self.assertEqual(hashlib.sha256(HEIC.read_bytes()).hexdigest(), original_hash)
        Path(TEST_DIR / "real-heic-item.json").write_text(json.dumps(item), encoding="utf-8")


if __name__ == "__main__":
    unittest.main(verbosity=2)
