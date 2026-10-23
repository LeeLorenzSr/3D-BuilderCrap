"""Create two tiny, deliberately invalid local import probes; no fuzzing or huge counts."""
import hashlib
import json
from pathlib import Path

from generate_fixtures import stl_bytes, triangles


def main():
    destination = Path(__file__).resolve().parents[1] / "fixtures" / "failures"
    destination.mkdir(parents=True, exist_ok=True)
    cases = [
        ("truncated-cube.stl", stl_bytes(triangles())[:84],
         "84-byte binary STL header declares 12 triangles but contains no triangle records."),
        ("not-a-package.3mf", b"Original deliberately invalid 3MF fixture\n",
         "Plain text with a 3MF extension, not an OPC/ZIP package."),
    ]
    records = []
    for name, data, purpose in cases:
        (destination / name).write_bytes(data)
        records.append({"file": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                        "purpose": purpose})
    (destination / "manifest.json").write_text(json.dumps({"schema_version": 1,
        "provenance": "Original bounded invalid inputs from scripts/generate_failure_fixtures.py",
        "cases": records}, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(records)} bounded invalid inputs in {destination}")


if __name__ == "__main__":
    main()
