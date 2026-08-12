// SOFT layer: block access to LaunchBox's administration UI while parental control is locked.
//
// LaunchBox is WPF and every admin surface is a Window shown via ShowDialog()/Show(); its View classes keep
// their REAL (un-obfuscated) names, captured by the 2026-08 write/UI audit. We Harmony-patch Window.ShowDialog
// and Window.Show and, when parental is configured AND locked, veto:
//   • any window whose full type name is in AdminWindows  → block it (it never shows) + point the user to unlock;
//   • the themed MessageBoxWindow whose text is destructive (delete a game/platform from the list, "permanently
//     delete…", "delete the media…") → force its model Result to No and skip it, cancelling the action up front.
// Everything else passes through untouched (main window, game details, ordinary confirmations, our own WinForms
// dialogs — which aren't WPF Windows anyway).
//
// This is the UX front line; the native .bin write-guard is the infallible backstop. Fully fail-safe: any error
// here degrades to "allow" and never breaks LaunchBox. Harmony is loaded from the embedded resource (single dll)
// only when this runs — never from the early StartupHook.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using HarmonyLib;

namespace LiteBoxParental
{
    internal static class AdminGuard
    {
        private static bool _installed;
        private static readonly object _gate = new object();

        /// <summary>True once we've stopped trying — either the patches are applied, or a real Harmony failure made
        /// the soft lock inert. False means "WPF wasn't up yet, retry me".</summary>
        internal static bool Installed => _installed;

        private const string MessageBoxWindowType =
            "Unbroken.LaunchBox.Windows.Desktop.DialogTheming.Controls.MessageBoxWindow";

        private const string ViewsNs = "Unbroken.LaunchBox.Windows.Desktop.Views.";

        // Admin windows matched by PATTERN, not an exhaustive list — every LaunchBox editor/manager follows a
        // naming convention, so we catch new ones (e.g. AddEditPlatformCategoryView) without re-auditing:
        //   • short name starts with "AddEdit" / "Edit" / "Manage"  (game/platform/category/playlist/emulator/…)
        //   • plus an explicit set for the non-pattern admin surfaces below.
        private static readonly HashSet<string> ExplicitAdmin = new HashSet<string>(StringComparer.Ordinal)
        {
            "WizardView",                  // all Tools > Import wizards
            "DownloadPlatformVideosView",
            "MediaManagerView",
            "OptionsView",
            "PluginManagerView",
            "ImportView", "ImportWizardView",
        };

        // Explicitly ALLOWED views (NOT admin — never block). Everything else in the Desktop.Views namespace is
        // treated as an admin dialog (block-most), so new editor/tool windows are covered without re-auditing.
        private static readonly HashSet<string> AllowedViews = new HashSet<string>(StringComparer.Ordinal)
        {
            "AchievementProfileView",   // consultation, not admin
            "ProgressDialogView",       // progress bars during operations — must stay visible
        };

        private static readonly HashSet<string> _seenViews = new HashSet<string>(StringComparer.Ordinal);

        private static bool IsAdminWindow(Type t)
        {
            var n = t.Name;
            if (AllowedViews.Contains(n)) return false;
            // Block EVERY LaunchBox Desktop.Views.* dialog (RootGameView, AuditView, AddEdit*, Manage*, Options…).
            if ((t.FullName ?? "").StartsWith(ViewsNs, StringComparison.Ordinal)) return true;
            // Non-Views admin surfaces (rare) still caught by name.
            return n.StartsWith("AddEdit", StringComparison.Ordinal)
                || n.StartsWith("Edit", StringComparison.Ordinal)
                || n.StartsWith("Manage", StringComparison.Ordinal)
                || ExplicitAdmin.Contains(n);
        }

        /// <summary>Arm the soft lock (idempotent). LaunchBox/BigBox only; Harmony is loaded here, never from the
        /// StartupHook. Any failure leaves the soft lock inert but the rest of the plugin fully working.</summary>
        public static void Install()
        {
            if (_installed) return;
            lock (_gate)
            {
                if (_installed) return;
                try
                {
                    if (!LockState.IsHost) { _installed = true; return; }   // never patch LiteBox / helpers
                    HarmonyLoader.Ensure();                                 // BCL: register the embedded-Harmony resolver
                    if (TryInstallPatches()) _installed = true;             // else WPF not up yet → RETRY on the next call
                }
                catch (Exception ex)
                {
                    _installed = true;   // a real Harmony failure — don't spin retrying a broken patch
                    Log.Line("[AdminGuard] install failed (soft-lock inert): " + ex.Message);
                }
            }
        }

