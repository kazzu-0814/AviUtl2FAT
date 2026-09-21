import unittest

from att_engine.diarization import SpeakerTurn, align_segments, normalize_speaker


class DiarizationTests(unittest.TestCase):
    def test_alignment_uses_maximum_overlap(self):
        values = [{"start": 1.5, "end": 4.0, "text": "x"}, {"start": 8.0, "end": 9.0, "text": "y"}]
        align_segments(values, [SpeakerTurn(0, 2, "A"), SpeakerTurn(2, 5, "b")])
        self.assertEqual("B", values[0]["speaker_id"])
        self.assertEqual("A", values[1]["speaker_id"])

    def test_speaker_values_are_safe(self):
        self.assertEqual("A", normalize_speaker("not-a-speaker"))
        self.assertEqual("C", normalize_speaker(" c "))


if __name__ == "__main__":
    unittest.main()
