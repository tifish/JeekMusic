"""Collect license texts for restored NuGet packages; no network requests.

Run after dotnet publish: python desktop/tools/collect-licenses.py <publish-directory>
Upstream licenses absent from NuGet are checked into desktop/licenses with source URLs.
"""
import argparse
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

desktop = Path(__file__).resolve().parents[1]
def resolve_assets(project, explicit=None):
    if explicit:
        result = Path(explicit)
        if not result.is_file():
            raise ValueError(f"NuGet assets file does not exist: {result}")
        return result
    candidates = sorted((project / "obj/publish").rglob("project.assets.json"))
    if len(candidates) == 1:
        return candidates[0]
    raise ValueError("Pass --assets with the ProjectAssetsFile from the matching publish; "
                     f"found {len(candidates)} isolated publish graphs. Never use stale build/legacy assets.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish_directory", type=Path)
    parser.add_argument("--assets", type=Path, help="ProjectAssetsFile from the exact publish configuration")
    args = parser.parse_args()
    assets_path = resolve_assets(desktop / "src/JeekMusic.App", args.assets)
    collect(args.publish_directory, assets_path)


def collect(publish_directory, assets_path):
    destination = publish_directory / "licenses"
    destination.mkdir(parents=True, exist_ok=True)
    shutil.copytree(desktop / "licenses", destination / "upstream", dirs_exist_ok=True)
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    package_roots = [Path(p) for p in assets["packageFolders"]]
    index = ["Jeek Music third-party dependencies", "", "Package versions and licenses from the restored NuGet graph.",
             "Some packages below contain build tools or assets for other platforms; they are not all included in the Windows binary.",
             "Full upstream notices are in upstream/, NuGet notices in nuget/, and libmpv source provenance in libmpv/.",
             "", "YAMNet: Copyright 2019 The TensorFlow Authors. Apache-2.0.",
             "Model source: https://www.kaggle.com/models/google/yamnet",
             "The shipped ONNX file was converted from mobile/app/src/main/assets/yamnet.tflite using tools/yamnet-to-onnx.py.",
             "Ude.NetStandard: used under the MPL-1.1 option, without source modifications.",
             "Source: https://github.com/yinyue200/ude (NuGet Ude.NetStandard 1.2.0).", ""]
    for name, info in sorted(assets["libraries"].items()):
        if info["type"] != "package":
            continue
        package = next((p / info["path"] for p in package_roots if (p / info["path"]).is_dir()), None)
        if package is None:
            raise SystemExit("Missing restored package: " + name)
        nuspec = next(package.glob("*.nuspec"))
        metadata = next(iter(ET.parse(nuspec).getroot()))
        fields = {e.tag.split("}")[-1]: e.text for e in metadata}
        index.append(f"{name}: {fields.get('license', 'See package notices')}; {fields.get('projectUrl', '')}")
        target = destination / "nuget" / name.replace("/", "-")
        target.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(nuspec, target / nuspec.name)
        for file in package.rglob("*"):
            if file.is_file() and any(word in file.name.lower() for word in ("license", "licence", "notice", "copying")):
                output = target / file.relative_to(package)
                output.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(file, output)
    (destination / "README.txt").write_text("\n".join(index) + "\n", encoding="utf-8")
    shutil.copyfile(desktop.parent / "LICENSE", publish_directory / "LICENSE.txt")
    print(f"Collected {len(assets['libraries'])} dependency records in {destination}")


if __name__ == "__main__":
    main()
