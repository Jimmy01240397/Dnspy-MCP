"""Verify that shutting down the MCP server releases every resource it held:
 - opened asm files must be unlocked on disk
 - agent TCP connections must be closed

Spawns its own short-lived MCP instance on a distinct port so it doesn't
fight with the session-wide mcp_proc fixture.
"""
from __future__ import annotations

import os
import shutil
import signal
import socket
import subprocess
import time
from pathlib import Path

import pytest

from conftest import (
    AGENT_HOST,
    AGENT_PORT,
    MCP_EXE,
    REPO_ROOT,
    _require_binary,
    _wait_port,
)
from mcp_client import MCPClient


def _free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def _spawn_mcp(port: int, log_name: str) -> subprocess.Popen:
    _require_binary(MCP_EXE, "dnspymcp.exe")
    log = (REPO_ROOT / "tests" / f"{log_name}.log").open("wb")
    # CREATE_NEW_PROCESS_GROUP is required for GenerateConsoleCtrlEvent below.
    return subprocess.Popen(
        [str(MCP_EXE), "--transport", "http", "--bind-host", "127.0.0.1",
         "--bind-port", str(port)],
        cwd=str(MCP_EXE.parent),
        stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
    )


def _graceful_stop(proc: subprocess.Popen, timeout: float = 10.0) -> int:
    # On Windows SIGTERM maps to TerminateProcess (no shutdown hook fires).
    # CTRL_BREAK_EVENT triggers .NET Generic Host's normal shutdown path, so
    # IServiceProvider.Dispose runs and our singletons' Dispose fires.
    os.kill(proc.pid, signal.CTRL_BREAK_EVENT)
    try:
        return proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        proc.kill()
        return proc.wait(timeout=5)


def test_shutdown_releases_opened_asm(tmp_path_factory, testtarget_asm: Path):
    port = _free_port()
    proc = _spawn_mcp(port, f"mcp_shutdown_asm_{port}")
    try:
        _wait_port("127.0.0.1", port, timeout=20.0, label="mcp shutdown asm")
        copy_dir = tmp_path_factory.mktemp("shutdown_asm")
        copy = copy_dir / "dnspymcptest.copy.exe"
        shutil.copy2(testtarget_asm, copy)

        client = MCPClient(f"http://127.0.0.1:{port}/mcp")
        client.initialize()
        r = client.call_json("reverse_open", {"asmPath": str(copy)})
        assert r["path"].lower() == str(copy).lower()

        # do NOT call reverse_close — shutdown must clean up the lock itself
    finally:
        rc = _graceful_stop(proc)
        assert rc == 0, f"mcp exited with code {rc}"

    # short grace period for Windows to finish releasing the mapping
    time.sleep(0.5)
    os.remove(copy)  # raises PermissionError if the handle was leaked
    assert not copy.exists()


def test_shutdown_closes_agent_connections(agent_proc):
    """Open an agent slot, shut down MCP, verify the agent sees the TCP drop.
    The agent refuses a second client (single-connection server), so we can
    detect a leaked connection by trying to (re)connect after MCP dies — it
    should succeed within a short window once the agent has processed the
    server-side close."""
    port = _free_port()
    proc = _spawn_mcp(port, f"mcp_shutdown_agent_{port}")
    try:
        _wait_port("127.0.0.1", port, timeout=20.0, label="mcp shutdown agent")
        client = MCPClient(f"http://127.0.0.1:{port}/mcp")
        client.initialize()
        r = client.call_json("debug_session_connect", {"host": AGENT_HOST, "port": AGENT_PORT})
        assert r["connected"]
    finally:
        rc = _graceful_stop(proc)
        assert rc == 0, f"mcp exited with code {rc}"

    # If MCP dropped its TCP connection cleanly we can now open a new one.
    # Poll briefly because the agent needs a moment to notice the close.
    deadline = time.time() + 5.0
    last_err: Exception | None = None
    while time.time() < deadline:
        try:
            with socket.create_connection((AGENT_HOST, AGENT_PORT), timeout=1.0) as s:
                s.settimeout(2.0)
                banner = s.recv(256)
                assert banner, "agent sent no banner — connection not freed"
            return
        except OSError as e:
            last_err = e
            time.sleep(0.2)
    pytest.fail(f"agent slot never freed after MCP shutdown: {last_err}")
