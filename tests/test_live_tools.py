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
    r = live_agent.call_json("debug_session_info")
    assert r is not None
    assert r["current"]
    # debugState is the merged session info — either pid or dump path should be populated
    st = r.get("debugState") or {}
    assert st.get("pid") or st.get("dumpPath")


def test_agent_list_current_switch(live_agent):
    lst = live_agent.call_json("debug_session_list")
    assert isinstance(lst, list) and len(lst) >= 1
    active = [a for a in lst if a["active"]]
    assert len(active) == 1
    cur = live_agent.call_json("debug_session_info")
    assert cur["current"] == active[0]["name"]

    # switching to the same slot should succeed
    sw = live_agent.call_json("debug_session_switch", {"name": active[0]["name"]})
    assert sw["current"] == active[0]["name"]


def test_agent_connect_is_idempotent(live_agent):
    # connecting the already-active session again should succeed and stay active
    lst = live_agent.call_json("debug_session_list")
    slot = next(a for a in lst if a["active"])
    r = live_agent.call_json("debug_session_connect", {
        "host": slot["host"], "port": slot["port"], "name": slot["name"]})
    assert r["connected"] and r["active"] == slot["name"]


def _items(env):
    """Unwrap pagination envelope -> items list. Asserts envelope shape."""
    assert isinstance(env, dict), f"expected envelope, got {type(env).__name__}"
    for k in ("total", "offset", "returned", "truncated", "items"):
        assert k in env, f"missing envelope key: {k}"
    assert isinstance(env["items"], list)
    return env["items"]


def test_agent_list_methods(live_agent):
    env = live_agent.call_json("debug_list_methods")
    items = _items(env)
    assert len(items) > 5
    names = {m.get("method") or m.get("name") for m in items}
    assert any("session." in (n or "") for n in names)


def test_agent_list_methods_paged(live_agent):
    """offset/max should slice + set truncated/nextOffset correctly."""
    full = _items(live_agent.call_json("debug_list_methods", {"max": 500}))
    assert len(full) >= 3, "need at least 3 methods to test paging"
    page1 = live_agent.call_json("debug_list_methods", {"offset": 0, "max": 2})
    assert page1["total"] == len(full)
    assert page1["returned"] == 2
    assert page1["truncated"] is True
    assert page1["nextOffset"] == 2
    page2 = live_agent.call_json("debug_list_methods", {"offset": 2, "max": 2})
    assert page2["offset"] == 2
    # both pages disjoint
    page1_names = {m.get("method") or m.get("name") for m in page1["items"]}
    page2_names = {m.get("method") or m.get("name") for m in page2["items"]}
    assert page1_names.isdisjoint(page2_names)


def test_list_dotnet_processes(live_agent):
    env = live_agent.call_json("debug_list_dotnet_processes")
    items = _items(env)
    assert isinstance(items, list)


def test_agent_current_contains_debug_state(live_agent, testtarget_pid):
    """agent_current must surface the debug state (pid etc.) of the active
    agent's target — no separate session_info tool needed."""
    r = live_agent.call_json("debug_session_info")
    assert r["connected"] is True
    st = r["debugState"]
    assert st is not None
    assert st.get("pid") == testtarget_pid


def test_heap_stats(live_agent):
    stats = _items(live_agent.call_json("debug_heap_stats", {"top": 10}))
    assert len(stats) >= 1
    # field name from agent is `type`; just assert rows are well-formed
    for row in stats:
        assert "type" in row and "count" in row and "totalSize" in row


def test_heap_find_widget(live_agent):
    rows = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "Widget", "max": 8}))
    assert len(rows) >= 1


def test_heap_read_widget_object(live_agent):
    rows = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "Widget", "max": 1}))
    assert rows, "no Widget instance on heap"
    addr = rows[0]["address"] if isinstance(rows[0], dict) else rows[0]
    obj = live_agent.call_json("debug_heap_read_object", {"address": int(addr), "maxFields": 16})
    assert obj is not None


def test_heap_read_string(live_agent):
    strs = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "System.String", "max": 1}))
    if not strs:
        pytest.skip("no strings on heap yet")
    addr = strs[0]["address"] if isinstance(strs[0], dict) else strs[0]
    s = live_agent.call_json("debug_heap_read_string", {"address": int(addr)})
    assert s is not None


def test_pause_and_list_threads(live_agent):
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    threads = _items(live_agent.call_json("debug_thread_list"))
    assert len(threads) >= 1
    live_agent.call_json("debug_go")


def test_list_modules(live_agent):
    mods = _items(live_agent.call_json("debug_list_modules"))
    assert len(mods) >= 1
    assert any("dnspymcptest" in (m.get("path") or m.get("name") or "").lower() for m in mods)
    # Default schema is the slim view: shortName + name + address only.
    sample = mods[0]
    assert set(sample.keys()) == {"shortName", "name", "address"}, f"unexpected slim schema: {sample}"


def test_list_modules_verbose(live_agent):
    mods = _items(live_agent.call_json("debug_list_modules", {"verbose": True}))
    assert len(mods) >= 1
    sample = mods[0]
    # Verbose schema: slim fields plus 5 extras (appDomain/assembly/size/isDynamic/isInMemory).
    expected = {"shortName", "name", "address", "appDomain", "assembly", "size", "isDynamic", "isInMemory"}
    assert set(sample.keys()) == expected, f"unexpected verbose schema: {sample}"


def test_bp_set_by_name_and_list(live_agent):
    r = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
    })
    assert r is not None
    bps = _items(live_agent.call_json("debug_bp_list"))
    assert len(bps) >= 1


def test_step_in_out(live_agent):
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    live_agent.call_json("debug_step_in", {"timeoutMs": 2000})
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    live_agent.call_json("debug_step_out", {"timeoutMs": 2000})
    live_agent.call_json("debug_go")


