"""Tests for [LIVE] tools — agent + attached target required.

The `live_agent` fixture spawns dnspymcpagent.exe already attached to the test
target via `--attach <pid>`. These tests talk to the agent through the MCP
server's live_* proxies.

Coverage: session plumbing, multi-agent registry, heap walker (ClrMD passive
path). ICorDebug-driven thread/module/bp/step tools are validated manually
against real targets; they rely on the dnSpy DnDebugger attach-time callback
burst which is still flaky in this harness and will be covered once the
bootstrap issue is fixed.
"""
from __future__ import annotations

import json

import pytest


def test_agent_connect(live_agent):
    r = live_agent.call_json("live_session_info")
    assert r is not None
    # either pid or dump path should be populated
    assert r.get("pid") or r.get("dumpPath")


def test_agent_list_current_switch(live_agent):
    lst = live_agent.call_json("live_agent_list")
    assert isinstance(lst, list) and len(lst) >= 1
    active = [a for a in lst if a["active"]]
    assert len(active) == 1
    cur = live_agent.call_json("live_agent_current")
    assert cur["current"] == active[0]["name"]

    # switching to the same slot should succeed
    sw = live_agent.call_json("live_agent_switch", {"name": active[0]["name"]})
    assert sw["current"] == active[0]["name"]


def test_agent_open_is_idempotent(live_agent):
    # opening the already-active session again should succeed and stay active
    lst = live_agent.call_json("live_agent_list")
    slot = next(a for a in lst if a["active"])
    r = live_agent.call_json("live_agent_open", {
        "host": slot["host"], "port": slot["port"], "name": slot["name"]})
    assert r["opened"] and r["active"] == slot["name"]


def test_agent_list_methods(live_agent):
    r = live_agent.call_json("live_agent_list_methods")
    assert isinstance(r, list) and len(r) > 5
    names = {m.get("method") or m.get("name") for m in r}
    assert any("session." in (n or "") for n in names)


def test_list_dotnet_processes(live_agent):
    r = live_agent.call_json("live_list_dotnet_processes")
    assert isinstance(r, list)


def test_session_info_contains_pid(live_agent, testtarget_pid):
    info = live_agent.call_json("live_session_info")
    assert info.get("pid") == testtarget_pid


def test_heap_stats(live_agent):
    stats = live_agent.call_json("live_heap_stats", {"top": 10})
    assert isinstance(stats, list) and len(stats) >= 1
    # field name from agent is `type`; just assert rows are well-formed
    for row in stats:
        assert "type" in row and "count" in row and "totalSize" in row


def test_heap_find_widget(live_agent):
    rows = live_agent.call_json("live_heap_find_instances", {"typeName": "Widget", "max": 8})
    assert isinstance(rows, list) and len(rows) >= 1


def test_heap_read_widget_object(live_agent):
    rows = live_agent.call_json("live_heap_find_instances", {"typeName": "Widget", "max": 1})
    assert rows, "no Widget instance on heap"
    addr = rows[0]["address"] if isinstance(rows[0], dict) else rows[0]
    obj = live_agent.call_json("live_heap_read_object", {"address": int(addr), "maxFields": 16})
    assert obj is not None


def test_heap_read_string(live_agent):
    strs = live_agent.call_json("live_heap_find_instances", {"typeName": "System.String", "max": 1})
    if not strs:
        pytest.skip("no strings on heap yet")
    addr = strs[0]["address"] if isinstance(strs[0], dict) else strs[0]
    s = live_agent.call_json("live_heap_read_string", {"address": int(addr)})
    assert s is not None


def test_pause_and_list_threads(live_agent):
    live_agent.call_json("live_session_pause")
    live_agent.call_json("live_wait_paused", {"timeoutMs": 3000})
    threads = live_agent.call_json("live_thread_list")
    assert isinstance(threads, list) and len(threads) >= 1
    live_agent.call_json("live_session_go")


def test_list_modules(live_agent):
    mods = live_agent.call_json("live_list_modules")
    assert isinstance(mods, list) and len(mods) >= 1
    assert any("dnspymcptest" in (m.get("path") or m.get("name") or "").lower() for m in mods)


def test_bp_set_by_name_and_list(live_agent):
    r = live_agent.call_json("live_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
    })
    assert r is not None
    bps = live_agent.call_json("live_bp_list")
    assert isinstance(bps, list) and len(bps) >= 1


def test_step_in_out(live_agent):
    live_agent.call_json("live_session_pause")
    live_agent.call_json("live_wait_paused", {"timeoutMs": 3000})
    live_agent.call_json("live_step_in", {"timeoutMs": 2000})
    live_agent.call_json("live_step_out", {"timeoutMs": 2000})
    live_agent.call_json("live_session_go")


def test_memory_roundtrip(live_agent):
    # read-then-write-same-byte on the top-of-stack address for thread 0 — this
    # is always mapped and writable, so the roundtrip is a reliable smoke test.
    # ICorDebug requires a paused state for stack walking.
    live_agent.call_json("live_session_pause")
    live_agent.call_json("live_wait_paused", {"timeoutMs": 3000})
    try:
        threads = live_agent.call_json("live_thread_list")
        assert threads, "no threads while paused"
        tid = threads[0]["uniqueId"]
        stk = live_agent.call_json("live_thread_stack", {"threadId": tid, "max": 1})
        assert stk, "empty stack while paused"
        addr = stk[0].get("stackStart")
        assert addr, "no stackStart in frame"
        data = live_agent.call_json("live_memory_read", {"address": int(addr), "size": 1})
        live_agent.call_json("live_memory_write", {"address": int(addr), "hex": data["hex"]})
    finally:
        live_agent.call_json("live_session_go")