        // Returns true once patched. Returns false (no throw) when WPF isn't loaded yet, so Install() is retried on
        // the next trigger (ctor was too early). Kept SEPARATE from Install so the HarmonyLib references here are
        // JIT-compiled only AFTER HarmonyLoader.Ensure() registered the resolver.
        private static bool TryInstallPatches()
        {
            var win = AccessTools.TypeByName("System.Windows.Window");
            if (win == null) { Log.Line("[AdminGuard] WPF not ready yet — will retry on the next trigger"); return false; }

            var h = new Harmony("litebox.parental.adminguard");
            var sd = AccessTools.Method(win, "ShowDialog", Type.EmptyTypes);
            if (sd != null) h.Patch(sd, prefix: new HarmonyMethod(typeof(AdminGuard).GetMethod(nameof(ShowDialogPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            var sh = AccessTools.Method(win, "Show", Type.EmptyTypes);
            if (sh != null) h.Patch(sh, prefix: new HarmonyMethod(typeof(AdminGuard).GetMethod(nameof(ShowPrefix), BindingFlags.Static | BindingFlags.NonPublic)));

            // Grey out admin items while locked — in BOTH right-click context menus (ContextMenu.Opened) AND the
            // top menu-bar / MENU / TOOLS drop-downs (MenuItem.SubmenuOpened). Class handlers fire for every menu,
            // so there is no obfuscated builder to find.
            try
            {
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
                EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened));
            }
            catch (Exception ex) { Log.Line("[AdminGuard] menu hook failed: " + ex.Message); }

            try { Branding.Start(); } catch { }   // live parental-status in the "Licensed to…" corner

            Log.Line("[AdminGuard] armed (ShowDialog=" + (sd != null) + " Show=" + (sh != null) + " + menus + status-corner)");
            return true;
        }

        // Items WE greyed, so we can UN-grey exactly those on unlock (and never re-enable something LaunchBox
        // disabled for its own reasons). Weak keys → no leak if a MenuItem is discarded.
        private static readonly ConditionalWeakTable<MenuItem, object> _greyed = new ConditionalWeakTable<MenuItem, object>();
        private static readonly object _mark = new object();

        // ── Menu grey-out (context menus + menu-bar drop-downs) ───────────────
        // Runs on EVERY menu open while parental is configured + SoftContextMenu is on: greys admin items when
        // LOCKED, and re-enables the ones WE greyed when UNLOCKED (covers menus that reuse their MenuItems).
        private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (!LockState.ScopeActive) return;
            try
            {
                if (!(sender is ContextMenu cm)) return;
                Branding.Reapply();                                                // keep the status-corner live
                RefreshParentalLabels(cm.Items);                                    // keep our Unlock label live
                if (TestConfig.SoftContextMenu) ProcessMenu(cm.Items, LockState.Locked);
            }
            catch (Exception ex) { Log.Line("[AdminGuard] context-menu error: " + ex.Message); }
        }

        private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (!LockState.ScopeActive) return;
            try
            {
                if (!(sender is MenuItem mi)) return;
                Branding.Reapply();                                                // keep the status-corner live
                RefreshParentalLabels(mi.Items);                                    // keep our Unlock label live
                if (TestConfig.SoftContextMenu) ProcessMenu(mi.Items, LockState.Locked);
            }
            catch (Exception ex) { Log.Line("[AdminGuard] submenu error: " + ex.Message); }
        }

        // LaunchBox caches our menu item's caption at build time, so a lock/unlock never updates it. Re-apply the
        // live label to OUR item on every menu open. Runs regardless of the grey-out toggle (it's just the label).
        private static void RefreshParentalLabels(ItemCollection items)
        {
            foreach (var obj in items)
            {
                if (!(obj is MenuItem mi)) continue;
                var h = (mi.Header?.ToString() ?? "").ToLowerInvariant();
                if (h.Contains("parental control") && (h.Contains("unlock") || h.Contains("to lock") || h.Contains("locked")))
                    mi.Header = UnlockMenuItem.CurrentLabel();
                else if (mi.HasItems) RefreshParentalLabels(mi.Items);
            }
        }

