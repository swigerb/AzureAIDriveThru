"""Prompt loader for Sonic AI Drive-Thru.

Loads YAML prompt files from personas/{brand}/prompts/ at startup (issue #70 -- persona pack
skeleton and loader). Validates required sections, caches in memory, and provides a clean API.

Usage:
    from prompt_loader import PromptLoader
    loader = PromptLoader(brand="sonic")
    system_prompt = loader.get_system_prompt()
    greeting = loader.get_greeting()
    tool_schemas = loader.get_tool_schemas()
    error_messages = loader.get_error_messages()
    hints = loader.get_hints()
"""

import json
import logging
import os
import threading
import time
from pathlib import Path
from typing import Any

import yaml
from jinja2 import BaseLoader, Environment

__all__ = ["PromptLoader", "REQUIRED_ERROR_MESSAGE_KEYS"]

logger = logging.getLogger("prompt-loader")

# #125 (fail-fast follow-up to #116): every structured rejection tools.py's update_order/modify
# path can return renders one of these keys via PromptLoader.render_error, plus generic_error as
# the shared fallback. There is no per-key default -- error_messages.yaml is per-pack only (design
# doc section 6) -- so a pack missing any of these used to silently fall back to
# render_error's "An error occurred (<key>)." placeholder at runtime instead of failing startup
# like a missing greeting/tool schema does. Mirrored byte-for-byte (same values, same order) in
# app/backend-dotnet/src/Backend/Prompts/PromptLoader.cs's RequiredErrorMessageKeys;
# tests/test_prompt_loading.py::test_required_error_message_keys_match_dotnet parses that C# file
# and asserts the two lists are equal, so the two can never silently drift apart.
REQUIRED_ERROR_MESSAGE_KEYS: tuple[str, ...] = (
    "generic_error",
    "item_not_on_menu",  # #73: update_order add -- item_name doesn't resolve on the menu at all
    "size_not_available",  # #73: update_order add -- item resolves, requested size doesn't
    "item_not_in_order",  # #116: update_order modify -- item resolves, isn't in the order yet
    "machine_unavailable",  # #116: update_order add -- item's requiresMachine is reported "down"
    "extras_blocked_category",  # #116: update_order add -- an extra with a blocked base category
    "extras_no_base_item",  # #116: update_order add -- an extra with no allowed base item yet
)

# personas/ sits at the repo root (design doc section 4.1); this module lives at
# app/backend/prompt_loader.py, two levels below it. Overridable via the PERSONAS_DIR env var
# (same variable persona_loader.py reads), so both modules agree on where packs live.
_REPO_ROOT = Path(__file__).resolve().parents[2]
_PERSONAS_DIR = Path(os.environ.get("PERSONAS_DIR") or (_REPO_ROOT / "personas"))

# Jinja2 environment for rendering error message templates
_jinja_env = Environment(loader=BaseLoader(), undefined=__import__("jinja2").StrictUndefined)


