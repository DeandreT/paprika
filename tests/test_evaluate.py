import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("evaluate", Path(__file__).parents[1] / "scripts" / "evaluate.py")
evaluate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluate)


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        self.prediction = {
            "id": "packet-1",
            "status": "review",
            "revision": 1,
            "pages": [{"id": str(p), "sourceId": "a", "pageNumber": p} for p in range(1, 4)],
            "documents": [
                {"type": "bol", "pageIds": ["1", "2"]},
                {"type": "invoice", "pageIds": ["3"]},
            ],
        }
        self.truth = {
            "packetId": "packet-1",
            "documents": [
                {"type": "bol", "pages": [{"sourceId": "a", "pageNumber": 1}]},
                {"type": "bol", "pages": [{"sourceId": "a", "pageNumber": 2}]},
                {"type": "receipt", "pages": [{"sourceId": "a", "pageNumber": 3}]},
            ],
        }

    def test_same_type_false_merge_is_not_hidden_by_page_accuracy(self):
        report = evaluate.compare(self.prediction, self.truth)
        self.assertEqual(report["correctlyTypedPages"], 2)
        self.assertEqual(report["exactlyReconstructedDocuments"], 1)
        self.assertEqual(report["fullyCorrectDocuments"], 0)
        self.assertEqual(report["missedDocumentStarts"], 1)

    def test_missing_ground_truth_page_is_rejected(self):
        self.truth["documents"].pop()
        with self.assertRaises(ValueError):
            evaluate.compare(self.prediction, self.truth)

    def test_wrong_page_order_is_not_exact_reconstruction(self):
        self.truth["documents"] = [
            {"type": "bol", "pages": [{"sourceId": "a", "pageNumber": 2}, {"sourceId": "a", "pageNumber": 1}]},
            {"type": "invoice", "pages": [{"sourceId": "a", "pageNumber": 3}]},
        ]
        report = evaluate.compare(self.prediction, self.truth)
        self.assertEqual(report["correctlyTypedPages"], 3)
        self.assertEqual(report["exactlyReconstructedDocuments"], 1)

    def test_reviewed_or_unverifiable_snapshot_is_rejected(self):
        for status, revision in [("approved", 1), ("review", 2), ("processing", 1), ("review", True), (None, None)]:
            with self.subTest(status=status, revision=revision):
                self.prediction.update(status=status, revision=revision)
                with self.assertRaises(ValueError):
                    evaluate.compare(self.prediction, self.truth)

    def test_grouping_without_labels_is_not_claimed_as_measured(self):
        self.assertIsNone(evaluate.compare(self.prediction, self.truth)["grouping"])
        self.truth["documents"][0]["businessReference"] = "LOAD-1"
        with self.assertRaises(ValueError):
            evaluate.compare(self.prediction, self.truth)

    def test_missing_group_assignments_are_not_treated_as_one_correct_group(self):
        for doc in self.truth["documents"]:
            doc["businessReference"] = "LOAD-1"
        report = evaluate.compare(self.prediction, self.truth)["grouping"]
        self.assertEqual(report["expectedLinkedPagePairs"], 3)
        self.assertEqual(report["proposedLinkedPagePairs"], 0)
        self.assertIsNone(report["pairPrecision"])
        self.assertEqual(report["pairRecall"], 0)
        self.assertEqual(report["referencePageAccuracy"], 0)

    def test_group_structure_and_reference_identity_are_distinct(self):
        for doc in self.truth["documents"]:
            doc["businessReference"] = "LOAD-1"
        for doc in self.prediction["documents"]:
            doc["businessReference"] = "WRONG-LOAD"
        report = evaluate.compare(self.prediction, self.truth)["grouping"]
        self.assertEqual(report["pairRecall"], 1)
        self.assertEqual(report["pairPrecision"], 1)
        self.assertEqual(report["referencePageAccuracy"], 0)

    def test_wrong_cross_group_link_lowers_pair_precision(self):
        for doc, reference in zip(self.truth["documents"], ["LOAD-A", "LOAD-A", "LOAD-B"]):
            doc["businessReference"] = reference
        for doc in self.prediction["documents"]:
            doc["businessReference"] = "LOAD-A"
        report = evaluate.compare(self.prediction, self.truth)["grouping"]
        self.assertEqual(report["expectedLinkedPagePairs"], 1)
        self.assertEqual(report["proposedLinkedPagePairs"], 3)
        self.assertEqual(report["pairPrecision"], 1 / 3)
        self.assertEqual(report["referencePageAccuracy"], 2 / 3)

    def test_invalid_source_page_numbers_are_rejected(self):
        for number in [0, -1, True, "1"]:
            with self.subTest(number=number):
                self.truth["documents"][0]["pages"][0]["pageNumber"] = number
                with self.assertRaises(ValueError):
                    evaluate.compare(self.prediction, self.truth)

    def test_workflow_provenance_is_retained_without_configuration_content(self):
        identity = {"id": "synthetic", "version": 2, "releaseId": "release", "releaseVersion": 1, "hash": "a" * 64}
        self.prediction["workflow"] = {**identity, "instructions": "Private configuration", "types": []}
        self.prediction["testRun"] = True
        self.truth["workflow"] = identity
        report = evaluate.compare(self.prediction, self.truth)
        self.assertEqual(report["workflow"], identity)
        self.assertTrue(report["testRun"])
        self.assertNotIn("instructions", report["workflow"])

    def test_mismatched_or_malformed_workflow_provenance_is_rejected(self):
        identity = {"id": "synthetic", "version": 2, "releaseId": "release", "releaseVersion": 1, "hash": "a" * 64}
        self.prediction["workflow"] = identity.copy()
        self.truth["workflow"] = {**identity, "version": 1}
        with self.assertRaises(ValueError):
            evaluate.compare(self.prediction, self.truth)
        del self.truth["workflow"]
        for key, value in [("version", True), ("releaseVersion", 0), ("hash", "missing"), ("releaseId", None)]:
            with self.subTest(key=key):
                self.prediction["workflow"] = {**identity, key: value}
                with self.assertRaises(ValueError):
                    evaluate.compare(self.prediction, self.truth)

    def test_legacy_provenance_and_preview_marker_are_not_invented(self):
        report = evaluate.compare(self.prediction, self.truth)
        self.assertIsNone(report["workflow"])
        self.assertFalse(report["testRun"])
        self.prediction["testRun"] = "false"
        with self.assertRaises(ValueError):
            evaluate.compare(self.prediction, self.truth)


if __name__ == "__main__":
    unittest.main()