        private static void ProcessMenu(ItemCollection items, bool locked)
        {
            foreach (var obj in items)
            {
                if (!(obj is MenuItem mi)) continue;
                if (locked && IsAdminMenuHeader(mi.Header?.ToString()))
                {
                    if (mi.IsEnabled) { mi.IsEnabled = false; _greyed.AddOrUpdate(mi, _mark); }
                    // don't recurse — the whole submenu is unreachable while greyed
                }
                else
                {
                    if (_greyed.TryGetValue(mi, out _)) { mi.IsEnabled = true; _greyed.Remove(mi); }   // undo OUR grey
                    if (mi.HasItems) ProcessMenu(mi.Items, locked);
                }
            }
        }

        // Block-most / allow-few: while locked, EVERY menu item is admin EXCEPT a small safe set (navigation,
        // view, launch, exit). This greys Install/Edit/Media/File Management/Add/Delete/Reset/Add-to-Playlist in
        // the game menu, everything in Tools, "Tools" inside MENU, and Add/Edit/Delete on category/platform/playlist.
        private static bool IsAdminMenuHeader(string header)
        {
            if (string.IsNullOrEmpty(header)) return false;
            var h = header.Trim().ToLowerInvariant();
            if (h.Length == 0) return false;

            // SAFE — never grey (checked first).
            if (h.Contains("parental control")       // OUR OWN items — above all the Unlock entry (never lock the key!)
                || h.StartsWith("view")              // View, View Achievements Profile
                || h.StartsWith("configure")         // Configure Layout
                || h.StartsWith("arrange")           // Arrange By
                || h.StartsWith("select random")
                || h == "play" || h.StartsWith("play ")
                || h == "help"
                || h.StartsWith("quit")
                || h.StartsWith("big box")
                // text-editing menus (search box, etc.) — keep usable while locked
                || h == "cut" || h == "copy" || h == "paste" || h == "undo" || h == "redo" || h == "select all")
                return false;

            // Everything else in these menus is an administration action → grey it while locked.
            return true;
        }

        // ShowDialog() returns bool? — on veto, skip the original and report a cancellation.
        private static bool ShowDialogPrefix(object __instance, ref bool? __result)
        {
            if (!Veto(__instance)) return true;
            __result = false;
            return false;
        }

        private static bool ShowPrefix(object __instance) => !Veto(__instance);

        /// <summary>True ⇒ block this window. NEVER throws (a guard bug must not break LaunchBox → default allow).</summary>
        private static bool Veto(object window)
        {
            try
            {
                if (window == null) return false;
                // Only a configured + locked session gates anything; unlock lifts it entirely.
                if (!LockState.ScopeActive || !LockState.Locked) return false;

                TestConfig.EnsureLoaded();
                var t = window.GetType();
                var full = t.FullName ?? "";

                if (full == MessageBoxWindowType)
                {
                    if (TestConfig.SoftConfirmGuard && IsConfirmation(window))
                    {
                        ForceNo(window);
                        Notify("This action was blocked because parental control is locked.");
                        return true;
                    }
                    return false;   // errors / info (or guard disabled) → shown normally
                }

                if (TestConfig.SoftAdminLock && IsAdminWindow(t) && !TestConfig.SoftAllowWindows.Contains(t.Name))
                {
                    Notify("Administration is locked by parental control.\n\nUnlock first: Tools → \"Parental control: Locked (click to unlock)\".");
                    return true;
                }
                // Discovery aid: log any LaunchBox View we DIDN'T block (once each) so a missed admin window surfaces.
                if (full.StartsWith(ViewsNs, StringComparison.Ordinal) && !AllowedViews.Contains(t.Name))
                    lock (_seenViews) { if (_seenViews.Add(t.Name)) Log.Line("[AdminGuard] view not blocked: " + t.Name); }
                return false;
            }
            catch (Exception ex) { Log.Line("[AdminGuard] veto error: " + ex.Message); return false; }
        }

