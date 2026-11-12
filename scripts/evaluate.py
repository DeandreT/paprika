"""Compare a frozen, pre-review packet JSON snapshot with independent reviewer labels.

No provider calls or private document content are included in the aggregate report.
"""

import argparse
import json
import re
from collections import Counter
from pathlib import Path


def page_map(packet):
    result = {}
    for page in packet["pages"]:
        if (not isinstance(page["id"], str) or not page["id"]
                or not isinstance(page["sourceId"], str) or not page["sourceId"]
                or type(page["pageNumber"]) is not int or page["pageNumber"] < 1):
            raise ValueError("Invalid source-page reference in prediction snapshot")
        if page["id"] in result:
            raise ValueError("Duplicate page ID in prediction snapshot")
        result[page["id"]] = (page["sourceId"], page["pageNumber"])
    if len(set(result.values())) != len(result):
        raise ValueError("Duplicate source-page reference in prediction snapshot")
    return result


def compare(prediction, truth):
    if prediction.get("status") != "review" or type(prediction.get("revision")) is not int or prediction["revision"] != 1:
        raise ValueError("Evaluation requires the initial review snapshot at revision 1")
    if prediction["id"] != truth["packetId"]:
        raise ValueError("Ground truth does not belong to this packet")
    pages = page_map(prediction)
    predicted_documents = [
        (doc["type"], tuple(pages[page_id] for page_id in doc["pageIds"]))
        for doc in prediction["documents"]
    ]
    actual_documents = [
        (doc["type"], tuple((p["sourceId"], p["pageNumber"]) for p in doc["pages"]))
        for doc in truth["documents"]
    ]
    for label, documents in [("prediction", predicted_documents), ("ground truth", actual_documents)]:
        if not documents or any(not members for _, members in documents):
            raise ValueError(f"Empty documents in {label}")
        if any(not isinstance(kind, str) or not kind.strip() for kind, _ in documents):
            raise ValueError(f"Missing document type in {label}")
        if any(not isinstance(source, str) or not source or type(number) is not int or number < 1
               for _, members in documents for source, number in members):
            raise ValueError(f"Invalid source-page reference in {label}")
        memberships = Counter(page for _, members in documents for page in members)
        if set(memberships) != set(pages.values()) or any(n != 1 for n in memberships.values()):
            raise ValueError(f"Each original page must occur exactly once in {label}")

    predicted_types = {page: kind for kind, members in predicted_documents for page in members}
    actual_types = {page: kind for kind, members in actual_documents for page in members}
    confusion = Counter((actual_types[page], predicted_types[page]) for page in pages.values())
    correct_pages = sum(n for (actual, predicted), n in confusion.items() if actual == predicted)
    # Exact membership AND ordering, not a set comparison. Interleaved documents are supported.
    actual_sequences = Counter(members for _, members in actual_documents)
    predicted_sequences = Counter(members for _, members in predicted_documents)
    reconstructed = sum((actual_sequences & predicted_sequences).values())
    fully_correct = sum((Counter(actual_documents) & Counter(predicted_documents)).values())
    predicted_starts = {members[0] for _, members in predicted_documents}
    actual_starts = {members[0] for _, members in actual_documents}
    by_type = []
    for kind in sorted(set(actual_types.values()) | set(predicted_types.values())):
        tp = confusion[(kind, kind)]
        predicted_count = sum(n for (_, predicted), n in confusion.items() if predicted == kind)
        actual_count = sum(n for (actual, _), n in confusion.items() if actual == kind)
        by_type.append({
            "type": kind,
            "actualPages": actual_count,
            "predictedPages": predicted_count,
            "precision": tp / predicted_count if predicted_count else None,
            "recall": tp / actual_count if actual_count else None,
        })
    return {
        "snapshotProvenance": "initial-review",
        "workflow": workflow_provenance(prediction, truth),
        "testRun": prediction.get("testRun", False),
        "pages": len(pages),
        "correctlyTypedPages": correct_pages,
        "pageTypeAccuracy": correct_pages / len(pages),
        "expectedDocuments": len(actual_documents),
        "proposedDocuments": len(predicted_documents),
        "exactlyReconstructedDocuments": reconstructed,
        "fullyCorrectDocuments": fully_correct,
        "extraDocumentStarts": len(predicted_starts - actual_starts),
        "missedDocumentStarts": len(actual_starts - predicted_starts),
        "byType": by_type,
        "confusion": [
            {"actual": a, "predicted": p, "pages": n}
            for (a, p), n in sorted(confusion.items())
        ],
        "grouping": compare_groups(prediction, truth, pages),
    }