class PromptLoader:
    """Loads and caches prompt YAML files for a given brand's persona pack."""

    def __init__(self, brand: str = "sonic", prompts_dir: Path | str | None = None):
        """*prompts_dir* (#74, optional): load prompts from this exact directory instead of
        deriving it from *brand* + ``PERSONAS_DIR`` -- lets a multi-persona deployment build one
        ``PromptLoader`` per bound persona from its own catalog-resolved
        :attr:`persona_loader.Persona.prompts_dir`, without requiring that path to match the
        ``PERSONAS_DIR / brand / "prompts"`` convention. Omitted (the default): unchanged,
        today's single-persona/env-driven behavior."""
        self._brand = brand
        self._brand_dir = Path(prompts_dir) if prompts_dir is not None else _PERSONAS_DIR / brand / "prompts"
        self._cache: dict[str, Any] = {}
        self._last_load_time: float = 0.0
        self._dev_mode = os.environ.get("DEV_MODE", "").lower() in ("true", "1", "yes")

        self._load_all()

        if self._dev_mode:
            logger.info("DEV_MODE enabled — prompt hot-reload active for brand '%s'", brand)
            self._watcher_thread = threading.Thread(target=self._watch_loop, daemon=True)
            self._watcher_thread.start()

    # ── Public API ──────────────────────────────────────────────────────────

    def get_system_prompt(self) -> str:
        """Return the assembled system prompt string from all sections."""
        self._maybe_reload()
        return self._cache["system_prompt"]

    def get_greeting(self) -> dict:
        """Return the greeting message dict (conversation.item.create payload)."""
        self._maybe_reload()
        return self._cache["greeting"]

    def get_greeting_json_str(self) -> str:
        """Return the greeting as a pre-serialized JSON string for WebSocket send."""
        self._maybe_reload()
        return self._cache["greeting_json"]

    def get_tool_schemas(self) -> list[dict]:
        """Return the list of tool schema dicts for OpenAI session.update."""
        self._maybe_reload()
        return self._cache["tool_schemas"]

    def get_error_messages(self) -> dict[str, str]:
        """Return raw error message templates (Jinja2 strings)."""
        self._maybe_reload()
        return self._cache["error_messages"]

    def render_error(self, key: str, **kwargs: Any) -> str:
        """Render an error message template with Jinja2 variables."""
        self._maybe_reload()
        template_str = self._cache["error_messages"].get(key)
        if template_str is None:
            logger.warning("Unknown error message key: %s", key)
            return f"An error occurred ({key})."
        try:
            return _jinja_env.from_string(template_str).render(**kwargs)
        except Exception as exc:
            logger.error("Failed to render error template '%s': %s", key, exc)
            return template_str

    def get_hints(self) -> dict:
        """Return the full hints data (upsell_hints, system_hints, delta_templates)."""
        self._maybe_reload()
        return self._cache["hints"]

    def get_upsell_hint(self, category: str) -> str:
        """Return the upsell hint string for a given item category."""
        self._maybe_reload()
        hints_data = self._cache["hints"]
        upsell_hints = hints_data.get("upsell_hints", {})

        # Search through hints for matching category
        for _hint_key, hint_info in upsell_hints.items():
            if category in (hint_info.get("trigger_categories") or []):
                return f" ({hint_info['hint']})"

        # Fall back to generic hint
        generic = upsell_hints.get("generic", {})
        return f" ({generic.get('hint', '')})" if generic.get("hint") else ""

    def get_delta_template(self, action: str) -> str:
        """Return the delta text template for 'add', 'remove', or 'modify' actions."""
        self._maybe_reload()
        templates = self._cache["hints"].get("delta_templates", {})
        if action == "add":
            return templates.get("item_added", "Added {{quantity}} {{display_name}} — your total is now {{total}}")
        if action == "modify":
            # #77: `modify` resizes an existing order line in place (design doc section 3.3 row
            # 21) -- distinct wording from "remove" so the guest doesn't hear their item was taken
            # off the order when it was actually just resized.
            return templates.get("item_modified", "Changed {{display_name}} — your total is now {{total}}")
        return templates.get("item_removed", "Removed {{quantity}} {{display_name}} — your total is now {{total}}")

    def render_template(self, template_str: str, **kwargs: Any) -> str:
        """Render any Jinja2 template string with the given variables."""
        try:
            return _jinja_env.from_string(template_str).render(**kwargs)
        except Exception as exc:
            logger.error("Failed to render template: %s", exc)
            return template_str

    # ── Loading & Validation ────────────────────────────────────────────────

    def _load_all(self) -> None:
        """Load all YAML files for the brand's persona pack. Fail-fast on errors."""
        if not self._brand_dir.is_dir():
            raise FileNotFoundError(
                f"Prompt directory not found: {self._brand_dir}. "
                f"Expected prompts at personas/{self._brand}/prompts/ "
                f"(PERSONAS_DIR={_PERSONAS_DIR})"
            )

        # Load system prompt
        sp_data = self._load_yaml("system_prompt.yaml")
        if sp_data is None:
            raise FileNotFoundError("System prompt file not found: system_prompt.yaml")
        self._cache["system_prompt"] = self._assemble_system_prompt(sp_data)

        # Load greeting
        gr_data = self._load_yaml("greeting.yaml")
        if gr_data is None:
            raise FileNotFoundError("Greeting file not found: greeting.yaml")
        self._validate_greeting(gr_data)
        greeting_msg = gr_data["greeting"]
        self._cache["greeting"] = greeting_msg
        # Pre-serialize for WebSocket
        self._cache["greeting_json"] = json.dumps(greeting_msg)

        # Load tool schemas
        ts_data = self._load_yaml("tool_schemas.yaml")
        if ts_data is None:
            raise FileNotFoundError("Tool schemas file not found: tool_schemas.yaml")
        self._validate_tool_schemas(ts_data)
        self._cache["tool_schemas"] = ts_data["tools"]

        # Load error messages
        em_data = self._load_yaml("error_messages.yaml")
        if em_data is None:
            raise FileNotFoundError("Error messages file not found: error_messages.yaml")
        error_messages = em_data.get("messages", {})
        self._validate_error_messages(error_messages)
        self._cache["error_messages"] = error_messages

        # Load hints
        hints_data = self._load_yaml("hints.yaml")
        if hints_data is None:
            raise FileNotFoundError("Hints file not found: hints.yaml")
        self._cache["hints"] = hints_data

        self._last_load_time = time.time()
        logger.info(
            "Loaded prompts for brand '%s': system_prompt=%d chars, tools=%d, errors=%d, hints=%d",
            self._brand,
            len(self._cache["system_prompt"]),
            len(self._cache["tool_schemas"]),
            len(self._cache["error_messages"]),
            len(self._cache["hints"].get("upsell_hints", {})),
        )

    def _load_yaml(self, filename: str) -> dict | None:
        """Load a single YAML file from the brand directory."""
        path = self._brand_dir / filename
        if not path.exists():
            return None
        try:
            with path.open("r", encoding="utf-8") as f:
                data = yaml.safe_load(f)
            if not isinstance(data, dict):
                raise ValueError(f"{filename} must be a YAML mapping, got {type(data).__name__}")
            return data
        except yaml.YAMLError as exc:
            raise ValueError(f"Malformed YAML in {filename}: {exc}") from exc

    def _assemble_system_prompt(self, data: dict) -> str:
        """Assemble the system prompt string from ordered sections."""
        sections = data.get("sections")
        if not sections:
            raise ValueError("system_prompt.yaml must have a 'sections' list")

        # Sort by priority if available
        sorted_sections = sorted(sections, key=lambda s: s.get("priority", 999))

        parts = []
        for section in sorted_sections:
            content = section.get("content", "").strip()
            if content:
                parts.append(content)

        if not parts:
            raise ValueError("system_prompt.yaml sections produced empty prompt")

        return "\n\n".join(parts)

    def _validate_greeting(self, data: dict) -> None:
        """Validate greeting structure."""
        greeting = data.get("greeting")
        if not greeting:
            raise ValueError("greeting.yaml must have a 'greeting' key")
        if "type" not in greeting:
            raise ValueError("greeting must have a 'type' field")

    def _validate_error_messages(self, messages: dict) -> None:
        """#125 (fail-fast follow-up to #116): every key in REQUIRED_ERROR_MESSAGE_KEYS must be
        present so the model never gets render_error's "An error occurred (<key>)." placeholder
        text in place of the pack's own guidance for a real, reachable rejection path."""
        missing = [key for key in REQUIRED_ERROR_MESSAGE_KEYS if key not in messages]
        if missing:
            raise ValueError(
                f"error_messages.yaml for persona pack '{self._brand}' is missing required "
                f"rejection-message key(s): {', '.join(missing)}"
            )

    def _validate_tool_schemas(self, data: dict) -> None:
        """Validate tool schemas structure."""
        tools = data.get("tools")
        if not isinstance(tools, list) or len(tools) == 0:
            raise ValueError("tool_schemas.yaml must have a non-empty 'tools' list")
        for i, tool in enumerate(tools):
            if "name" not in tool:
                raise ValueError(f"Tool at index {i} missing 'name'")
            if "type" not in tool:
                raise ValueError(f"Tool '{tool.get('name', i)}' missing 'type'")

    # ── DEV_MODE Hot-Reload ─────────────────────────────────────────────────

    def _maybe_reload(self) -> None:
        """In DEV_MODE, check if files have been modified and reload."""
        if not self._dev_mode:
            return
        # Check every 2 seconds at most
        if time.time() - self._last_load_time < 2.0:
            return
        try:
            latest_mtime = max(
                f.stat().st_mtime for f in self._brand_dir.glob("*.yaml")
            )
            if latest_mtime > self._last_load_time:
                logger.info("DEV_MODE: Detected prompt file changes — reloading")
                self._load_all()
        except Exception as exc:
            logger.warning("DEV_MODE: Failed to check for file changes: %s", exc)

    def _watch_loop(self) -> None:
        """Background thread for DEV_MODE file watching."""
        while True:
            time.sleep(2)
            try:
                self._maybe_reload()
            except Exception as exc:
                logger.error("DEV_MODE watcher error: %s", exc)
