"""Verify that an upgrade preserves the exact JSON of existing packets/history/artifacts."""

import argparse
from contextlib import closing
import hashlib
import json
import sqlite3
from pathlib import Path


def snapshot(database):
    queries = {
        "packets": "SELECT id, json FROM packets",
        "revisions": "SELECT packet_id || ':' || revision, json FROM revisions",
        "artifacts": "SELECT packet_id || ':' || name, json FROM artifacts",
    }
    result = {}
    with closing(sqlite3.connect(Path(database).resolve().as_uri() + "?mode=ro", uri=True)) as connection:
        for table, query in queries.items():
            result[table] = {
                key: hashlib.sha256(value.encode("utf-8")).hexdigest()
                for key, value in connection.execute(query)
            }
    return result


def verify(baseline, current):
    for table in ("packets", "revisions", "artifacts"):
        if not baseline.get(table):
            raise ValueError("Legacy proof requires nonempty packet, revision and artifact records")
        for key, digest in baseline[table].items():
            if current.get(table, {}).get(key) != digest:
                raise ValueError("An existing legacy record was changed or removed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("capture", "verify"))
    parser.add_argument("database", type=Path)
    parser.add_argument("baseline", type=Path)
    args = parser.parse_args()
    try:
        current = snapshot(args.database)
        if args.action == "capture":
            verify(current, current)
            with args.baseline.open("x", encoding="utf-8") as output:
                json.dump(current, output)
        else:
            verify(json.loads(args.baseline.read_text(encoding="utf-8")), current)
    except (OSError, sqlite3.Error, ValueError, TypeError, KeyError):
        parser.exit(2, "Legacy-record preservation proof failed.\n")
    print("Legacy packet, revision and artifact record hashes " + ("captured." if args.action == "capture" else "preserved."))


if __name__ == "__main__":
    main()
