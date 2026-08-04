// Makes LaunchBox plugins that raise LaunchBox notifications work, unchanged, under LiteBox.
//
// A plugin has no SDK call for notifications, so it reflects into the application assembly:
//
//     AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "LaunchBox")
//              .GetType("Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter")
//              .GetMethod("SendInfoNotification").Invoke(null, new object[]{ "hello", 0 });
//
// Under LiteBox that search comes up empty and the plugin quietly no-ops. So at boot — BEFORE any plugin
// is loaded — we register an assembly whose identity IS "LaunchBox", carrying LaunchBox's public
// Notifications API, with every call forwarded to LiteBox's own center. Source: the LbShim project;
// the built dll rides along as an embedded resource and is loaded FROM MEMORY.
//
// From memory, deliberately: LiteBox.exe lives in <LB>\Core, the very folder holding the REAL
// LaunchBox.dll. Writing our LaunchBox.dll there would overwrite LaunchBox's own assembly. Loading the
// bytes also means our identity is registered in the default load context first, so a plugin with a hard
// compile-time reference to LaunchBox binds HERE rather than dragging the real one — a WPF application
// assembly with licence-checking module initializers — into a WinForms host.
//
// Everything is fail-soft: no resource, no bridge type, a changed contract ⇒ a log line and plugins simply
// see no LaunchBox assembly, exactly as before this file existed.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using LiteBox.Notifications;

namespace LbApiHost.Host.Notifications;

internal static class LaunchBoxShim
{
    private const string ResourceName = "shim/LaunchBox.dll";
    private const string BridgeType = "LiteBox.Shim.ShimBridge";

    private static bool _installed;
    private static MethodInfo? _hostEvent;

    /// <summary>True once the compatibility assembly is live and wired to the center.</summary>
    public static bool Available => _hostEvent != null;

    /// <summary>Registers the compatibility assembly. Call ONCE, before plugins are loaded.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            var already = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "LaunchBox", StringComparison.OrdinalIgnoreCase));
            if (already != null)
            {
                // A real LaunchBox.dll got loaded first (LiteBox started from a LaunchBox process, a plugin
                // forced it…). Leave it alone: it owns the name, and plugins will reach ITS center.
                Console.WriteLine("[notify-shim] an assembly named 'LaunchBox' is already loaded — not shimming");
                return;
            }

            using var res = typeof(LaunchBoxShim).Assembly.GetManifestResourceStream(ResourceName);
            if (res == null) { Console.WriteLine("[notify-shim] resource missing: " + ResourceName); return; }
            using var ms = new MemoryStream();
            res.CopyTo(ms);
            ms.Position = 0;
            var asm = AssemblyLoadContext.Default.LoadFromStream(ms);

            var bridge = asm.GetType(BridgeType);
            var handler = bridge?.GetField("Handler", BindingFlags.Public | BindingFlags.Static);
            _hostEvent = bridge?.GetMethod("HostEvent", BindingFlags.Public | BindingFlags.Static);
            if (handler == null || _hostEvent == null)
            {
                _hostEvent = null;
                Console.WriteLine("[notify-shim] bridge type/members not found — LaunchBox notifications stay inert");
                return;
            }