def test_memory_roundtrip(live_agent):
    # read-then-write-same-byte on the top-of-stack address for thread 0 — this
    # is always mapped and writable, so the roundtrip is a reliable smoke test.
    # ICorDebug requires a paused state for stack walking.
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    try:
        threads = _items(live_agent.call_json("debug_thread_list"))
        assert threads, "no threads while paused"
        tid = threads[0]["uniqueId"]
        stk = _items(live_agent.call_json("debug_thread_stack", {"threadId": tid, "max": 1}))
        assert stk, "empty stack while paused"
        addr = stk[0].get("stackStart")
        assert addr, "no stackStart in frame"
        data = live_agent.call_json("debug_memory_read", {"address": int(addr), "size": 1})
        live_agent.call_json("debug_memory_write", {"address": int(addr), "hex": data["hex"]})
    finally:
        live_agent.call_json("debug_go")


def test_thread_stack_id_aliases(live_agent):
    # debug_thread_stack must accept uniqueId, osThreadId, and the legacy
    # threadId alias and produce the same stack for the same thread.
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    try:
        threads = _items(live_agent.call_json("debug_thread_list"))
        assert threads, "no threads while paused"
        # pick the first thread that has a non-zero osThreadId so we can
        # round-trip both fields.
        sel = next((t for t in threads if t.get("osThreadId")), threads[0])
        u, o = sel["uniqueId"], sel["osThreadId"]

        s_unique = _items(live_agent.call_json("debug_thread_stack", {"uniqueId": u, "max": 1}))
        s_legacy = _items(live_agent.call_json("debug_thread_stack", {"threadId": u, "max": 1}))
        s_os = _items(live_agent.call_json("debug_thread_stack", {"osThreadId": o, "max": 1}))
        assert s_unique == s_legacy, "uniqueId vs legacy threadId disagree"
        assert s_unique == s_os, "uniqueId vs osThreadId disagree"
    finally:
        live_agent.call_json("debug_go")


def test_thread_stack_id_validation(live_agent):
    # Missing both ids must error; passing both must error.
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    try:
        r = live_agent.call("debug_thread_stack", {"max": 1})
        assert not r["ok"], f"expected error when no id supplied, got {r}"
        r = live_agent.call("debug_thread_stack", {"uniqueId": 1, "osThreadId": 2, "max": 1})
        assert not r["ok"], f"expected error when both ids supplied, got {r}"
    finally:
        live_agent.call_json("debug_go")


# ---- attach / detach lifecycle (Phase 2 API) -----------------------------
# Run these LAST in the file — they cycle the debugger attachment and we want
# the earlier tests to use the stable fixture-managed attach.


def _debug_state(live_agent):
    """Helper: fetch the merged debug state from agent_current."""
    return live_agent.call_json("debug_session_info").get("debugState") or {}


def test_detach_attach_cycle(live_agent, testtarget_pid):
    """Detach then re-attach to the same PID. Post-cycle session must report
    attached with matching pid; lastExit info should show the detach."""
    # Detach
    r = live_agent.call_json("debug_pid_detach")
    assert r["detached"] is True
    assert r.get("lastExitedPid") == testtarget_pid
    assert "detach" in (r.get("lastExitReason") or "").lower()

    # agent_current's debugState mirrors the same state
    st = _debug_state(live_agent)
    assert st.get("isAttached") is False
    assert st.get("pid") in (None, 0)   # serializer may omit or send 0
    assert st.get("lastExitedPid") == testtarget_pid

    # Re-attach
    r2 = live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})
    assert r2["attached"] is True
    assert r2["pid"] == testtarget_pid

    # Fresh attach clears lastExit history
    st2 = _debug_state(live_agent)
    assert st2.get("isAttached") is True
    assert st2.get("pid") == testtarget_pid
    assert st2.get("lastExitedPid") in (None, 0)
    assert not st2.get("lastExitReason")


def test_detach_is_idempotent(live_agent, testtarget_pid):
    """Calling detach twice in a row must not error. Second call reports
    detached=False (nothing to detach)."""
    live_agent.call_json("debug_pid_detach")
    r = live_agent.call_json("debug_pid_detach")
    assert r["detached"] is False  # nothing to detach the second time

    # Restore the attachment for any subsequent test
    live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})


def test_attach_to_nonexistent_pid_errors(live_agent, testtarget_pid):
    """Attach to a PID that doesn't exist must surface a clear error, not
    silently half-attach. The agent must NOT die (STA exception kills process
    is precisely what we guarded against in DebuggerSession.Attach)."""
    # Detach first so we're in a clean state to probe the error path
    live_agent.call_json("debug_pid_detach")

    bad_pid = 1234567  # virtually guaranteed not to exist
    r = live_agent.call("debug_pid_attach", {"pid": bad_pid})
    assert not r["ok"], f"expected error, got ok response: {r}"
    msg = (r.get("text") or r.get("error") or "").lower()
    # Accept any of the common flavors — ArgumentException / "not running" /
    # "not found" / HRESULT — what matters is there IS a message.
    assert msg, "error must carry a descriptive message"

    # Agent must still be alive and able to re-attach. If the STA thread died
    # this would throw ConnectionRefused.
    r2 = live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})
    assert r2["attached"] is True
    assert r2["pid"] == testtarget_pid


def test_attach_same_pid_is_idempotent(live_agent, testtarget_pid):
    """Calling attach with the already-attached pid should succeed (internally
    detaches + re-attaches, but from caller's view it just works)."""
    r = live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})
    assert r["attached"] is True
    assert r["pid"] == testtarget_pid
    # agent_current confirms
    st = _debug_state(live_agent)
    assert st.get("isAttached") and st.get("pid") == testtarget_pid
