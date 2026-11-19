# Source releases

Export the exact reviewed commit from the repository root using Python 3 and Git with `archive --mtime` support:

```sh
python3 scripts/source-snapshot.py HEAD paprika-source.tar.gz
```

The archive contains the committed tree under `paprika/`, including dependency locks, build scripts, and tests. It excludes Git history, remotes, local settings outside the tree and untracked build outputs. The exporter prints the source commit, tree ID and archive SHA-256. It refuses to replace an existing file.

Extract the archive in a new directory and build from that directory:

```sh
tar -xzf paprika-source.tar.gz
cd paprika
docker build --platform linux/amd64 -t paprika:source-check .
bash scripts/check-container.sh paprika:source-check
```

Record the release tag, source archive checksum and tested image digest together. Distribute the corresponding source with each binary release, including applicable third-party license notices and any corresponding source required for bundled components. The application source archive alone is not a source distribution for third-party binary dependencies.
