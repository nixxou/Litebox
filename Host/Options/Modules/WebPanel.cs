// Web module config panel — STUB. The embedded web frontends (LiteBox Web / BigBox Web / database Web),
// each served from its own folder. A parallel agent replaces this body with the full settings UI; for now it
// returns the shared placeholder so the tab renders consistently.

#nullable enable

using System;
using System.Windows.Forms;

namespace LbApiHost.Host.Options;

internal static class WebPanel
{
    public static (Control panel, Action? apply) Build(float dpiS, bool readOnly)
        => ModulePanelKit.Placeholder(
            "Web frontends",
            "The embedded web server: LiteBox Web, BigBox Web and the database Web, each served from its own folder.",
            dpiS);
}
