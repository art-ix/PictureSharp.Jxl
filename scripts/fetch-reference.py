"""Fetch the pinned libjxl source archive (Python 3.12+; no build or submodules)."""
import hashlib
import json
from pathlib import Path
import tarfile
import tempfile
import urllib.request

root = Path(__file__).resolve().parent.parent
lock = json.loads((root / "scripts/reference-lock.json").read_text())
reference = root / "reference"
reference.mkdir(exist_ok=True)
target = root / lock["sourceDirectory"]
if target.exists():
    raise SystemExit(f"Already present: {target}; leaving reference sources unchanged.")
archive = reference / f"libjxl-{lock['tag']}.tar.gz"
with urllib.request.urlopen(lock["archiveUrl"], timeout=60) as response:
    with archive.open("wb") as output:
        while chunk := response.read(1024 * 1024):
            output.write(chunk)
if hashlib.sha256(archive.read_bytes()).hexdigest() != lock["sha256"]:
    raise SystemExit("Archive SHA-256 mismatch; sources were not extracted.")
with tempfile.TemporaryDirectory(dir=reference) as staging:
    with tarfile.open(archive) as source:
        source.extractall(staging, filter="data")
    (Path(staging) / f"libjxl-{lock['commit']}").rename(target)
print(f"Verified {lock['tag']} at {lock['commit']}: {target}")
