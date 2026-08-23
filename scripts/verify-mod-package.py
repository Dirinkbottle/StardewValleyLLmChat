#!/usr/bin/env python3

import argparse
import json
from pathlib import PurePosixPath
from zipfile import BadZipFile, ZipFile


def fail(message: str) -> None:
    raise ValueError(message)


def verify_archive(archive_path: str) -> None:
    try:
        with ZipFile(archive_path) as archive:
            broken_entry = archive.testzip()
            if broken_entry is not None:
                fail(f"CRC check failed for {broken_entry}")

            file_names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
            if not file_names:
                fail("archive is empty")

            casefolded_names: set[str] = set()
            for name in file_names:
                if "\\" in name:
                    fail(f"non-portable backslash path: {name}")

                path = PurePosixPath(name)
                if path.is_absolute() or ".." in path.parts:
                    fail(f"unsafe archive path: {name}")

                casefolded = name.casefold()
                if casefolded in casefolded_names:
                    fail(f"case-insensitive path collision: {name}")
                casefolded_names.add(casefolded)

            manifest_names = [name for name in file_names if PurePosixPath(name).name == "manifest.json"]
            if len(manifest_names) != 1:
                fail(f"expected exactly one manifest.json, found {len(manifest_names)}")

            manifest_name = manifest_names[0]
            manifest = json.loads(archive.read(manifest_name).decode("utf-8-sig"))
            entry_dll = str(manifest.get("EntryDll", "")).strip()
            unique_id = str(manifest.get("UniqueID", "")).strip()
            if not entry_dll:
                fail("manifest EntryDll is empty")
            if unique_id != "Dirinkbottle.StardewValleyLLmChat":
                fail(f"unexpected manifest UniqueID: {unique_id or '<empty>'}")

            mod_root = PurePosixPath(manifest_name).parent
            required_paths = (
                mod_root / entry_dll,
                mod_root / "mod.toml",
                mod_root / "image" / "chatbox" / "chatbox-sheet.png",
            )
            file_name_set = set(file_names)
            for required_path in required_paths:
                required_name = required_path.as_posix()
                if required_name not in file_name_set:
                    fail(f"missing required mod file: {required_name}")
    except BadZipFile as error:
        fail(f"invalid zip archive: {error}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify a Linux-safe StardewMod release zip.")
    parser.add_argument("archives", nargs="+", help="Release zip files to verify.")
    args = parser.parse_args()

    for archive_path in args.archives:
        verify_archive(archive_path)
        print(f"package OK: {archive_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
