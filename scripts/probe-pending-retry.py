"""Read-only assertion of a stopped synthetic verification database, not a repair tool."""

import json
from pathlib import Path
import sqlite3
import sys


def main() -> None:
    database, delivery_id = Path(sys.argv[1]).resolve(), sys.argv[2]
    connection = sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)
    try:
        connection.execute("PRAGMA query_only = ON")
        work = connection.execute(
            "SELECT work_id FROM queue_work WHERE kind='delivery' AND id=?",
            (delivery_id,),
        ).fetchone()
        if not work or not work[0]:
            raise RuntimeError("No durable delivery generation was recorded.")
        row = connection.execute(
            "SELECT response FROM deliveries WHERE id=?", (delivery_id,)
        ).fetchone()
        if not row:
            raise RuntimeError("Delivery is missing from the stopped database.")
        delivery = json.loads(row[0])
        pending = connection.execute(
            "SELECT count(*) FROM wolverine_incoming_envelopes "
            "WHERE received_at='local://delivery/' AND status<>'Handled' "
            "AND instr(body, CAST(? AS BLOB))>0",
            (work[0],),
        ).fetchone()[0]
        dead = connection.execute(
            "SELECT count(*) FROM wolverine_dead_letters "
            "WHERE instr(body, CAST(? AS BLOB))>0",
            (work[0],),
        ).fetchone()[0]
        if not pending or dead or delivery["status"] not in ("queued", "processing"):
            raise RuntimeError(
                "Crash test was inconclusive: the stopped generation is not pending "
                "without a dead letter. Do not claim retry recovery from this run."
            )
        if delivery["attempts"] < 1:
            raise RuntimeError("The stopped delivery has not attempted its simulated transport.")
        print(json.dumps({"attempts": delivery["attempts"], "pending": pending}))
    finally:
        connection.close()


if __name__ == "__main__":
    main()
