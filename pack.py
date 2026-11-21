"""Package a tested native Windows publish and the source needed to rebuild and test it."""

import argparse
import hashlib
import json
import os
import shutil
import tempfile
from pathlib import Path
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile


GUIDES = Path(__file__).resolve().parent
ROOT = GUIDES if (GUIDES / "SteamUnify2").is_dir() else GUIDES.parent
APP = ROOT / "SteamUnify2"
PROJECT = APP / "SteamUnify.csproj"
VERSION = ElementTree.parse(PROJECT).findtext("PropertyGroup/Version")
TITLE = f"SteamUnify-{VERSION}"
RESOURCES = ("category_model.json", "genres_zh.json", "heybox_names.json", "official_names.json", "tag_names_zh.json")
DOC_ASSETS = ("docs/images/library.png", "docs/images/translation.png", "docs/translation-sources-2.8.1.csv",
              "docs/popular-game-name-sources-2.8.2.csv", "docs/expanded-game-name-sources-2.8.3.csv",
              "docs/heybox-name-sources-2.8.4.csv")
TESTS = {
    "suggestion-tests": ("SuggestionsTests.csproj",),
    "io-tests": ("IoTests.csproj",),
    "cache-tests": ("CacheTests.csproj", "verify.py"),
    "acceptance": ("Acceptance.csproj", "fixtures.py"),
}


def source_files():
    files = {n: GUIDES / n for n in ("README.md", "BUILD.md", "LICENSE", "pack.py")}
    files.update({name: GUIDES / name for name in DOC_ASSETS})
    owned = [PROJECT, APP / "App.ico", APP / "app.manifest", *APP.glob("*.xaml"), *APP.glob("*.cs")]
    owned += [APP / "Resources" / name for name in RESOURCES]
    for folder, names in TESTS.items():
        test_dir = ROOT / folder
        owned += [test_dir / name for name in names]
        owned += list(test_dir.glob("*.cs"))
    files.update({p.relative_to(ROOT).as_posix(): p for p in owned})
    for name, path in files.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        if path.suffix == ".csproj":
            for element in ElementTree.parse(path).iter():
                include = element.get("Include", "")
                if element.tag not in {"Compile", "Content", "Resource"}:
                    continue
                for pattern in include.replace("\\", "/").split(";"):
                    directory, leaf = os.path.split(pattern)
                    matches = list((path.parent / directory).glob(leaf))
                    if not matches or any(p.resolve().relative_to(ROOT).as_posix() not in files for p in matches):
                        raise ValueError(f"Missing project input for {name}: {pattern}")
    return files


def runtime_notices():
    # Single-file publishing creates a publish-specific dependency manifest.
    # A subsequent framework-dependent test build can replace the ordinary bin manifest.
    relpath = Path("Release/net10.0-windows/win-x64/SteamUnify.deps.json")
    for folder in ("obj", "bin"):
        depfile = APP / folder / relpath
        if depfile.is_file():
            deps = json.loads(depfile.read_text(encoding="utf-8-sig"))["libraries"]
            if any(key.startswith("runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/") for key in deps):
                break
    else:
        raise FileNotFoundError("Build the self-contained Windows x64 publish using BUILD.md before packaging.")
    assets = json.loads((APP / "obj/project.assets.json").read_text(encoding="utf-8-sig"))
    caches = [Path(path) for path in assets["packageFolders"]]
    if os.environ.get("NUGET_PACKAGES"):
        caches.insert(0, Path(os.environ["NUGET_PACKAGES"]))
    files = {}
    for pack in ("Microsoft.NETCore.App.Runtime.win-x64", "Microsoft.WindowsDesktop.App.Runtime.win-x64"):
        prefix = "runtimepack." + pack + "/"
        version = next(key[len(prefix):] for key in deps if key.startswith(prefix))
        folder = next((root / pack.lower() / version for root in caches if (root / pack.lower() / version).is_dir()), None)
        if folder is None:
            raise FileNotFoundError(f"{pack} {version} not found in NuGet caches from {depfile}")
        notices = [f for f in folder.iterdir() if f.is_file() and (f.stem.lower() == "license" or "third-party-notices" in f.name.lower())]
        if not any(f.stem.lower() == "license" for f in notices):
            raise FileNotFoundError(f"Missing runtime license in {folder}")
        for path in notices:
            files[f"licenses/{pack}-{version}/{path.name}"] = path
    return files


def portable_files(publish):
    expected = {"SteamUnify.exe", *("Resources/" + name for name in RESOURCES)}
    actual = {p.relative_to(publish).as_posix() for p in publish.rglob("*") if p.is_file() and p.suffix.lower() != ".pdb"}
    if actual != expected:
        raise ValueError(f"Use a clean single-file publish. Missing: {expected - actual}. Unexpected: {actual - expected}.")
    files = {name: publish / name for name in expected}
    files.update({name: GUIDES / name for name in ("README.md", "LICENSE")})
    files.update({name: GUIDES / name for name in DOC_ASSETS})
    files.update(runtime_notices())
    return files


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.digest()


def make_archive(stage, filename, label, files):
    archive = stage / filename
    with ZipFile(archive, "w", ZIP_DEFLATED, compresslevel=6) as output:
        for entry, path in sorted(files.items()):
            output.write(path, label + "/" + entry)
    unpack = stage / (label + "-extracted")
    with ZipFile(archive) as verify:
        expected = {label + "/" + entry for entry in files}
        if set(verify.namelist()) != expected or verify.testzip() is not None:
            raise RuntimeError(f"{filename} failed archive verification")
        verify.extractall(unpack)
    for entry, original in files.items():
        if digest(unpack / label / entry) != digest(original):
            raise RuntimeError(f"{filename}: extracted bytes differ for {entry}")
    return archive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, default=ROOT / "wpf-28-publish")
    parser.add_argument("--output", type=Path, default=ROOT / "release")
    parser.add_argument("--check-only", action="store_true", help="validate packaging inputs without writing archives")
    args = parser.parse_args()
    portable = portable_files(args.publish.resolve())
    source = source_files()
    print(f"Validated {len(portable)} portable files and {len(source)} source files.")
    if args.check_only:
        return
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="pack-", dir=ROOT) as temp:
        stage = Path(temp)
        outputs = [
            make_archive(stage, TITLE + "-win-x64.zip", TITLE, portable),
            make_archive(stage, TITLE + "-source.zip", TITLE + "-source", source),
        ]
        for archive in outputs:
            target = args.output / archive.name
            shutil.move(archive, target)
            print(f"{target} ({target.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
