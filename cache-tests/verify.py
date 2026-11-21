"""Exercise native cache reads, writes and restores on isolated v29/v30 fixtures.

From the test project, build CacheTests.csproj and run:
    python verify.py path/to/CacheTests.dll path/to/fixture-dir --dotnet path/to/dotnet
If the original Python package is available, pass --upstream for a second writer comparison.
"""
import argparse
import hashlib
import struct
import subprocess
import sys
import zlib
from pathlib import Path

args = argparse.ArgumentParser()
args.add_argument("runner", type=Path)
args.add_argument("scratch", type=Path)
args.add_argument("--dotnet", default="dotnet")
args.add_argument("--upstream", type=Path)
args = args.parse_args()
KEYS = ["appinfo", "common", "name", "type", "genres", "0", "1", "store_tags",
        "name_localized", "schinese", "english", "opaque", "words"]
KEY_DATA = b"".join(s.encode() + b"\0" for s in KEYS)
MAGIC29, MAGIC30 = 0x07564429, 0x0756442A
TARGET = 1100001


def node(kind, key, body):
    return bytes([kind]) + struct.pack("<I", KEYS.index(key)) + body


def obj(key, *children):
    return node(0, key, b"".join(children) + b"\x08")


def string(key, text):
    return node(1, key, text.encode() + b"\0")


def integer(key, val):
    return node(2, key, struct.pack("<i", val))


def game(appid, changed):
    replace = changed and appid == TARGET
    common = [
        string("type", "Game"),
        string("name", "本地化验证" if replace else f"Test Game {appid}"),
        obj("genres", integer("0", 1), integer("1", 2)),
        obj("store_tags", integer("0", 19), integer("1", 492)),
        node(5, "words", "测A试".encode("utf-16-le") + b"\0\0"),
    ]
    if not replace:
        common.append(obj("name_localized", string("schinese", f"游戏 {appid}"), string("english", f"Test Game {appid}")))
    return obj("appinfo", obj("common", *common), node(7, "opaque", bytes(range(8)))) + b"\x08"


def file29(changed, padding):
    rows = []
    for appid in range(TARGET, TARGET + 3):
        vdf = game(appid, changed)
        rows.append(struct.pack("<IIII", appid, 60 + len(vdf), 9, 47)
                    + bytes(range(32)) + hashlib.sha1(vdf).digest() + vdf)
    entries = b"".join(rows) + bytes(padding)
    return struct.pack("<IIQ", MAGIC29, 1, 16 + len(entries)) + entries + struct.pack("<I", len(KEYS)) + KEY_DATA


def file30(changed, stride, prefix, suffix, include_markers):
    index = bytearray()
    blobs = bytearray()
    count = 3
    for appid in range(TARGET, TARGET + count):
        vdf = game(appid, changed)
        blob = hashlib.sha1(vdf).digest() + vdf
        index += struct.pack("<III", appid, len(blobs), len(blob))
        index += bytes((appid + i) & 255 for i in range(stride - 12))
        blobs += blob
    index = bytes(index)
    raw_index = prefix + index + suffix
    crc = zlib.crc32((raw_index if include_markers else index) + KEY_DATA)
    header = struct.pack("<10I", MAGIC30, 1, 1, 256, count, len(raw_index), len(KEYS), len(KEY_DATA), len(blobs), crc)
    return header + struct.pack("<I", zlib.crc32(header)) + raw_index + KEY_DATA + blobs


def test(name, generate, sharded, upstream=False):
    root = args.scratch.resolve() / name
    if sharded:
        path = root / "appcache/appinfo/appinfo_01.vdf"
    else:
        path = root / "appcache/appinfo.vdf"
    path.parent.mkdir(parents=True, exist_ok=True)
    original = generate(False)
    path.write_bytes(original)
    if sharded:
        # Empty Steam shards are valid alongside a nonempty one.
        header = struct.pack("<10I", MAGIC30, 1, 2, 256, 0, 0, 0, 0, 0, 0)
        (path.parent / "appinfo_02.vdf").write_bytes(header + struct.pack("<I", zlib.crc32(header)))
    expected = generate(True)
    if args.upstream and upstream:
        sys.path.insert(0, str(args.upstream.resolve()))
        from steam_zh import appinfo, appinfo_shards
        reference = (appinfo_shards.ShardFile if sharded else appinfo.AppInfo).load(path)
        reference.set_name(TARGET, "本地化验证")
        reference.remove_localized(TARGET)
        assert reference.build() == expected, f"Upstream disagrees with fixture {name}"
    subprocess.run([args.dotnet, str(args.runner.resolve()), str(root), str(TARGET)], check=True)
    assert Path(str(path) + ".actual").read_bytes() == expected, f"Native writer mismatch in {name}"
    assert path.read_bytes() == original, f"Native restore mismatch in {name}"
    return root


test("v29", lambda changed: file29(changed, 0), False, True)
test("v29-padding", lambda changed: file29(changed, 8), False)
p = b"\x03\0\0\0"
s = b"\x22\x11\x44\x33"
for name, stride, pre, suf, full, upstream in [
        ("v30", 80, b"", b"", False, True),
        ("v30-prefix", 80, p, b"", False, True),
        ("v30-suffix", 80, b"", s, False, True),
        ("v30-wide", 81, b"", b"", False, True),
        ("v30-wide-96", 96, b"", b"", False, True),
        ("v30-both", 80, p, s, False, True),
        ("v30-full-crc", 80, p, s, True, False),
        ("v30-wide-prefix", 81, p, b"", False, False)]:
    test(name, lambda changed: file30(changed, stride, pre, suf, full), True, upstream)
print("All 10 layouts passed with byte-exact writes and restores.")