def workflow_provenance(prediction, truth):
    """Carry configuration identity, never prompts, document text or provider secrets."""
    if type(prediction.get("testRun", False)) is not bool:
        raise ValueError("testRun must be a boolean")
    workflow = prediction.get("workflow")
    if workflow is None:
        if truth.get("workflow") is not None:
            raise ValueError("Ground truth expects workflow provenance missing from the snapshot")
        return None  # Older frozen snapshots remain evaluable without invented provenance.
    if not isinstance(workflow, dict):
        raise ValueError("Invalid workflow provenance")
    for key in ("id", "releaseId"):
        if not isinstance(workflow.get(key), str) or not workflow[key]:
            raise ValueError("Missing workflow identity")
    for key in ("version", "releaseVersion"):
        if type(workflow.get(key)) is not int or workflow[key] < 1:
            raise ValueError("Invalid workflow version")
    if not isinstance(workflow.get("hash"), str) or not re.fullmatch(r"[0-9a-fA-F]{64}", workflow["hash"]):
        raise ValueError("Invalid workflow snapshot hash")
    identity = {key: workflow[key] for key in ("id", "version", "releaseId", "releaseVersion", "hash")}
    expected = truth.get("workflow")
    if expected is not None and expected != identity:
        raise ValueError("Ground truth expects another workflow snapshot")
    return identity


def compare_groups(prediction, truth, pages):
    """Optional exact references and pairwise grouping, never business-system validation."""
    labelled = ["businessReference" in doc for doc in truth["documents"]]
    if not any(labelled):
        return None
    if not all(labelled):
        raise ValueError("Grouping truth must explicitly label every document, including unassigned ones")

    def reference(doc):
        value = doc.get("businessReference")
        if value is not None and not isinstance(value, str):
            raise ValueError("Business references must be strings or null")
        return value if value and value.strip() else None

    actual = { (p["sourceId"], p["pageNumber"]): reference(doc)
               for doc in truth["documents"] for p in doc["pages"] }
    proposed = { pages[page_id]: reference(doc)
                 for doc in prediction["documents"] for page_id in doc["pageIds"] }
    choose_two = lambda n: n * (n - 1) // 2
    expected_pairs = sum(choose_two(n) for n in Counter(r for r in actual.values() if r is not None).values())
    proposed_pairs = sum(choose_two(n) for n in Counter(r for r in proposed.values() if r is not None).values())
    joint = Counter((actual[p], proposed[p]) for p in pages.values()
                    if actual[p] is not None and proposed[p] is not None)
    correct_pairs = sum(choose_two(n) for n in joint.values())
    return {
        "referencePageAccuracy": sum(actual[p] == proposed[p] for p in pages.values()) / len(pages),
        "expectedAssignedPages": sum(r is not None for r in actual.values()),
        "proposedAssignedPages": sum(r is not None for r in proposed.values()),
        "expectedLinkedPagePairs": expected_pairs,
        "proposedLinkedPagePairs": proposed_pairs,
        "correctLinkedPagePairs": correct_pairs,
        "pairPrecision": correct_pairs / proposed_pairs if proposed_pairs else None,
        "pairRecall": correct_pairs / expected_pairs if expected_pairs else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prediction", type=Path, help="Frozen initial GET /api/packets/{id} JSON")
    parser.add_argument("truth", type=Path, help="Independent labels; see docs/evaluation.md")
    args = parser.parse_args()
    try:
        report = compare(
            json.loads(args.prediction.read_text(encoding="utf-8-sig")),
            json.loads(args.truth.read_text(encoding="utf-8-sig")),
        )
    except (ValueError, KeyError, TypeError, OSError):
        parser.exit(2, "Invalid evaluation input. Require initial review/revision 1, matching packet identity, valid source pages and complete unique membership; grouping labels must cover every document.\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
