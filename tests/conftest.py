"""Shared pytest fixtures for dnspymcp integration tests.

The test suite spawns three processes:
  1. dnspymcptest.exe     — an idle .NET target we can decompile + attach
  2. dnspymcpagent.exe    — the net48 ICorDebug backend (listens on 127.0.0.1:5555)
  3. dnspymcp.exe         — the MCP server in HTTP mode (listens on 127.0.0.1:5556)

The fixtures assume the repo has already been built via `builder.ps1`, leaving
the binaries under ./dist/ and ./dnspymcptest/bin/Debug/. On GitHub Actions the
build step runs before pytest.
"""
from __future__ import annotations

import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import pytest

from mcp_client import MCPClient

REPO_ROOT = Path(__file__).resolve().parent.parent
DIST = REPO_ROOT / "dist"
TESTTARGET_EXE = REPO_ROOT / "dnspymcptest" / "bin" / "Debug" / "dnspymcptest.exe"
AGENT_EXE = DIST / "dnspymcpagent" / "dnspymcpagent.exe"
MCP_EXE = DIST / "dnspymcp" / "dnspymcp.exe"

AGENT_HOST = "127.0.0.1"
AGENT_PORT = int(os.environ.get("DNSPYMCP_AGENT_PORT", "5555"))
MCP_HOST = "127.0.0.1"
MCP_PORT = int(os.environ.get("DNSPYMCP_SERVER_PORT", "5556"))
MCP_ENDPOINT = f"http://{MCP_HOST}:{MCP_PORT}/mcp"


def _wait_port(host: str, port: int, timeout: float, label: str) -> None:
    deadline = time.time() + timeout
    last_err: Exception | None = None
    while time.time() < deadline:
        try:
            with socket.create_connection((host, port), timeout=1.0):
                return
        except OSError as e:
            last_err = e
            time.sleep(0.2)
    raise RuntimeError(f"{label} did not open {host}:{port} within {timeout}s ({last_err})")


def _require_binary(path: Path, label: str) -> None:
    if not path.exists():
        pytest.exit(
            f"{label} missing at {path}. Run `pwsh -File builder.ps1` first.",
            returncode=2,
        )


def _spawn(cmd: list[str], cwd: Path, log_name: str) -> subprocess.Popen:
    log_path = REPO_ROOT / "tests" / f"{log_name}.log"
    log_path.parent.mkdir(exist_ok=True)
    log = log_path.open("wb")
    return subprocess.Popen(
        cmd,
        cwd=str(cwd),
        stdout=log,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
    )


@pytest.fixture(scope="session")
def testtarget_pid() -> int:
    _require_binary(TESTTARGET_EXE, "dnspymcptest.exe")
    p = _spawn([str(TESTTARGET_EXE)], cwd=TESTTARGET_EXE.parent, log_name="testtarget")
    try:
        time.sleep(1.0)
        if p.poll() is not None:
            raise RuntimeError(f"dnspymcptest exited early with code {p.returncode}")
        yield p.pid
    finally:
        try:
            p.terminate()
            p.wait(timeout=5)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass


@pytest.fixture(scope="session")
def agent_proc(testtarget_pid: int):
    _require_binary(AGENT_EXE, "dnspymcpagent.exe")
    cmd = [
        str(AGENT_EXE),
        "--host", AGENT_HOST,
        "--port", str(AGENT_PORT),
        "--attach", str(testtarget_pid),
    ]
    p = _spawn(cmd, cwd=AGENT_EXE.parent, log_name="agent")
    try:
        _wait_port(AGENT_HOST, AGENT_PORT, timeout=20.0, label="agent")
        yield p
    finally:
        try:
            p.terminate()
            p.wait(timeout=5)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass


@pytest.fixture(scope="session")
def mcp_proc(agent_proc):
    _require_binary(MCP_EXE, "dnspymcp.exe")
    env = os.environ.copy()
    env.setdefault("DNSPYMCP_AGENT_HOST", AGENT_HOST)
    env.setdefault("DNSPYMCP_AGENT_PORT", str(AGENT_PORT))
    cmd = [
        str(MCP_EXE),
        "--transport", "http",
        "--bind-host", MCP_HOST,
        "--bind-port", str(MCP_PORT),
    ]
    p = subprocess.Popen(
        cmd,
        cwd=str(MCP_EXE.parent),
        stdout=(REPO_ROOT / "tests" / "mcp.log").open("wb"),
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        env=env,
    )
    try:
        _wait_port(MCP_HOST, MCP_PORT, timeout=20.0, label="mcp server")
        yield p
    finally:
        try:
            p.terminate()
            p.wait(timeout=5)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass


@pytest.fixture(scope="session")
def mcp(mcp_proc) -> MCPClient:
    c = MCPClient(MCP_ENDPOINT)
    c.initialize()
    return c


@pytest.fixture(scope="session")
def testtarget_asm() -> Path:
    _require_binary(TESTTARGET_EXE, "dnspymcptest.exe")
    return TESTTARGET_EXE


@pytest.fixture(scope="session")
def live_agent(mcp: MCPClient):
    """Ensure the MCP server is connected to the agent. Returns the mcp client."""
    r = mcp.call("live_agent_open", {"host": AGENT_HOST, "port": AGENT_PORT})
    if not r["ok"]:
        pytest.skip(f"agent open failed: {r['text']}")
    return mcp
