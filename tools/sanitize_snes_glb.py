#!/usr/bin/env python3
"""Remove embedded third-party imagery from the licensed SNES cartridge GLB.

The source mesh is CC BY-NC 4.0. This keeps its geometry/UVs and attribution,
replaces the material with a neutral PBR material, removes all image/texture
objects, and rebuilds the binary chunk so the JPEG bytes are not retained.
"""

from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path


GLB_MAGIC = b"glTF"
JSON_CHUNK = 0x4E4F534A
BIN_CHUNK = 0x004E4942


def align4(value: int) -> int:
    return (value + 3) & ~3


def read_glb(path: Path) -> tuple[dict, bytes]:
    data = path.read_bytes()
    magic, version, total_length = struct.unpack_from("<4sII", data, 0)
    if magic != GLB_MAGIC or version != 2 or total_length != len(data):
        raise ValueError(f"{path} is not a valid GLB 2.0 file")

    offset = 12
    document = None
    binary = b""
    while offset < len(data):
        chunk_length, chunk_type = struct.unpack_from("<II", data, offset)
        offset += 8
        chunk = data[offset : offset + chunk_length]
        offset += chunk_length
        if chunk_type == JSON_CHUNK:
            document = json.loads(chunk.decode("utf-8").rstrip(" \t\r\n\0"))
        elif chunk_type == BIN_CHUNK:
            binary = chunk
    if document is None:
        raise ValueError(f"{path} has no JSON chunk")
    return document, binary


def sanitize(document: dict, binary: bytes) -> tuple[dict, bytes]:
    image_views = {
        image["bufferView"]
        for image in document.get("images", [])
        if "bufferView" in image
    }

    old_views = document.get("bufferViews", [])
    view_map: dict[int, int] = {}
    new_views: list[dict] = []
    new_binary = bytearray()
    for old_index, view in enumerate(old_views):
        if old_index in image_views:
            continue
        source_offset = view.get("byteOffset", 0)
        source_length = view["byteLength"]
        destination_offset = align4(len(new_binary))
        new_binary.extend(b"\0" * (destination_offset - len(new_binary)))
        new_binary.extend(binary[source_offset : source_offset + source_length])

        replacement = dict(view)
        replacement["byteOffset"] = destination_offset
        view_map[old_index] = len(new_views)
        new_views.append(replacement)

    for accessor in document.get("accessors", []):
        if "bufferView" in accessor:
            accessor["bufferView"] = view_map[accessor["bufferView"]]

    document["bufferViews"] = new_views
    document["buffers"] = [{"byteLength": len(new_binary)}]
    document.pop("images", None)
    document.pop("textures", None)
    document.pop("samplers", None)

    document["materials"] = [{
        "name": "BR_Libretro_Neutral_Cartridge",
        "doubleSided": True,
        "pbrMetallicRoughness": {
            "baseColorFactor": [0.42, 0.41, 0.39, 1.0],
            "metallicFactor": 0.0,
            "roughnessFactor": 0.78,
        },
    }]

    used = [name for name in document.get("extensionsUsed", [])
            if name != "KHR_materials_pbrSpecularGlossiness"]
    required = [name for name in document.get("extensionsRequired", [])
                if name != "KHR_materials_pbrSpecularGlossiness"]
    if used:
        document["extensionsUsed"] = used
    else:
        document.pop("extensionsUsed", None)
    if required:
        document["extensionsRequired"] = required
    else:
        document.pop("extensionsRequired", None)

    asset = document.setdefault("asset", {"version": "2.0"})
    extras = asset.setdefault("extras", {})
    extras["brLibretroModifications"] = (
        "Embedded texture and branded imagery removed; material replaced with "
        "a neutral cartridge surface for runtime game artwork."
    )
    asset["generator"] = "BR-Libretro sanitize_snes_glb.py"
    return document, bytes(new_binary)


def write_glb(path: Path, document: dict, binary: bytes) -> None:
    json_bytes = json.dumps(document, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    json_bytes += b" " * (align4(len(json_bytes)) - len(json_bytes))
    binary += b"\0" * (align4(len(binary)) - len(binary))
    total_length = 12 + 8 + len(json_bytes) + 8 + len(binary)
    output = bytearray(struct.pack("<4sII", GLB_MAGIC, 2, total_length))
    output.extend(struct.pack("<II", len(json_bytes), JSON_CHUNK))
    output.extend(json_bytes)
    output.extend(struct.pack("<II", len(binary), BIN_CHUNK))
    output.extend(binary)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(output)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    document, binary = read_glb(args.source)
    document, binary = sanitize(document, binary)
    write_glb(args.destination, document, binary)


if __name__ == "__main__":
    main()
