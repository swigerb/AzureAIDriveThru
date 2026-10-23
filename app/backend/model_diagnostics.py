"""Disabled-default, bounded synthetic-session model boundary diagnostics."""
import hashlib
import json
import logging
import threading
import time
import uuid
from datetime import datetime

logger = logging.getLogger(__name__)


def digest(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest().upper()


def sanitize(text):
    # Bounded linear token scanning, no backtracking regex or third-party dependency.
    result = []
    hide_next = False
    for token in str(text)[:1200].split():
        stripped = token.strip(".,!?;:()[]{}\"'").lower()
        private = (hide_next or "@" in token or "/" in token or "=" in token or ":" in token
                   or stripped.startswith(("www.", "demo-"))
                   or len(token) >= 20 or sum(c.isdigit() for c in token) >= 4
                   or (any(c.isdigit() for c in token) and any(c.isalpha() for c in token)))
        hide_next = stripped in {"code", "token", "password", "secret", "key", "authorization", "bearer"}
        if hide_next:
            private = True
        result.append("[redacted]" if private else token)
    return " ".join(result)


class DiagnosticGate:
    def __init__(self, clock=time.time):
        self.clock = clock
        self._lock = threading.Lock()
        self._claimed = False

    def claim(self, settings, *, entra, principal, marker, persona, session_id):
        prefix = "CascadeDiagnostics__"
        if settings.get(prefix + "Enabled") in (None, "", "false"):
            return None
        try:
            configured_oid = uuid.UUID(settings.get(prefix + "OperatorObjectId", ""))
            configured_marker = settings.get(prefix + "RunMarker", "")
            run_id = uuid.UUID(hex=configured_marker)
            expiry = datetime.fromisoformat(settings.get(prefix + "ExpiresUtc", "").replace("Z", "+00:00"))
            if (settings.get(prefix + "Enabled") != "true" or not configured_oid.int or not run_id.int
                    or len(configured_marker) != 32 or expiry.tzinfo is None
                    or not 0 < expiry.timestamp() - self.clock() <= 900):
                raise ValueError("invalid configuration")
        except (ValueError, TypeError, AttributeError):
            logger.warning("Synthetic model diagnostics disabled: invalid or expired configuration.")
            return None
        try:
            actual_oid = uuid.UUID(principal.get("oid", "")) if principal else None
        except (ValueError, TypeError, AttributeError):
            actual_oid = None
        if not entra or actual_oid != configured_oid or marker != configured_marker or persona != "tacobell":
            if marker:
                logger.warning("Synthetic model diagnostic request rejected: scope mismatch.")
            return None
        with self._lock:
            if self._claimed:
                logger.warning("Synthetic model diagnostic request rejected: process claim already used.")
                return None
            self._claimed = True
        capture = DiagnosticCapture(session_id, expiry.timestamp(), self.clock)
        capture.emit("claimed")
        return capture


class DiagnosticCapture:
    def __init__(self, session_id, expiry, clock=time.time, sink=None, sanitizer=sanitize):
        self.session_id, self.expiry, self.clock = session_id, expiry, clock
        self.sink = sink or (lambda payload: logger.info("Synthetic model diagnostic %s", payload))
        self.sanitize = sanitizer
        self.response_id = None
        self.sequence = 0
        self.request_index = 0
        self.guest_index = 0
        self.stopped = False

    def emit(self, stage, **payload):
        if self.stopped:
            return
        self.sequence += 1
        if self.clock() >= self.expiry or self.sequence > 160:
            self.stopped = True
            logger.warning("Synthetic model diagnostic capture stopped: expiry or event limit reached.")
            return
        document = dict(payload, sessionId=self.session_id, responseId=self.response_id,
                        sequence=self.sequence, stage=stage, requestIndex=self.request_index, guestIndex=self.guest_index)
        encoded = json.dumps(document, separators=(",", ":"))
        if len(encoded) > 16000:
            self.stopped = True
            logger.warning("Synthetic model diagnostic capture stopped: payload limit reached.")
            return
        self.sink(encoded)

    def guest_committed(self, length, message_count):
        self.guest_index += 1
        self.response_id = None
        self.emit("guest.committed", contentLength=length, messageCount=message_count)

    def observe(self, action):
        if self.stopped:
            return
        try:
            action()
        except (TimeoutError, TypeError, ValueError, KeyError, AttributeError):
            self.stopped = True
            logger.warning("Synthetic model diagnostic capture stopped: invalid boundary or sanitization failure.")

    def calls(self, calls):
        return [{"idHash": digest(str(call.get("id", ""))),
                 "name": self.sanitize(call.get("function", {}).get("name", ""))}
                for call in (calls or [])[:16]]

    def request(self, messages, model, tools, tool_choice=None):
        def capture():
            self.request_index += 1
            snapshot = []
            for message in messages[-64:]:
                value = message.as_dict()
                content = value.get("content") or ""
                role = value.get("role")
                entry = {"role": role, "contentLength": len(content)}
                if role == "assistant":
                    entry["content"] = self.sanitize(content)
                if role == "system":
                    entry["contentHash"] = digest(content)
                if value.get("tool_call_id"):
                    entry["toolCallIdHash"] = digest(value["tool_call_id"])
                if value.get("tool_calls"):
                    entry["toolCalls"] = self.calls(value["tool_calls"])
                snapshot.append(entry)
            shapes = []
            for tool in (tools or [])[:32]:
                value = tool.as_dict()
                shapes.append({"name": self.sanitize(value.get("function", {}).get("name", "")),
                               "schemaHash": digest(json.dumps(value, sort_keys=True, separators=(",", ":")))})
            self.emit("request", model=model if model == "gpt-5-mini" else "[hashed]",
                      modelHash=digest(model), modelLength=len(model), messageCount=len(messages),
                      retainedMessageCount=len(snapshot), messages=snapshot, tools=shapes, toolsPresent=tools is not None,
                      toolChoice=self.sanitize(tool_choice or "default"))
        self.observe(capture)

    def choice(self, completion):
        def capture():
            choice = completion.choices[0]
            message = choice.message.as_dict()
            self.emit("choice", content=self.sanitize(message.get("content") or ""),
                      contentLength=len(message.get("content") or ""),
                      toolCalls=self.calls(message.get("tool_calls")),
                      finishReason=self.sanitize(choice.as_dict().get("finish_reason") or ""))
        self.observe(capture)
