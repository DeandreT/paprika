"""Write the tracked source of a commit to a reproducible archive. Requires Git and Python 3."""

import argparse
import gzip
import hashlib
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def snapshot(ref, destination):
    root = Path(__file__).resolve().parents[1]
    def git(*args):
        return subprocess.check_output(["git", "-C", str(root), *args])

    commit = git("rev-parse", "--verify", "--end-of-options", f"{ref}^{{commit}}").decode().strip()
    tree = git("rev-parse", f"{commit}^{{tree}}").decode().strip()
    entries = git("ls-tree", "-rz", tree).split(b"\0")
    if any(entry and not entry.startswith((b"100644 blob ", b"100755 blob ")) for entry in entries):
        raise ValueError("Review the snapshot exporter before shipping symlinks or submodules.")
    # Archive a tree, not a commit: no previous commits, author metadata or PAX commit ID.
    # Ignore files absent from this tree, even if staged or present in the working directory.
    with tempfile.TemporaryDirectory() as tmp:
        tar = Path(tmp) / "source.tar"
        git("-c", "core.autocrlf=false", "-c", "tar.umask=0002",
            "archive", "--format=tar", "--prefix=paprika/", "--mtime=2000-01-01T00:00:00Z",
            f"--output={tar}", tree)
        with destination.open("xb") as output:
            try:
                with gzip.GzipFile(fileobj=output, filename="", mode="wb", mtime=0) as archive, tar.open("rb") as source:
                    shutil.copyfileobj(source, archive)
            except BaseException:
                output.close()
                destination.unlink()
                raise
    print(f"Source commit: {commit}\nSource tree:   {tree}")
    print(f"SHA-256:       {hashlib.sha256(destination.read_bytes()).hexdigest()}  {destination.name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ref", help="Reviewed commit or release tag")
    parser.add_argument("output", type=Path, help="New .tar.gz file; never overwrites")
    args = parser.parse_args()
    try:
        snapshot(args.ref, args.output)
    except (ValueError, OSError, subprocess.CalledProcessError) as exc:
        sys.exit(str(exc))
