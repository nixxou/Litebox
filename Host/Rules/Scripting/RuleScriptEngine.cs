// The C# script engine — Roslyn scripting (Microsoft.CodeAnalysis.CSharp.Scripting 4.12), the
// successor of BigBoxProfile's AHK slot. The four Roslyn assemblies (~12 MB) sleep in
// <LB>\ThirdParty\Roslyn, NOT in Core — resolved by the lazy ALC hook below, loaded on the first
// script only (this file is the single place Roslyn types appear; everything heavy is NoInlining).
//
// Scripts compile ONCE per session (cache keyed on the text) — the first script pays Roslyn's
// warmup (~1 s), every run after is microseconds. Default imports cover the everyday toolbox:
// IO, LINQ, regex, System.Text.Json, System.Xml.Linq, diagnostics — plus our Scripting namespace
// (the clean HID records). Runs are watchdogged: a script that never returns is abandoned after
// 10 s with a log line instead of hanging the launch (no safe thread abort exists in .NET — the
// runaway task is left behind, documented).

#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using LbApiHost.Host.Diag;

namespace LbApiHost.Host.Rules.Scripting;

internal static class RuleScriptEngine
{
    private const string Tag = "script";
    private const int TimeoutMs = 10_000;

    private static bool _resolverInstalled;
    private static readonly ConcurrentDictionary<string, object> _cache = new();   // code → Script<object>

    public static string RoslynDir
        => Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "ThirdParty", "Roslyn");

    private static readonly string[] RoslynAssemblies =
    {
        "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.CodeAnalysis.Scripting", "Microsoft.CodeAnalysis.CSharp.Scripting",
    };

    private static void EnsureResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (Array.IndexOf(RoslynAssemblies, name.Name) < 0) return null;
            string p = Path.Combine(RoslynDir, name.Name + ".dll");
            if (!File.Exists(p)) return null;
            try { return ctx.LoadFromAssemblyPath(p); }
            catch (Exception ex) { LbLog.Warn(Tag, $"{name.Name} load failed: {ex.Message}"); return null; }
        };
    }

    /// <summary>Compile only — the dialog's "Check" button. (true, "OK") or (false, diagnostics).</summary>
    public static (bool Ok, string Message) Check(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (true, "(empty)");
        EnsureResolver();
        try { return CheckCore(code); }
        catch (Exception ex) { return (false, "Script engine unavailable: " + ex.Message); }
    }

    /// <summary>Compiles (cached) and runs with the watchdog. (true, "") or (false, why). The
    /// globals object is mutated in place — Exe/Args after the run ARE the script's output.</summary>
    public static (bool Ok, string Error) Run(string code, RuleScriptGlobals globals)
    {
        if (string.IsNullOrWhiteSpace(code)) return (true, "");
        EnsureResolver();
        try { return RunCore(code, globals); }
        catch (Exception ex) { return (false, "Script engine unavailable: " + ex.Message); }
    }

    // ── everything below touches Roslyn types (JIT-isolated) ──────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object GetOrCompile(string code)
        => _cache.GetOrAdd(code, static c =>
        {
            var options = Microsoft.CodeAnalysis.Scripting.ScriptOptions.Default
                .AddReferences(
                    typeof(object).Assembly,                                   // System.Private.CoreLib
                    typeof(System.Linq.Enumerable).Assembly,                   // System.Linq
                    typeof(System.Text.RegularExpressions.Regex).Assembly,     // regex
                    typeof(System.Text.Json.JsonSerializer).Assembly,          // JSON
                    typeof(System.Xml.Linq.XDocument).Assembly,                // XML
                    typeof(System.Diagnostics.Process).Assembly,               // processes
                    typeof(File).Assembly,                                     // IO
                    typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,    // dynamic (Game/Emulator)
                    typeof(RuleScriptGlobals).Assembly)                        // us (records, API)
                .AddImports(
                    "System", "System.IO", "System.Linq", "System.Collections.Generic",
                    "System.Text", "System.Text.RegularExpressions", "System.Text.Json",
                    "System.Xml.Linq", "System.Diagnostics",
                    "LbApiHost.Host.Rules.Scripting");
            // The plugin SDK, when it is around (Core): scripts may cast Game to IGame. Loaded by
            // NAME so this engine never hard-references it — the selftest harness has no SDK.
            try
            {
                var sdk = System.Reflection.Assembly.Load("Unbroken.LaunchBox.Plugins");
                options = options.AddReferences(sdk).AddImports("Unbroken.LaunchBox.Plugins.Data");
            }
            catch { }
            var script = Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript
                .Create<object>(c, options, typeof(RuleScriptGlobals));
            // Compile() RETURNS diagnostics (it does not throw) — promote errors to the exception
            // both callers key on, so a broken script is reported and never cached.
            var diags = script.Compile();
            if (System.Linq.ImmutableArrayExtensions.Any(diags,
                    d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                throw new Microsoft.CodeAnalysis.Scripting.CompilationErrorException(
                    "compilation failed", diags);
            return script;
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool, string) CheckCore(string code)
    {
        try { GetOrCompile(code); return (true, "OK"); }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        { return (false, string.Join("\r\n", ex.Diagnostics)); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (bool, string) RunCore(string code, RuleScriptGlobals globals)
    {
        object script;
        try { script = GetOrCompile(code); }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        { return (false, "compile: " + string.Join(" | ", ex.Diagnostics)); }

        var typed = (Microsoft.CodeAnalysis.Scripting.Script<object>)script;
        // catchException: a runtime throw lands in ScriptState.Exception instead of faulting the task.
        var task = Task.Run(() => typed.RunAsync(globals, _ => true));
        if (!task.Wait(TimeoutMs))
        {
            LbLog.Warn(Tag, $"script still running after {TimeoutMs / 1000}s — abandoned (the task keeps running; fix the script)");
            return (false, "timeout");
        }
        if (task.Result.Exception != null) return (false, task.Result.Exception.Message);
        return (true, "");
    }
}
