"""End-to-end tests against the actual Windows worker and LibreOffice engine."""
import argparse
import hashlib
import json
import os
import subprocess
import uuid
import zipfile
from pathlib import Path

from pypdf import PdfReader

parser = argparse.ArgumentParser()
parser.add_argument("--app", required=True)
parser.add_argument("--engine", required=True)
parser.add_argument("--corpus", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
app, corpus, output = Path(args.app).resolve(), Path(args.corpus).resolve(), Path(args.output).resolve()
required_native = ["Magick.Native-Q8-x64.dll", "pdfium.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "wic_heic.dll"]
missing = [name for name in required_native if not (app.parent / name).is_file()]
if missing:
    raise SystemExit("Incomplete application; tests were not started. Missing: " + ", ".join(missing))
output.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, MORPHY_ENGINE_PATH=str(Path(args.engine).resolve()))
routes = {"docx": ["pdf"], "doc": ["pdf", "docx"], "odt": ["pdf", "docx"], "rtf": ["pdf", "docx"],
          "txt": ["pdf", "docx"], "md": ["pdf", "docx", "html"], "xlsx": ["pdf", "csv"],
          "xls": ["pdf", "xlsx", "csv"], "ods": ["pdf", "xlsx", "csv"], "csv": ["pdf", "xlsx"],
          "pptx": ["pdf"], "ppt": ["pdf", "pptx"], "odp": ["pdf", "pptx"]}
results = []
pdfs = []

def convert(source, target, case):
    destination = output / case
    if destination.exists():
        destination = output / (case + '-' + uuid.uuid4().hex[:8])
    destination.mkdir(exist_ok=True)
    original = hashlib.sha256(source.read_bytes()).hexdigest()
    process = subprocess.run([str(app), "--convert", str(source), target, str(destination)], env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=330)
    files = sorted(destination.glob("*." + target))
    assert process.returncode == 0, process.stderr or process.stdout or f"exit {process.returncode}"
    assert files, "No outputs"
    assert hashlib.sha256(source.read_bytes()).hexdigest() == original, "Original modified"
    assert not list(destination.glob(".morphy-*")), "Staging directory leaked"
    if target == "pdf":
        for file in files:
            with file.open('rb') as stream:
                pdf = PdfReader(stream)
                assert len(pdf.pages) > 0
                text = "".join(page.extract_text() for page in pdf.pages)
                assert any("가" <= c <= "힣" for c in text), f"Korean text missing: {text[:100]}"
                assert pdf.pages[0].mediabox.width > 0
            pdfs.append(file)
    if source.suffix.lower() == ".md":
        # Markdown tables must come out as tables, never as literal "| a | b |" text.
        for file in files:
            if target == "html":
                html = file.read_text(encoding="utf-8")
                assert "<table" in html and "|---" not in html, "Markdown table missing in HTML"
            elif target == "docx":
                with zipfile.ZipFile(file) as docx:
                    assert "<w:tbl>" in docx.read("word/document.xml").decode("utf-8"), "Markdown table missing in DOCX"
            elif target == "pdf":
                with file.open('rb') as stream:
                    text = "".join(page.extract_text() for page in PdfReader(stream).pages)
                assert "|---" not in text and "| " not in text, "Markdown table printed as pipe text in PDF"
    if target == "csv":
        assert all("�" not in file.read_text(encoding="utf-8-sig") for file in files)
        # utf-8-sig reads with or without a BOM; Korean Excel needs it, so require it explicitly.
        assert all(file.read_bytes().startswith(b"\xef\xbb\xbf") for file in files), "CSV must start with a UTF-8 BOM for Excel"
        if source.stem.startswith("매출"):
            assert len(files) == 2, "Multi-sheet CSV output count"
            assert any("SHEET-TWO" in f.read_text(encoding="utf-8-sig") for f in files)
    return files

for source in sorted(corpus.iterdir()):
    for target in routes.get(source.suffix[1:].lower(), []):
        case = f"{len(results):03d}-{source.suffix[1:]}-{target}"
        try:
            files = convert(source, target, case)
            result = {"input": source.name, "target": target, "status": "PASS", "outputs": [str(f) for f in files]}
        except Exception as error:
            result = {"input": source.name, "target": target, "status": "FAIL", "error": str(error)}
        results.append(result)
        print(result["status"], source.name, "->", target, flush=True)

if pdfs:
    pdf = next((p for p in pdfs if "보고서" in p.name), pdfs[0])
    for target in ["png", "txt", "heic", "webp"]:
        try:
            files = convert(pdf, target, f"pdf-{target}")
            with pdf.open('rb') as stream:
                doc = PdfReader(stream)
                if target != "txt":
                    assert len(files) == len(doc.pages), "Page image count"
                else:
                    extracted = files[0].read_text(encoding="utf-8")
                    assert "한국어" in extracted, "Korean PDF text extraction"
                    assert "END-OF-DOC" in extracted, "Last PDF page text missing"
            result = {"input": pdf.name, "target": target, "status": "PASS"}
        except Exception as error:
            result = {"input": pdf.name, "target": target, "status": "FAIL", "error": str(error)}
        results.append(result)
        print(result["status"], "PDF ->", target, flush=True)

(output / "report.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
failures = [r for r in results if r["status"] == "FAIL"]
print(f"{len(results) - len(failures)}/{len(results)} passed", flush=True)
raise SystemExit(bool(failures))