            handler.SetValue(null, (Func<string, object[], object?>)Handle);
            Subscribe();
            Console.WriteLine($"[notify-shim] LaunchBox notification API available (assembly {asm.GetName().Version})");
        }
        catch (Exception ex)
        {
            _hostEvent = null;
            Console.WriteLine("[notify-shim] install failed: " + ex.Message);
        }
    }

    /// <summary>Walks the compatibility layer the way a PLUGIN does — by reflection, with no reference to
    /// anything of ours — and logs each step. Driven by --notify-demo; the point is that a regression in
    /// the shim shows up as a failed step here rather than as a plugin that quietly stops notifying.</summary>
    public static void SelfTest()
    {
        const string TypeName = "Unbroken.LaunchBox.Windows.Desktop.Notifications.NotificationCenter";
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "LaunchBox");
            if (asm == null) { Console.WriteLine("[notify-shim] self-test: assembly 'LaunchBox' not found"); return; }

            var t = asm.GetType(TypeName);
            if (t == null) { Console.WriteLine("[notify-shim] self-test: type not found: " + TypeName); return; }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static;
            var info = t.GetMethod("SendInfoNotification", Flags);
            var error = t.GetMethod("SendErrorNotification", Flags);
            var input = t.GetMethod("SendInputNotification", Flags);
            Console.WriteLine($"[notify-shim] self-test: resolved info={info != null} error={error != null} input={input != null}");

            info?.Invoke(null, new object[] { "LaunchBox-compat: info notification.", 0 });
            var actions = new List<KeyValuePair<string, Action>>
            {
                new("Say hi", () => NotificationCenter.Info("Hi, from a LaunchBox-compat action.")),
            };
            var handle = input?.Invoke(null, new object[] { "LaunchBox-compat: notification with actions.", actions, 0 });
            Console.WriteLine("[notify-shim] self-test: SendInputNotification returned " + (handle?.GetType().FullName ?? "null"));
        }
        catch (Exception ex) { Console.WriteLine("[notify-shim] self-test failed: " + ex); }
    }

    // ── shim → host ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every call a plugin makes lands here. Args are BCL types only (the bridge contract);
    /// an unknown verb returns null so an older host and a newer shim degrade instead of throwing.</summary>
    private static object? Handle(string verb, object[] args)
    {
        try
        {
            switch (verb)
            {
                case "send":
                {
                    string message = Arg<string>(args, 0) ?? "";
                    string kind = Arg<string>(args, 1) ?? "info";
                    int life = args.Length > 2 && args[2] is int i ? i : 0;
                    var actions = args.Length > 3 ? args[3] as IEnumerable<KeyValuePair<string, Action>> : null;
                    var icon = args.Length > 4 ? args[4] : null;
                    bool popup = !(args.Length > 5 && args[5] is bool p) || p;
                    bool markUnread = !(args.Length > 6 && args[6] is bool u) || u;

                    return NotificationCenter.Send(
                        message,
                        kind == "error" ? NotificationKind.Error
                            : kind == "progress" ? NotificationKind.Progress : NotificationKind.Info,
                        life,
                        actions?.Select(a => new NotificationAction(a.Key, a.Value)),
                        WpfIcon.ToBitmap(icon),
                        popup, markUnread);
                }
                case "read": NotificationCenter.MarkRead(Handle0(args)); return null;
                case "unread": NotificationCenter.MarkUnread(Handle0(args)); return null;
                case "dismiss": NotificationCenter.Dismiss(Handle0(args)); return null;
                case "remove": NotificationCenter.Remove(Handle0(args)); return null;
                case "busy": return null;   // LiteBox has no global busy indicator; the call is accepted, not mirrored
                default:
                    Console.WriteLine("[notify-shim] unknown verb: " + verb);
                    return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[notify-shim] '{verb}' failed: {ex.Message}");
            return null;
        }
    }

    private static LiteBoxNotification? Handle0(object[] args)
        => args != null && args.Length > 0 ? args[0] as LiteBoxNotification : null;

    private static T? Arg<T>(object[] args, int i) where T : class
        => args != null && i < args.Length ? args[i] as T : null;

    // ── host → shim ──────────────────────────────────────────────────────────────────────────────────

    private static void Subscribe()
    {
        NotificationCenter.Raised += n => Event("raised", n, n.Message, n.IsError, n.IsInProgress,
            n.LifeSpanSeconds > 0 ? n.LifeSpanSeconds : (int?)null, n.DateRaised,
            n.Actions.Select(a => new KeyValuePair<string, Action>(a.Label, a.Run)).ToList());
        NotificationCenter.Read += n => Event("read", n);
        NotificationCenter.Unread += n => Event("unread", n);
        NotificationCenter.Dismissed += n => Event("dismissed", n);
        NotificationCenter.Removed += n => Event("removed", n);
        NotificationCenter.Updated += n => Event("updated", n, n.Message);
    }

    private static void Event(string verb, LiteBoxNotification n, params object?[] extra)
    {
        var m = _hostEvent;
        if (m == null) return;
        var args = new object?[1 + extra.Length];
        args[0] = n;
        Array.Copy(extra, 0, args, 1, extra.Length);
        try { m.Invoke(null, new object?[] { verb, args }); }
        catch (Exception ex) { Console.WriteLine($"[notify-shim] event '{verb}': {ex.InnerException?.Message ?? ex.Message}"); }
    }
}

/// <summary>WPF ImageSource → GDI+ Image, for the LaunchBox signatures that take one. Best effort: a
/// non-bitmap or thread-affine source simply yields null and the popup draws its own glyph.</summary>
internal static class WpfIcon
{
    public static System.Drawing.Image? ToBitmap(object? imageSource)
    {
        if (imageSource is not System.Windows.Media.Imaging.BitmapSource src) return null;
        try
        {
            if (src.Dispatcher != null && !src.Dispatcher.CheckAccess() && !src.IsFrozen) return null;
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return new System.Drawing.Bitmap(ms);
        }
        catch { return null; }
    }
}
