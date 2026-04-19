"""Tests for [FILE] (on-disk) tools — they don't need the agent.

These run against the compiled dnspymcptest.exe assembly, which is built by
`builder.ps1` into dnspymcptest/bin/Debug/. The server exposes the tools via
HTTP; the fixture in conftest.py takes care of spawning it.
"""
from __future__ import annotations

import json
import shutil
from pathlib import Path

import pytest


@pytest.fixture(scope="module")
def asm(mcp, testtarget_asm: Path) -> str:
    """Open the test assembly in the workspace once per module."""
    path = str(testtarget_asm)
    r = mcp.call_json("asm_file_open", {"asmPath": path})
    assert r is not None
    assert r.get("path", "").lower() == path.lower()
    yield path
    mcp.call("asm_file_close", {"asmPath": path})


def test_list_tools_surface(mcp):
    tools = mcp.list_tools()
    names = {t["name"] for t in tools}
    # smoke check — make sure the catalog contains both FILE and LIVE tools.
    for must in ("asm_file_open", "decompile_type", "il_method", "find_string",
                 "xref_to_method", "file_patch_il_nop",
                 "live_agent_open", "live_list_dotnet_processes",
                 "live_heap_stats", "live_bp_set_by_name"):
        assert must in names, f"missing tool: {must}"


def test_asm_list_and_types(mcp, asm):
    r = mcp.call_json("asm_file_list")
    assert isinstance(r, list) and any(x["path"].lower() == asm.lower() for x in r)

    r = mcp.call_json("asm_file_list_types", {"asmPath": asm, "namePattern": "Widget"})
    assert set(r.keys()) >= {"total", "offset", "returned", "truncated", "items"}
    assert any(t["fullName"] == "DnSpyMcp.TestTarget.Widget" for t in r["items"])


def test_list_types_pagination(mcp, asm):
    page1 = mcp.call_json("asm_file_list_types", {"asmPath": asm, "max": 2, "offset": 0})
    assert page1["returned"] == 2
    if page1["truncated"]:
        assert page1["nextOffset"] == 2
        assert "truncated" in page1["hint"]
        page2 = mcp.call_json("asm_file_list_types", {"asmPath": asm, "max": 2, "offset": page1["nextOffset"]})
        assert page2["offset"] == 2


