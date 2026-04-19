# dnspymcp

A Model-Context-Protocol server that exposes **dnSpy**'s static analysis and
**ICorDebug** live-debug capabilities to LLM clients.

Two binaries:

| Binary              | TFM      | Role                                                                  |
|---------------------|----------|-----------------------------------------------------------------------|
| **dnspymcp**        | net9.0   | MCP server. Stdio / Streamable-HTTP / SSE transports.                 |
| **dnspymcpagent**   | net4.8   | Lightweight debug backend that runs on the target host and speaks ICorDebug via dnSpy's `dndbg` engine. Persistent TCP + newline-delimited JSON. |

`dnspymcp` talks to `dnspymcpagent` over **one** persistent TCP connection
(no HTTP, no per-call headers, no reconnect storms).  Connection is opened
when the debugger session starts and closed when it ends.

---

## Why two binaries?

ICorDebug lives under .NET Framework. For cross-version debugging the only
safe host is an **out-of-process net48** debugger that loads the right
`mscordbi.dll` through `ICLRMetaHost`. That's the agent.

The MCP-facing half lives on net9 so it can use the official
`ModelContextProtocol` + AspNetCore packages and run side-by-side with other
modern-.NET tooling.

---

## Tool naming convention

Every MCP tool is tagged either `[FILE]` or `[LIVE]`:

* **`[FILE]`** — operates on a .dll/.exe on disk. Doesn't need the agent.
  Prefixed with `asm_file_*`, `decompile_*`, `il_*`, `file_patch_*`.
* **`[LIVE]`** — operates on a running / dumped process through the agent.
  Prefixed with `live_*`.

The first line of every tool description states the target context so the
LLM never confuses "I'm inspecting a file on disk" with "I'm poking at a
running process".

### Static (FILE) tools

```
asm_file_open / asm_file_close / asm_file_list / asm_file_current / asm_file_switch
asm_file_list_types / asm_file_list_methods
decompile_type / decompile_method
il_method / il_method_by_token
find_string / xref_to_method
file_patch_il_nop / file_patch_bytes / file_save_assembly
```

### Live (LIVE) tools — proxy to the agent

```
live_agent_open / live_agent_close / live_agent_list
live_agent_current / live_agent_switch / live_agent_list_methods
live_list_dotnet_processes / live_session_info
live_session_go / live_session_pause / live_session_terminate / live_wait_paused

live_thread_list / live_thread_stack / live_thread_current
live_list_modules / live_find_type / live_list_type_methods

live_bp_set_il / live_bp_set_by_name / live_bp_list
live_bp_delete / live_bp_enable / live_bp_disable

live_step_in / live_step_over / live_step_out

live_heap_find_instances / live_heap_read_object / live_heap_read_string / live_heap_stats
live_memory_read / live_memory_write / live_memory_read_int / live_disasm
```

---

## Running

### Target side (the host whose .NET process you want to debug)

```
dnspymcpagent.exe --host 0.0.0.0 --port 5555 --attach 1234
# optional: --token SECRET   (client must `auth` first)
# optional: --dump path\to\crash.dmp   (offline analysis, no ICorDebug)
```

Protocol: one NDJSON request per line, one NDJSON response per line.

```
>>> {"id":1,"method":"session.info"}
<<< {"id":1,"ok":true,"result":{...}}
```

Special method `__list__` enumerates every registered debug command.
Exactly one client at a time — a second connection is rejected.

### MCP-server side (the host running your LLM client)

```
dnspymcp.exe                                   # stdio (default — for Claude Desktop etc.)
dnspymcp.exe --transport http --bind-port 5556 # Streamable HTTP
dnspymcp.exe --transport sse  --bind-port 5556 # legacy SSE
```

The agent target is not a CLI concern — host and port are **required**
parameters of the `live_agent_open` tool, so the LLM must declare
where it's connecting every time. You can call it multiple times with
different `name`s to register several target agents.

---

## Build from source

Requirements: Windows, .NET SDK 9.0, .NET Framework 4.8 reference assemblies
(installed by VS / Build Tools or `dotnet workload install desktop`).

```
git clone --recurse-submodules https://github.com/Jimmy01240397/Dnspy-MCP.git
cd Dnspy-MCP
pwsh -File builder.ps1                 # full build: dnspy subset -> lib/ -> dist/
pwsh -File builder.ps1 -Zip            # also produce a release zip
pwsh -File builder.ps1 -SkipDnSpy      # reuse an already-populated lib/
pwsh -File builder.ps1 -Clean          # wipe lib/ and dist/
```

`builder.ps1` is the single entry point — the GitHub Actions release job
calls the exact same script, so local and CI builds are byte-identical.
The script only builds the `dnSpy.Debugger.DotNet.CorDebug` project out of
the dnSpy submodule (not the full dnSpy IDE) and copies the eight DLLs the
agent references into `lib/`.  `Krafs.Publicizer` then exposes the internal
`dndbg` surface without patching dnSpy source.  The dnSpy source itself is
tracked as a git submodule under `dnspy/`, but `lib/` is **not** committed
— it's a build artifact, regenerated by `builder.ps1`.

---

## Agent session registry

`dnspymcp` keeps a named registry of agent sessions so one MCP server can
drive several target hosts at once. An "agent session" is a persistent
TCP link to one `dnspymcpagent` process (which itself is pinned to one
target PID or dump at startup). The registry is idalib-style: open once,
switch for free — TCP auto-reconnects if the agent restarts, so you never
need to disconnect between tool calls.

```
live_agent_open(host, port, token?, name?)    # open/re-open a session, becomes active
live_agent_close(name)                         # disconnect TCP and drop the slot
live_agent_list()                              # list sessions, mark the active one
live_agent_current()                           # which slot LIVE calls go to right now
live_agent_switch(name)                        # route LIVE calls to another slot
```

Every `live_*` call targets the active slot unless you pass `agent=<name>`.
To debug a different PID, boot another `dnspymcpagent` on a different port
and `live_agent_open` against it — there is no runtime re-attach.

---

## Testing

The repo ships a pytest suite that covers both the FILE and LIVE surfaces
end-to-end. The LIVE fixtures spawn three cooperating processes for the
session:

1. **`dnspymcptest.exe`** — a tiny idle .NET Framework 4.8 target with a few
   managed types on the heap. Safe to attach to.
2. **`dnspymcpagent.exe`** — launched with `--attach <pid>` against the test
   target, listens on `127.0.0.1:5555`.
3. **`dnspymcp.exe`** — launched with `--transport http --bind-port 5556`,
   talks to the agent over NDJSON.

```
pwsh -File builder.ps1                                 # build dist/ (and lib/)
dotnet build dnspymcptest/dnspymcptest.csproj -c Debug # build the test target
pip install -r tests/requirements.txt
pytest tests -v --tb=short
```

The LIVE debugger must **only** attach to the bundled `dnspymcptest.exe` —
never point the test fixtures at an unrelated process. The CI workflow in
`.github/workflows/ci.yml` runs the same commands on `windows-latest`.

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for
the full text. `dnspymcp` vendors the `dnSpy` repository as a git submodule
under `dnspy/`; those sources remain under their original **GPLv3** license
(`dnspy/LICENSE.txt`). Only the compiled `dndbg` / CorDebug glue DLLs are
linked at runtime; no GPL source is redistributed as part of `dnspymcp`.

---

## Safety note

The agent can attach to any .NET process it has permission to open, set
breakpoints, read / write arbitrary memory, and terminate the target.  Only
run it against processes you own or have authorization to debug.
