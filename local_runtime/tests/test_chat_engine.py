"""Unit tests for `local_runtime/engines/chat.py`'s pure, model-free functions:
`_build_prompt` (renders the `/v1/chat` message list into a Phi-4-mini chat-template prompt,
including the `<tool_call>`/custom `<|tool|>` conventions) and `_parse_response` (parses generated
text back into a `ChatResult`). `Phi4ChatEngine.ensure_loaded`/`chat` themselves need
`onnxruntime-genai` and real model weights, so those are exercised manually (README "On-device
local mode"), not here -- this file never imports `onnxruntime_genai`.
"""

from __future__ import annotations

import unittest

from local_runtime.engines import ChatToolCall
from local_runtime.engines.chat import _build_prompt, _parse_response


class BuildPromptTests(unittest.TestCase):
    def test_system_message_is_rendered_first(self):
        prompt = _build_prompt([{"role": "system", "content": "You are a drive-thru order taker."}], [])
        self.assertTrue(prompt.startswith("<|system|>\nYou are a drive-thru order taker.\n<|end|>"))

    def test_user_and_assistant_turns_are_rendered_in_order(self):
        messages = [
            {"role": "system", "content": "sys"},
            {"role": "user", "content": "one burger"},
            {"role": "assistant", "content": "anything else?"},
            {"role": "user", "content": "no thanks"},
        ]
        prompt = _build_prompt(messages, [])
        user_idx = prompt.index("one burger")
        assistant_idx = prompt.index("anything else?")
        second_user_idx = prompt.index("no thanks")
        self.assertLess(user_idx, assistant_idx)
        self.assertLess(assistant_idx, second_user_idx)
        self.assertTrue(prompt.rstrip().endswith("<|assistant|>"))

    def test_tools_are_embedded_in_the_system_section_when_present(self):
        tools = [{"type": "function", "name": "get_order", "description": "returns the order", "parameters": {}}]
        prompt = _build_prompt([{"role": "system", "content": "sys"}], tools)
        self.assertIn("get_order", prompt)
        self.assertIn("<tool_call>", prompt)

    def test_no_tools_means_no_tool_instructions_leak_into_the_prompt(self):
        prompt = _build_prompt([{"role": "system", "content": "sys"}], [])
        self.assertNotIn("Available tools", prompt)

    def test_assistant_tool_calls_are_re_emitted_as_tool_call_tags(self):
        messages = [
            {"role": "user", "content": "one burger"},
            {
                "role": "assistant",
                "content": None,
                "tool_calls": [{"id": "call_1", "name": "get_order", "arguments": "{}"}],
            },
        ]
        prompt = _build_prompt(messages, [])
        self.assertIn('<tool_call>{"name": "get_order", "arguments": {}}</tool_call>', prompt)

    def test_tool_result_messages_use_a_dedicated_tool_tag(self):
        messages = [{"role": "tool", "content": "order: 1 burger", "tool_call_id": "call_1"}]
        prompt = _build_prompt(messages, [])
        self.assertIn("<|tool|>", prompt)
        self.assertIn("call_1", prompt)
        self.assertIn("order: 1 burger", prompt)

    def test_multiple_system_messages_are_joined(self):
        messages = [
            {"role": "system", "content": "part one"},
            {"role": "system", "content": "part two"},
            {"role": "user", "content": "hi"},
        ]
        prompt = _build_prompt(messages, [])
        self.assertIn("part one", prompt)
        self.assertIn("part two", prompt)
        # Exactly one <|system|> block, not one per system message.
        self.assertEqual(prompt.count("<|system|>"), 1)


class ParseResponseTests(unittest.TestCase):
    def test_plain_text_response_has_no_tool_calls(self):
        result = _parse_response("Sure, anything else?", max_tool_calls=8)
        self.assertEqual(result.content, "Sure, anything else?")
        self.assertEqual(result.tool_calls, [])

    def test_a_single_tool_call_is_parsed_with_a_synthetic_id(self):
        text = '<tool_call>{"name": "get_order", "arguments": {}}</tool_call>'
        result = _parse_response(text, max_tool_calls=8)
        self.assertIsNone(result.content)
        self.assertEqual(len(result.tool_calls), 1)
        call = result.tool_calls[0]
        self.assertIsInstance(call, ChatToolCall)
        self.assertEqual(call.name, "get_order")
        self.assertTrue(call.id)  # non-empty synthetic id

    def test_multiple_tool_calls_in_one_reply_are_all_parsed(self):
        text = (
            '<tool_call>{"name": "get_order", "arguments": {}}</tool_call>'
            '<tool_call>{"name": "get_total", "arguments": {}}</tool_call>'
        )
        result = _parse_response(text, max_tool_calls=8)
        self.assertEqual([c.name for c in result.tool_calls], ["get_order", "get_total"])
        self.assertNotEqual(result.tool_calls[0].id, result.tool_calls[1].id)

    def test_tool_calls_are_capped_at_max_tool_calls(self):
        text = "".join(f'<tool_call>{{"name": "tool_{i}", "arguments": {{}}}}</tool_call>' for i in range(5))
        result = _parse_response(text, max_tool_calls=2)
        self.assertEqual(len(result.tool_calls), 2)

    def test_malformed_json_tool_call_is_skipped_not_raised(self):
        text = '<tool_call>{name: "get_order"}</tool_call>Sure, one moment.'
        result = _parse_response(text, max_tool_calls=8)
        self.assertEqual(result.tool_calls, [])
        self.assertIn("Sure, one moment.", result.content)

    def test_tool_call_missing_a_name_is_skipped(self):
        text = '<tool_call>{"arguments": {}}</tool_call>'
        result = _parse_response(text, max_tool_calls=8)
        self.assertEqual(result.tool_calls, [])

    def test_stray_tool_call_tags_are_stripped_from_spoken_content(self):
        text = 'Sure! <tool_call>{"not_a_real_name_key": 1}</tool_call> One moment.'
        result = _parse_response(text, max_tool_calls=8)
        self.assertNotIn("<tool_call>", result.content)
        self.assertIn("Sure!", result.content)
        self.assertIn("One moment.", result.content)

    def test_arguments_provided_as_a_json_object_are_serialized_to_a_string(self):
        text = '<tool_call>{"name": "get_order", "arguments": {"id": 1}}</tool_call>'
        result = _parse_response(text, max_tool_calls=8)
        self.assertIsInstance(result.tool_calls[0].arguments, str)
        self.assertIn('"id"', result.tool_calls[0].arguments)


if __name__ == "__main__":
    unittest.main()