def test_list_methods(mcp, asm):
    r = mcp.call_json("asm_file_list_methods",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Program"})
    items = r["items"]
    by_name = {m["name"] for m in items}
    assert {"Main", "Compute", "Add", "Multiply"}.issubset(by_name)
    add = next(m for m in items if m["name"] == "Add")
    assert add["token"] > 0
    assert add["hasBody"]


def test_decompile_type(mcp, asm):
    r = mcp.call_json("decompile_type",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert set(r.keys()) >= {"totalChars", "offsetChars", "returnedChars", "truncated", "text"}
    assert "class Widget" in r["text"]
    assert "public string Name" in r["text"]


def test_decompile_type_truncation(mcp, asm):
    r = mcp.call_json("decompile_type",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget", "maxChars": 20})
    assert r["returnedChars"] == 20
    assert r["truncated"] is True
    assert r["nextOffsetChars"] == 20
    assert "truncated" in r["hint"]


def test_decompile_method(mcp, asm):
    r = mcp.call_json("decompile_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Add"})
    assert "Add" in r["text"] and "return" in r["text"]


def test_il_method(mcp, asm):
    r = mcp.call_json("il_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Add"})
    items = r["items"]
    assert isinstance(items, list) and len(items) >= 1
    assert any(i["opCode"] == "ret" for i in items)


def test_il_method_by_token(mcp, asm):
    methods = mcp.call_json("asm_file_list_methods",
                            {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Program"})["items"]
    add = next(m for m in methods if m["name"] == "Add")
    r = mcp.call_json("il_method_by_token", {"asmPath": asm, "token": add["token"]})
    assert any(i["opCode"] == "ret" for i in r["items"])


def test_find_string(mcp, asm):
    r = mcp.call_json("find_string", {"asmPath": asm, "needle": "widget-"})
    assert r["returned"] >= 1
    assert any("widget-" in (x.get("value") or "") for x in r["items"])


def test_xref_to_method_shorthand(mcp, asm):
    # The shorthand path (no ::) should resolve by type+name and match any overload.
    r = mcp.call_json("xref_to_method",
                      {"asmPath": asm,
                       "targetFullName": "DnSpyMcp.TestTarget.Program.Add"})
    assert r["returned"] >= 1
    assert any("Compute" in x["from"] for x in r["items"])


def test_xref_to_method_full_signature(mcp, asm):
    r = mcp.call_json("xref_to_method",
                      {"asmPath": asm,
                       "targetFullName": "System.Int32 DnSpyMcp.TestTarget.Program::Add(System.Int32,System.Int32)"})
    assert r["returned"] >= 1


def test_file_patch_il_nop(mcp, asm, tmp_path_factory):
    out = tmp_path_factory.mktemp("patch") / "dnspymcptest.patched.exe"
    r = mcp.call_json("file_patch_il_nop", {
        "asmPath": asm,
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
        "startOffset": 0,
        "endOffset": 2,
        "outputPath": str(out),
    })
    assert r["changedInstructions"] >= 1
    assert Path(r["written"]).exists()


def test_file_patch_bytes_roundtrip(mcp, asm, tmp_path_factory):
    # copy the asm so we don't clobber the original
    copy_dir = tmp_path_factory.mktemp("bytes")
    copy = copy_dir / "dnspymcptest.exe"
    shutil.copy2(asm, copy)
    # read first byte, overwrite with same value
    orig = copy.read_bytes()[:1].hex()
    r = mcp.call_json("file_patch_bytes", {"filePath": str(copy), "offset": 0, "hex": orig})
    assert r["written"] == 1


def test_file_save_assembly(mcp, asm, tmp_path_factory):
    out = tmp_path_factory.mktemp("save") / "dnspymcptest.saved.exe"
    r = mcp.call_json("file_save_assembly", {"asmPath": asm, "outputPath": str(out)})
    assert Path(r["written"]).exists()


def test_asm_close_reopen(mcp, testtarget_asm: Path):
    path = str(testtarget_asm)
    # make sure open+close roundtrip works without leaving state behind
    mcp.call_json("asm_file_open", {"asmPath": path})
    closed = mcp.call_json("asm_file_close", {"asmPath": path})
    assert closed.get("closed") is True
    # reopen for subsequent tests
    mcp.call_json("asm_file_open", {"asmPath": path})


def test_asm_current_and_switch(mcp, testtarget_asm: Path):
    path = str(testtarget_asm)
    mcp.call_json("asm_file_open", {"asmPath": path})
    cur = mcp.call_json("asm_file_current")
    assert cur["current"].lower() == path.lower()
    # switch is a no-op when there's only one, but should still succeed
    switched = mcp.call_json("asm_file_switch", {"asmPath": path})
    assert switched["current"].lower() == path.lower()


def test_asm_open_auto_switches_active(mcp, testtarget_asm: Path, tmp_path_factory):
    """Opening a second asm must make it the new active session (matches live_agent_open)."""
    primary = str(testtarget_asm)
    mcp.call_json("asm_file_open", {"asmPath": primary})
    mcp.call_json("asm_file_switch", {"asmPath": primary})

    # copy the asm so we have a distinct path to open
    copy_dir = tmp_path_factory.mktemp("autoswitch")
    copy = copy_dir / "dnspymcptest.copy.exe"
    shutil.copy2(primary, copy)
    mcp.call_json("asm_file_open", {"asmPath": str(copy)})

    cur = mcp.call_json("asm_file_current")
    assert cur["current"].lower() == str(copy).lower(), (
        "asm_file_open should switch the active session to the newly-opened asm")

    # cleanup: switch back and close the copy so later tests aren't affected
    mcp.call_json("asm_file_close", {"asmPath": str(copy)})
    mcp.call_json("asm_file_switch", {"asmPath": primary})


def test_asm_close_releases_file_handle(mcp, testtarget_asm: Path, tmp_path_factory):
    """asm_file_close must dispose the PEFile/ModuleDef so the file can be deleted."""
    import os
    copy_dir = tmp_path_factory.mktemp("release")
    copy = copy_dir / "dnspymcptest.release.exe"
    shutil.copy2(testtarget_asm, copy)

    mcp.call_json("asm_file_open", {"asmPath": str(copy)})
    mcp.call_json("asm_file_close", {"asmPath": str(copy)})

    # If the handle leaks the delete raises PermissionError on Windows.
    os.remove(copy)
    assert not copy.exists()


def test_asm_default_session_omit_path(mcp, testtarget_asm: Path):
    """With exactly one open asm, tools that accept asmPath=null must still work."""
    mcp.call_json("asm_file_open", {"asmPath": str(testtarget_asm)})
    mcp.call_json("asm_file_switch", {"asmPath": str(testtarget_asm)})
    # omit asmPath — should use the active session
    r = mcp.call_json("asm_file_list_types", {"namePattern": "Widget"})
    assert any(t["fullName"] == "DnSpyMcp.TestTarget.Widget" for t in r["items"])
    # decompile without asmPath too
    r = mcp.call_json("decompile_type", {"typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert "class Widget" in r["text"]