        // Universal confirmation detector: a themed MessageBox with a QUESTION icon is an "are you sure?" prompt
        // (delete, expand, combine, migrate, update…) — language-independent, unlike its text. Errors / info
        // (Icon = Error / Information) are NOT confirmations and stay visible. Falls back to the localized
        // destructive-template match if the icon can't be read.
        private static bool IsConfirmation(object window)
        {
            try
            {
                var dc = window.GetType().GetProperty("DataContext")?.GetValue(window);
                var icon = dc?.GetType().GetProperty("Icon")?.GetValue(dc)?.ToString();
                if (!string.IsNullOrEmpty(icon) &&
                    (icon.Equals("Question", StringComparison.OrdinalIgnoreCase) || icon.Equals("Warning", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }
            return IsDestructive(ReadDcText(window));   // fallback
        }

        private static string ReadDcText(object window)
        {
            try
            {
                var dc = window.GetType().GetProperty("DataContext")?.GetValue(window);
                return dc?.GetType().GetProperty("Text")?.GetValue(dc)?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // LANGUAGE-INDEPENDENT destructive-confirmation detection. LaunchBox localises its dialog text via
        // Unbroken.LaunchBox.Properties.Strings (a resx-backed class: static string props return the CURRENT
        // language). We pick the destructive templates by their KEY NAME (English identifiers like
        // "PermanentlyDelete…", stable across languages), read their localised value, and match the dialog text
        // against the template's fixed segments (around {0}). Falls back to English fragments only if reflection
        // finds nothing.
        private static bool _templatesBuilt;
        private static readonly List<string[]> _destructiveSegs = new List<string[]>();   // fixed segments per template

        private static void BuildTemplates()
        {
            if (_templatesBuilt) return;
            _templatesBuilt = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unbroken.LaunchBox");
                var t = asm?.GetType("Unbroken.LaunchBox.Properties.Strings");
                if (t == null) { Log.Line("[AdminGuard] Strings resource not found — confirm-guard uses English fallback"); return; }
                var matched = new List<string>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    if (p.PropertyType != typeof(string) || p.GetIndexParameters().Length != 0) continue;
                    // ONLY the dialog MESSAGES (full sentences) — never Label*/MenuItem*/Field* button captions,
                    // which are short and would false-match. LaunchBox confirmation prompts are all "Message…".
                    if (!p.Name.StartsWith("Message", StringComparison.Ordinal)) continue;
                    var k = p.Name.ToLowerInvariant();
                    if (!(k.Contains("delete") || k.Contains("remove") || k.Contains("permanently") || k.Contains("uninstall"))) continue;
                    string val; try { val = p.GetValue(null) as string; } catch { continue; }
                    if (string.IsNullOrWhiteSpace(val)) continue;
                    var segs = Regex.Split(val, @"\{\d+\}").Select(s => s.Trim()).Where(s => s.Length >= 4).ToArray();
                    if (segs.Length == 0) continue;   // nothing distinctive to match on
                    _destructiveSegs.Add(segs);
                    matched.Add(p.Name);
                }
                Log.Line("[AdminGuard] destructive Message templates: " + matched.Count
                    + (TestConfig.DebugLog && matched.Count > 0 ? " [" + string.Join(", ", matched) + "]" : ""));
            }
            catch (Exception ex) { Log.Line("[AdminGuard] BuildTemplates error: " + ex.Message); }
        }

        // Every fixed segment of the template must appear in the text, in order (placeholders were dropped).
        private static bool MatchesTemplate(string text, string[] segs)
        {
            int pos = 0;
            foreach (var s in segs)
            {
                int idx = text.IndexOf(s, pos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return false;
                pos = idx + s.Length;
            }
            return true;
        }

        private static bool IsDestructive(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            BuildTemplates();
            foreach (var segs in _destructiveSegs)
                if (MatchesTemplate(text, segs)) return true;
            if (_destructiveSegs.Count > 0) return false;   // templates loaded → trust them

            // Fallback (English) only when the Strings resource couldn't be read.
            var t = text.ToLowerInvariant();
            return t.Contains("permanently delete")
                || t.Contains("delete the media")
                || t.Contains("delete the following")
                || (t.Contains("delete") && t.Contains("collection"));
        }

        // Set MessageBoxModel.Result to No (or Cancel) so the caller reads a cancellation after we skip the dialog.
        private static void ForceNo(object window)
        {
            try
            {
                var dc = window.GetType().GetProperty("DataContext")?.GetValue(window);
                var rp = dc?.GetType().GetProperty("Result");
                if (rp == null || !rp.CanWrite || !rp.PropertyType.IsEnum) return;
                var val = EnumValue(rp.PropertyType, "No") ?? EnumValue(rp.PropertyType, "Cancel") ?? EnumValue(rp.PropertyType, "None");
                if (val != null) rp.SetValue(dc, val);
            }
            catch { }
        }

        private static object EnumValue(Type enumType, string name)
        {
            try { return Enum.IsDefined(enumType, name) ? Enum.Parse(enumType, name) : null; } catch { return null; }
        }

        private static void Notify(string msg)
        {
            try { MessageBox.Show(msg, "Parental control", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
        }
    }
}
