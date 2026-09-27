"""The single source of truth for "no persona specified" (#74, Rick's PR #102 review item 2).

Every module that used to keep its own module-level brand-specific globals as a "no persona
catalog configured" fallback (``order_state.py``, ``tools.py``, ``rtmt.py``,
``session_manager.py``) now imports THIS module instead. There is no dual path any
more: a session created with no persona argument, a WebSocket connected with no
``?persona=`` query param, and a resume request with no persona specified ALL bind
to the exact same DEFAULT persona -- resolved here, once -- through the exact same
code every explicitly-bound persona goes through (``menu_utils.get_catalog_for_persona``,
``order_state.OrderState.create_session``, ...). "Omitted" is a value (the default
persona id), never a distinct code path.

Self-initializes at plain ``import`` time from :meth:`PersonaCatalog.load`'s own
env-driven defaults (``PERSONAS_DIR``/``PERSONAS``/``DEFAULT_PERSONA``) -- the exact
same defaults that reproduce today's single-default-persona-pack behavior byte for byte. This
matters because at least one test (``test_conformance_hooks.py``'s
``_isolated_order_state()``) execs a completely independent copy of ``order_state.py``
via ``importlib.util.spec_from_file_location`` that is never registered in
``sys.modules`` and never has any external setter called on it -- a plain
``import default_persona`` inside that fresh module must still resolve the correct
default persona on its own, not depend on ``app.py``'s ``create_app()`` having run.

``configure_default_catalog`` lets ``app.py``'s ``create_app()`` install its OWN
already-loaded ``PersonaCatalog`` instead of causing a second, redundant disk load in
production -- purely an optimization (and a guarantee the two can never drift); the
self-initialized value above is already byte-for-byte correct without it.
"""

from __future__ import annotations

import menu_utils
from persona_loader import Persona, PersonaCatalog
from prompt_loader import PromptLoader

_catalog: PersonaCatalog = PersonaCatalog.load()

# #74 (Rick's PR #102 review, round 3, required item 1): memoized per persona id, the same
# pattern as ``menu_utils._catalog_cache`` -- built once for the default persona, then reused
# rather than re-parsing the pack's YAML prompts on every call.
_prompt_loader_cache: dict[str, PromptLoader] = {}


def get_default_catalog() -> PersonaCatalog:
    """The default-persona-resolution catalog -- production installs its own real
    catalog via :func:`configure_default_catalog`; every other caller (including a
    fresh, standalone module exec) gets this self-initialized, env-driven one."""
    return _catalog


def get_default_persona() -> Persona:
    """The persona a session/request binds to when none is specified at all."""
    return _catalog.get(_catalog.default_persona_id)


def get_default_menu_catalog() -> menu_utils.MenuCatalog:
    """The default persona's own :class:`~menu_utils.MenuCatalog` -- what a caller with no
    session/persona in hand (a direct test call, or an unknown/expired session id) resolves its
    menu through (#74; ``menu_utils.get_catalog_for_persona`` caches one instance per persona id,
    so this is the SAME instance every explicitly-bound default-persona session already uses,
    never a second, independently-loaded copy)."""
    return menu_utils.get_catalog_for_persona(get_default_persona())


def get_default_prompt_loader() -> PromptLoader:
    """The default persona's own :class:`~prompt_loader.PromptLoader` -- the deployment-wide
    fallback ``session_manager.py``/``tools.py`` read greeting/error-message text through when a
    caller has no explicitly-bound persona's own ``PromptLoader`` in hand (#74, Rick's PR #102
    review, round 3, required item 1: replaces ``session_manager.py``'s hardcoded
    ``_DEFAULT_GREETING_MSG`` hardcoded string -- there is no more brand-specific text living
    outside a persona pack). Memoized per persona id (same pattern as ``get_default_menu_catalog``), so
    this is the SAME instance every explicitly-bound default-persona session already uses."""
    persona = get_default_persona()
    loader = _prompt_loader_cache.get(persona.id)
    if loader is None:
        loader = PromptLoader(brand=persona.id, prompts_dir=persona.prompts_dir)
        _prompt_loader_cache[persona.id] = loader
    return loader


def configure_default_catalog(catalog: PersonaCatalog) -> None:
    """Install *catalog* (app.py's own already-loaded ``PersonaCatalog``) as the
    source of the default persona, so production never loads the persona packs from
    disk twice and the two can never drift apart."""
    global _catalog
    _catalog = catalog
