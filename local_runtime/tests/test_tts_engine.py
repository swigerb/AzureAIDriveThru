"""Unit tests for `local_runtime/engines/tts.py`'s pure, model-free helper functions:
`_split_sentences` and `_resample_pcm16`. `PiperTtsEngine.ensure_loaded`/`synthesize` themselves
need `piper-tts` and real voice models, so those are exercised manually (README "On-device local
mode"), not here -- this file never imports `piper`.
"""

from __future__ import annotations

import struct
import unittest

from local_runtime.engines.tts import _resample_pcm16, _split_sentences


def _tone(num_samples: int, amplitude: int = 1000) -> bytes:
    return struct.pack(f"<{num_samples}h", *([amplitude] * num_samples))


class SplitSentencesTests(unittest.TestCase):
    def test_splits_on_sentence_ending_punctuation(self):
        self.assertEqual(
            _split_sentences("Your total is five dollars. Please pull forward! Thanks?"),
            ["Your total is five dollars.", "Please pull forward!", "Thanks?"],
        )

    def test_single_sentence_with_no_terminal_punctuation_is_returned_whole(self):
        self.assertEqual(_split_sentences("one moment please"), ["one moment please"])

    def test_empty_text_returns_a_single_empty_placeholder_not_an_empty_list(self):
        # PiperVoice.synthesize is called once per returned "sentence" -- an empty list would
        # silently synthesize nothing at all for an empty/whitespace-only reply.
        self.assertEqual(_split_sentences(""), [""])
        self.assertEqual(_split_sentences("   "), [""])

    def test_extra_whitespace_between_sentences_is_not_returned_as_its_own_entry(self):
        result = _split_sentences("Hi there.    How can I help?")
        self.assertEqual(len(result), 2)


class ResamplePcm16Tests(unittest.TestCase):
    def test_same_rate_returns_input_unchanged(self):
        pcm = _tone(100)
        self.assertEqual(_resample_pcm16(pcm, 22050, 22050), pcm)

    def test_empty_input_returns_empty_output(self):
        self.assertEqual(_resample_pcm16(b"", 22050, 24000), b"")

    def test_upsampling_produces_the_expected_sample_count(self):
        pcm = _tone(2205)  # 0.1s @ 22050Hz
        out = _resample_pcm16(pcm, 22050, 24000)
        expected_samples = round(2205 * 24000 / 22050)
        self.assertEqual(len(out) // 2, expected_samples)

    def test_downsampling_a_constant_tone_preserves_its_amplitude(self):
        pcm = _tone(1000, amplitude=1000)
        out = _resample_pcm16(pcm, 24000, 16000)
        samples = struct.unpack(f"<{len(out) // 2}h", out)
        self.assertTrue(all(abs(s - 1000) <= 1 for s in samples))

    def test_odd_trailing_byte_is_dropped_not_corrupted(self):
        pcm = _tone(100) + b"\x01"  # one dangling byte, not a full sample
        out = _resample_pcm16(pcm, 24000, 16000)
        expected_samples = round(100 * 16000 / 24000)
        self.assertEqual(len(out) // 2, expected_samples)


if __name__ == "__main__":
    unittest.main()
