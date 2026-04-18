using dndbg.COM.CorDebug;
using dndbg.Engine;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Minimal <see cref="DebugOptionsProvider"/> implementation. dnSpy's DebugOptions type
/// is a data bag with a non-nullable <c>DebugOptionsProvider</c> property — leaving it null
/// causes the CreateProcess / LoadModule callback handlers inside dndbg to NRE, which is
/// swallowed by the dispatcher and halts the attach-time callback burst mid-stride. We
/// don't care about JIT-optimization tweaks or JMC in a reverse-engineering harness, so
/// return sane defaults for everything.
/// </summary>
internal sealed class DefaultDebugOptionsProvider : DebugOptionsProvider
{
    public override CorDebugJITCompilerFlags GetDesiredNGENCompilerFlags(DnProcess process)
        => CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION;

    public override ModuleLoadOptions GetModuleLoadOptions(DnModule module) => new ModuleLoadOptions
    {
        JITCompilerFlags = CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION,
        ModuleTrackJITInfo = true,
        ModuleAllowJitOptimizations = false,
        JustMyCode = false,
    };
}
