#!/usr/bin/env python3
"""One-shot rename pass: unify tool prefixes to `reverse_*` (on-disk static
analysis) and `debug_*` (attached-process live debugging). Runs against:

  - dnspymcp/Tools/*.cs        tool registrations + mentions in descriptions
  - tests/*.py                 test call sites
  - tests/conftest.py          fixture wiring
  - tests/mcp_client.py        any hardcoded names

Order matters: do the longest / most specific patterns first so the shorter
ones (e.g. `decompile_type` -> `reverse_decompile_type`) don't collide with
already-rewritten names.
"""
from __future__ import annotations
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent

# (old, new) — order matters; longer/more specific first
RENAMES = [
    # static (reverse_*) — include `Name = "..."` wrappers and references
    ("asm_file_list_types",     "reverse_list_types"),
    ("asm_file_list_methods",   "reverse_list_methods"),
    ("asm_file_current",        "reverse_current"),
    ("asm_file_switch",         "reverse_switch"),
    ("asm_file_close",          "reverse_close"),
    ("asm_file_list",           "reverse_list"),   # must run AFTER the _list_types / _list_methods rewrites above
    ("asm_file_open",           "reverse_open"),
    ("il_method_by_token",      "reverse_il_method_by_token"),
    ("il_method",               "reverse_il_method"),
    ("decompile_method",        "reverse_decompile_method"),
    ("decompile_type",          "reverse_decompile_type"),
    ("find_string",             "reverse_find_string"),
    ("xref_to_method",          "reverse_xref_to_method"),
    ("file_patch_il_nop",       "reverse_patch_il_nop"),
    ("file_patch_bytes",        "reverse_patch_bytes"),
    ("file_save_assembly",      "reverse_save_assembly"),

    # debug_* — straight `live_*` -> `debug_*`
    ("live_session_list_methods",  "debug_session_list_methods"),
    ("live_session_pid_attach",    "debug_session_pid_attach"),
    ("live_session_pid_detach",    "debug_session_pid_detach"),
    ("live_session_disconnect",    "debug_session_disconnect"),
    ("live_session_connect",       "debug_session_connect"),
    ("live_session_switch",        "debug_session_switch"),
    ("live_session_info",          "debug_session_info"),
    ("live_session_list",          "debug_session_list"),
    ("live_session_pause",         "debug_session_pause"),
    ("live_session_go",            "debug_session_go"),
    ("live_bp_set_by_name",        "debug_bp_set_by_name"),
    ("live_bp_set_il",             "debug_bp_set_il"),
    ("live_bp_delete",             "debug_bp_delete"),
    ("live_bp_disable",            "debug_bp_disable"),
    ("live_bp_enable",             "debug_bp_enable"),
    ("live_bp_list",               "debug_bp_list"),
    ("live_heap_find_instances",   "debug_heap_find_instances"),
    ("live_heap_read_object",      "debug_heap_read_object"),
    ("live_heap_read_string",      "debug_heap_read_string"),
    ("live_heap_stats",            "debug_heap_stats"),
    ("live_memory_read_int",       "debug_memory_read_int"),
    ("live_memory_write",          "debug_memory_write"),
    ("live_memory_read",           "debug_memory_read"),
    ("live_list_dotnet_processes", "debug_list_dotnet_processes"),
    ("live_list_type_methods",     "debug_list_type_methods"),
    ("live_list_modules",          "debug_list_modules"),
    ("live_thread_current",        "debug_thread_current"),
    ("live_thread_list",           "debug_thread_list"),
    ("live_thread_stack",          "debug_thread_stack"),
    ("live_find_type",             "debug_find_type"),
    ("live_wait_paused",           "debug_wait_paused"),
    ("live_step_in",               "debug_step_in"),
    ("live_step_out",              "debug_step_out"),
    ("live_step_over",             "debug_step_over"),
    ("live_disasm",                "debug_disasm"),
]

TARGETS = [
    "dnspymcp/Tools/AsmFileTools.cs",
    "dnspymcp/Tools/FilePatchTools.cs",
    "dnspymcp/Tools/LiveDebugTools.cs",
    "tests/conftest.py",
    "tests/mcp_client.py",
    "tests/test_file_tools.py",
    "tests/test_live_tools.py",
]


def rewrite(path: Path) -> int:
    text = path.read_text(encoding="utf-8")
    changes = 0
    for old, new in RENAMES:
        if old in text:
            n = text.count(old)
            text = text.replace(old, new)
            changes += n
    path.write_text(text, encoding="utf-8")
    return changes


def main() -> None:
    total = 0
    for rel in TARGETS:
        p = ROOT / rel
        if not p.exists():
            print(f"  SKIP (missing): {rel}")
            continue
        n = rewrite(p)
        print(f"  {n:4d} renames in {rel}")
        total += n
    print(f"total: {total} renames")


if __name__ == "__main__":
    main()
