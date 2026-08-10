// litebox-parentalcontrol.asi  —  loaded by Ultimate ASI Loader (winhttp.dll proxy)
// before .NET starts, into BOTH LaunchBox.exe and BigBox.exe.
//
// Hooks CreateFileW: any READ on LB\Data\Platforms\*.xml is transparently redirected to
// a memory-backed anonymous pipe streaming a FILTERED XML — keeping only games that pass
// parental control, and pruning their orphan related rows. A game is kept iff BOTH:
//   • its <Rating> passes the rules (Whitelist/Blacklist + wildcard Rule= patterns), AND
//   • its <ID> is NOT in the per-game blocked set (BlockedId=, the "requires parental" flag).
//
// Config = LB\Core\litebox-parental.dat, the flat file LiteBox writes (Host/Parental/
// ParentalNativeExport). Keys: LaunchBoxEnabled, BigBoxEnabled, PinSet, Mode, repeated
// Rule=, repeated BlockedId=, optional PluginPath (anti-tamper). No file / scope off for
// this process → the ASI is inert.
//
// Cold-start gate (before the managed plugin speaks), PER PROCESS:
//   • LaunchBox.exe → filter iff LaunchBoxEnabled (LB has no "boots locked" notion — our
//     config alone decides);
//   • BigBox.exe    → filter iff BigBoxEnabled AND a non-empty <LockPin> is set (BigBox
//     boots locked only then). BigBox also gets its <Allow*WhileLocked> flags forced false.
// Once the managed plugin calls litebox_parental_set_filtering() it is the sole authority.
//
// No temp files on disk; only the small kept-ID set is cached per platform, and the
// filtered XML is re-streamed through a fresh pipe on each open. WRITES are the managed
// plugin's job (it blocks File.Copy into Data\ while locked — see WS0/WS5.2).
//
// Adapted from the proven extenddb.asi (same MinHook + streaming-pipe machinery), with the
// ExtendDB module/JSON/RegionPriorities cruft removed and LaunchBox.exe activation added.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlwapi.h>
#include <MinHook.h>

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <fstream>
#include <iterator>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#pragma comment(lib, "shlwapi.lib")

// extenddb.asi version — bump on meaningful changes. Logged at boot
// together with the compile timestamp and this module's full path.
// 2.0.0: renamed from natifblock.asi + removed the dead CefSharp
// disable-gpu string patch (LB 13.28 uses WebView2).
#define LITEBOX_PARENTAL_ASI_VERSION "1.0.0"

// -------------------- logging --------------------

static std::wstring g_logPath;
static std::mutex   g_logMutex;

// Gate keyed on the managed plugin's debug setting — extenddb.asi writes
// litebox-parental.log ONLY when ExtendDBConfig.json's "DebugLog" field is
// "All Events". Set once at boot by LoadExtendDbDebugSetting() (called
// from DllMain after LoadParentalConfig). Default false so the kiosk
// install stays silent unless the user explicitly opts in. Logs that
// fire BEFORE the gate decision (boot banner, parental config load)
// are dropped on purpose — re-enable + restart to capture them.
static bool g_debugLog = false;

static void Log(const std::string& line)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_debugLog) return;
    if (g_logPath.empty()) return;
    FILE* f = nullptr;
    if (_wfopen_s(&f, g_logPath.c_str(), L"ab") != 0 || f == nullptr) return;
    SYSTEMTIME st; GetLocalTime(&st);
    fprintf(f, "[%04u-%02u-%02uT%02u:%02u:%02u.%03u] %s\r\n",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
            line.c_str());
    fclose(f);
}

// -------------------- string utils --------------------

static bool ContainsCI(const std::wstring& hay, const wchar_t* needle)
{
    return StrStrIW(hay.c_str(), needle) != nullptr;
}

static bool EndsWithCI(const std::wstring& s, const wchar_t* suffix)
{
    size_t n = wcslen(suffix);
    if (s.size() < n) return false;
    return _wcsicmp(s.c_str() + s.size() - n, suffix) == 0;
}

static std::string Narrow(const std::wstring& w)
{
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

static std::wstring Widen(const std::string& s)
{
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), n);
    return w;
}

// -------------------- XML filter (streaming, line-based) --------------------

static std::string ExtractTagValue(const std::string& s, const char* tag)
{
    std::string open  = std::string("<") + tag + ">";
    std::string close = std::string("</") + tag + ">";
    size_t a = s.find(open);
    if (a == std::string::npos) return {};
    a += open.size();
    size_t b = s.find(close, a);
    if (b == std::string::npos) return {};
    return s.substr(a, b - a);
}

// -------------------- parental-control config --------------------
//
// extenddb.asi reads LB\Core\ExtendDBParental.dat — the SAME flat-INI
// config the managed ExtendDB plugin writes. Only three keys matter
// here: BigBoxEnabled, Mode, the repeated Rule lines, and PluginPath.
// If the file is absent extenddb.asi does nothing at all (g_configPresent
// stays false); same when BigBoxEnabled is not 1.
//
// PluginPath is the ExtendDB plugin DLL's path relative to LB\Core. It
// drives the anti-tamper check (see EnforcePluginPresence): if the
// config exists but that DLL is gone, someone removed the plugin to
// bypass parental control, and we abort the program launch.
//
// Rule matching mirrors the C# side (ParentalControlManager):
// whole-string, case-insensitive, '*' = any run, '?' = one char.
//   Whitelist -> a game is kept only if some rule matches its Rating.
//   Blacklist -> a game is kept unless some rule matches its Rating.

static bool                            g_configPresent    = false;  // litebox-parental.dat exists
static bool                            g_bigBoxEnabled    = false;  // BigBoxEnabled=1
static bool                            g_launchBoxEnabled = false;  // LaunchBoxEnabled=1
static bool                            g_pinSet           = false;  // PinSet=1 (BigBox LockPin present)
static int                             g_mode             = 0;      // 0 = Whitelist, 1 = Blacklist
static std::vector<std::string>        g_rules;                     // Rule= wildcard patterns
static std::unordered_set<std::string> g_blockedIds;                // BlockedId= per-game "requires parental" IDs
static std::string                     g_pluginPath;                // PluginPath= (relative to LB\Core) — anti-tamper

static std::string TrimCopy(const std::string& s)
{
    size_t a = s.find_first_not_of(" \t\r\n");
    if (a == std::string::npos) return {};
    size_t b = s.find_last_not_of(" \t\r\n");
    return s.substr(a, b - a + 1);
}

static std::string ToLowerCopy(const std::string& s)
{
    std::string r(s);
    std::transform(r.begin(), r.end(), r.begin(),
                   [](unsigned char c) { return (char)std::tolower(c); });
    return r;
}

static bool ParseConfigBool(const std::string& v)
{
    std::string s = ToLowerCopy(TrimCopy(v));
    return s == "1" || s == "true" || s == "yes" || s == "on";
}

// Whole-string, case-insensitive wildcard match. '*' = any run
// (including empty), '?' = exactly one char. Mirrors the C# regex
// (^ + escaped pattern with .* and . + $).
static bool WildcardMatchCI(const std::string& text, const std::string& pattern)
{
    auto lc = [](char c) { return (char)std::tolower((unsigned char)c); };
    size_t t = 0, p = 0;
    size_t star = std::string::npos, mark = 0;
    while (t < text.size())
    {
        if (p < pattern.size() && (pattern[p] == '?' || lc(pattern[p]) == lc(text[t])))
        {
            ++t; ++p;
        }
        else if (p < pattern.size() && pattern[p] == '*')
        {
            star = p++;
            mark = t;
        }
        else if (star != std::string::npos)
        {
            p = star + 1;
            t = ++mark;
        }
        else
        {
            return false;
        }
    }
    while (p < pattern.size() && pattern[p] == '*') ++p;
    return p == pattern.size();
}

// Applies the configured Whitelist/Blacklist mode to a game's Rating.
static bool IsRatingAllowed(const std::string& rating)
{
    bool matched = false;
    for (const auto& rule : g_rules)
    {
        if (WildcardMatchCI(rating, rule)) { matched = true; break; }
    }
    return (g_mode == 0) ? matched : !matched;  // 0 = Whitelist, 1 = Blacklist
}

// Reads ExtendDBParental.dat (flat INI, UTF-8 no BOM). Missing file ->
// g_configPresent stays false. Unknown keys are ignored. Called once
// from DllMain before the hook is installed.
static void LoadParentalConfig(const std::wstring& path)
{
    std::ifstream in(path.c_str(), std::ios::binary);
    if (!in)
    {
        Log("[Boot] ExtendDBParental.dat not found: " + Narrow(path));
        return;
    }
    g_configPresent = true;

    std::string line;
    int ruleCount = 0;
    while (std::getline(in, line))
    {
        line = TrimCopy(line);
        if (line.empty() || line[0] == '#' || line[0] == ';') continue;

        size_t eq = line.find('=');
        if (eq == std::string::npos || eq == 0) continue;

        std::string key = ToLowerCopy(TrimCopy(line.substr(0, eq)));
        std::string val = TrimCopy(line.substr(eq + 1));

        if (key == "bigboxenabled")
            g_bigBoxEnabled = ParseConfigBool(val);
        else if (key == "launchboxenabled")
            g_launchBoxEnabled = ParseConfigBool(val);
        else if (key == "pinset")
            g_pinSet = ParseConfigBool(val);
        else if (key == "mode")
            g_mode = (ToLowerCopy(val) == "blacklist") ? 1 : 0;
        else if (key == "rule")
        {
            if (!val.empty()) { g_rules.push_back(val); ++ruleCount; }
        }
        else if (key == "blockedid")
        {
            if (!val.empty()) g_blockedIds.insert(val);
        }
        else if (key == "pluginpath")
            g_pluginPath = val;
        // Version: ignored.
    }

    char msg[256];
    snprintf(msg, sizeof(msg),
             "[Boot] config loaded: LaunchBoxEnabled=%d BigBoxEnabled=%d PinSet=%d mode=%s rules=%d blocked=%zu",
             g_launchBoxEnabled ? 1 : 0, g_bigBoxEnabled ? 1 : 0, g_pinSet ? 1 : 0,
             g_mode == 1 ? "Blacklist" : "Whitelist", ruleCount, g_blockedIds.size());
    Log(msg);
}

// -------------------- ExtendDB debug-log gate --------------------
//
// Reads ExtendDBConfig.json (UTF-8 JSON written by the managed plugin
// with Newtonsoft.Json Formatting.Indented) and sets the
// g_debugLog gate iff "DebugLog" = "All Events".
//
// Line-based parse (no JSON dep — extenddb.asi has only MinHook):
// Indented JSON puts each field on its own line as
//   `  "Name": "value",`
// so finding the key, jumping to the next colon and extracting the
// substring between the next pair of double quotes covers every
// case Newtonsoft writes. The DebugLog field always carries a string
// value (no escapes), so no JSON-unescape pass is needed. Any
// unexpected shape leaves the gate at its silent default — that
// matches the C# plugin's behavior when the field reads "No Debug
// Log" or is missing entirely.
static void LoadExtendDbDebugSetting(const std::wstring& configPath)
{
    std::ifstream in(configPath.c_str(), std::ios::binary);
    if (!in) return;  // no config (ExtendDB not installed) → keep silent default.

    std::string line;
    while (std::getline(in, line))
    {
        // Quote-bounded match so `"DebugLogXxx"` (hypothetical sibling
        // field) can't match `"DebugLog"`.
        size_t k = line.find("\"DebugLog\"");
        if (k == std::string::npos) continue;
        size_t col = line.find(':', k);
        if (col == std::string::npos) continue;
        size_t q1 = line.find('"', col + 1);
        if (q1 == std::string::npos) continue;
        size_t q2 = line.find('"', q1 + 1);
        if (q2 == std::string::npos) continue;
        std::string val = line.substr(q1 + 1, q2 - q1 - 1);
        if (val == "All Events") g_debugLog = true;
        return;  // key found — done, whatever the value was.
    }
}

// -------------------- ExtendDB module flags --------------------
//
// extenddb.asi spans TWO ExtendDB modules (see
// docs/welcomescreen-modules.md → "L'ASI natif"):
//   • parental → the CreateFileW read-filter, the anti-tamper plugin-
//     presence check, and the BigBoxSettings lock hardening.
//   • cache/tweaks → the RegionPriorities=World append.
// Each behaviour is gated INDEPENDENTLY on its module flag, read from
// the SAME ExtendDBConfig.json the managed plugin parses (we already
// read "DebugLog" from it). A flag absent / file missing → default ON,
// so a stock install keeps extenddb.asi's historical behaviour. Renaming
// the .asi is the managed-side HARD fallback (only when BOTH modules are
// off); this in-process gating is the soft, per-behaviour one.
static bool g_moduleParental = true;  // ExtendDBConfig.json "ModuleParental"
static bool g_moduleCache    = true;  // ExtendDBConfig.json "ModuleCache"

// Reads a JSON bool field written by Newtonsoft Formatting.Indented as
//   `  "Key": true,`
// The exact token `"Key":` (no space before the colon — Newtonsoft's
// shape) disambiguates from any longer sibling key. Returns def when the
// file or key is absent, or the value isn't a bare true/false.
static bool ReadExtendDbBool(const std::wstring& configPath, const char* key, bool def)
{
    std::ifstream in(configPath.c_str(), std::ios::binary);
    if (!in) return def;

    std::string token = std::string("\"") + key + "\":";
    std::string line;
    while (std::getline(in, line))
    {
        size_t k = line.find(token);
        if (k == std::string::npos) continue;
        size_t p = k + token.size();
        while (p < line.size() && (line[p] == ' ' || line[p] == '\t')) ++p;
        if (line.compare(p, 4, "true") == 0) return true;
        if (line.compare(p, 5, "false") == 0) return false;
        return def; // key present but value unexpected
    }
    return def;
}

// Loads the module flags extenddb.asi cares about. Called once from
// DllMain right after LoadExtendDbDebugSetting (same JSON path).
static void LoadExtendDbModuleFlags(const std::wstring& configPath)
{
    g_moduleParental = ReadExtendDbBool(configPath, "ModuleParental", true);
    g_moduleCache    = ReadExtendDbBool(configPath, "ModuleCache",    true);
    char msg[160];
    snprintf(msg, sizeof(msg),
             "[Boot] modules: parental=%d cache=%d",
             g_moduleParental ? 1 : 0, g_moduleCache ? 1 : 0);
    Log(msg);
}

// Resolves the directory holding ExtendDBConfig.json from the
// PluginPath recorded in ExtendDBParental.dat (relative to LB\Core,
// or absolute) — same logic as EnforcePluginPresence's path resolution
// but stripped down to the directory. Falls back to the LB-standard
// convention when no PluginPath is recorded (older config files).
static std::wstring ResolveExtendDbDir(const std::wstring& coreDir)
{
    std::wstring rel = g_pluginPath.empty()
        ? std::wstring(L"..\\Plugins\\ExtendDB\\ExtendDB.dll")
        : Widen(g_pluginPath);
    bool isAbsolute =
        (rel.size() >= 2 && rel[1] == L':') ||
        (rel.size() >= 2 && rel[0] == L'\\' && rel[1] == L'\\');
    std::wstring full = isAbsolute ? rel : (coreDir + rel);
    auto slash = full.find_last_of(L"\\/");
    if (slash != std::wstring::npos) full.resize(slash + 1);
    return full;
}

// -------------------- safety interlock: write-guard plugin presence --------------------
//
// NO anti-tamper here (deliberate — nothing is ever aborted). Instead a SAFETY INTERLOCK:
// the read-filter is dangerous WITHOUT the managed write-guard plugin, because a filtered
// read + a save would permanently drop the hidden games. So we filter ONLY when that plugin
// is installed. When its DLL is absent we simply do NOT filter — the process sees the real,
// unfiltered library (harmless). Checked once at boot at the standard deploy path; and once
// the plugin has actually called set_filtering() its presence is proven regardless.
static bool WriteGuardPluginPresent(const std::wstring& coreDir)
{
    // LiteBox deploys the managed plugin here (WS6 install flow).
    const std::wstring full = coreDir + L"..\\Plugins\\litebox-parentalcontrol\\litebox-parentalcontrol.dll";
    DWORD attrs = GetFileAttributesW(full.c_str());
    bool present = (attrs != INVALID_FILE_ATTRIBUTES) && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
    Log(std::string("[Boot] write-guard plugin ") + (present ? "present" : "MISSING") + " at " + Narrow(full)
        + (present ? "" : " -> read-filter DISABLED (a filtered read without the write-guard risks data loss)."));
    return present;
}

static std::string MatchDepth1Open(const std::string& line)
{
    if (line.size() < 4) return {};
    if (line[0] != ' ' || line[1] != ' ') return {};
    if (line[2] != '<' || line[3] == '/' || line[3] == '?' || line[3] == '!') return {};
    size_t end = line.find_first_of("> /\t", 3);
    if (end == std::string::npos) return {};
    return line.substr(3, end - 3);
}

static bool IsDepth1Close(const std::string& line, const std::string& name)
{
    std::string close = "  </" + name + ">";
    return line.compare(0, close.size(), close) == 0;
}

// Recursion guard: while we read the original, our own CreateFileW must not be hijacked.
static thread_local bool t_suppress = false;

// Pass 1: read the original file once, collect IDs of games whose Rating starts with M.
// Returns the set of kept IDs (small: ~30 bytes per ID).
static std::shared_ptr<const std::unordered_set<std::string>>
ComputeKeptIds(const std::wstring& original)
{
    t_suppress = true;
    struct Guard { ~Guard() { t_suppress = false; } } guard;

    auto ids = std::make_shared<std::unordered_set<std::string>>();
    int totalGames = 0;

    std::ifstream in(original.c_str(), std::ios::binary);
    if (!in) { Log("[Filter] open-fail " + Narrow(original)); return ids; }

    std::string line, buffer, currentName;
    bool inElement = false;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!inElement) {
            auto name = MatchDepth1Open(line);
            if (name == "Game") {
                inElement = true;
                currentName = name;
                buffer.clear();
                buffer.append(line).append("\n");
            }
        } else {
            buffer.append(line).append("\n");
            if (IsDepth1Close(line, currentName)) {
                inElement = false;
                totalGames++;
                auto id = ExtractTagValue(buffer, "ID");
                auto rating = ExtractTagValue(buffer, "Rating");
                // Keep iff the rating passes the rules AND the game is not on the per-game blocked list.
                bool blocked = !id.empty() && g_blockedIds.find(id) != g_blockedIds.end();
                if (IsRatingAllowed(rating) && !blocked) {
                    if (!id.empty()) ids->insert(id);
                }
            }
        }
    }

    char msg[512];
    snprintf(msg, sizeof(msg),
             "[Filter] %ls: scanned %d games, %zu kept (passing parental rules)",
             PathFindFileNameW(original.c_str()), totalGames, ids->size());
    Log(msg);
    return ids;
}

// -------------------- in-memory cache (ids only) --------------------

static std::unordered_map<std::wstring,
                          std::shared_ptr<const std::unordered_set<std::string>>> g_cache;
static std::mutex g_cacheMutex;

static std::shared_ptr<const std::unordered_set<std::string>>
GetOrComputeKeptIds(const std::wstring& original)
{
    {
        std::lock_guard<std::mutex> lock(g_cacheMutex);
        auto it = g_cache.find(original);
        if (it != g_cache.end()) return it->second;
    }
    auto computed = ComputeKeptIds(original);
    {
        std::lock_guard<std::mutex> lock(g_cacheMutex);
        g_cache[original] = computed;
        return computed;
    }
}

// -------------------- pipe-backed redirect --------------------

// Stream pass 2 directly into the pipe: re-read the original, write only kept rows.
// Memory cost per active pipe: ~few KB (current line + current element buffer),
// independent of the original file size or the kept-game count.
static void StreamFilteredToPipe(HANDLE writeEnd, const std::wstring& original,
                                 std::shared_ptr<const std::unordered_set<std::string>> keptIds)
{
    t_suppress = true;
    struct Guard { ~Guard() { t_suppress = false; } } guard;

    std::ifstream in(original.c_str(), std::ios::binary);
    if (!in) { CloseHandle(writeEnd); return; }

    auto writeStr = [&](const std::string& s) -> bool {
        const char* p = s.data();
        size_t remaining = s.size();
        DWORD written = 0;
        while (remaining > 0) {
            DWORD chunk = (DWORD)std::min<size_t>(remaining, 64 * 1024);
            if (!WriteFile(writeEnd, p, chunk, &written, nullptr) || written == 0) return false;
            p += written;
            remaining -= written;
        }
        return true;
    };

    std::string line, buffer, currentName, lineEnd("\n");
    bool inElement = false;
    while (std::getline(in, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!inElement) {
            auto name = MatchDepth1Open(line);
            if (!name.empty()) {
                inElement = true;
                currentName = name;
                buffer.clear();
                buffer.append(line).append("\n");
            } else {
                if (!writeStr(line) || !writeStr(lineEnd)) { CloseHandle(writeEnd); return; }
            }
        } else {
            buffer.append(line).append("\n");
            if (IsDepth1Close(line, currentName)) {
                inElement = false;
                bool keep = true;
                if (currentName == "Game") {
                    auto id = ExtractTagValue(buffer, "ID");
                    keep = !id.empty() && keptIds->count(id) > 0;
                } else {
                    auto gid = ExtractTagValue(buffer, "GameID");
                    if (gid.empty()) gid = ExtractTagValue(buffer, "GameId");
                    if (!gid.empty()) keep = keptIds->count(gid) > 0;
                }
                if (keep && !writeStr(buffer)) { CloseHandle(writeEnd); return; }
            }
        }
    }
    CloseHandle(writeEnd);
}

// Returns the read end of an anonymous pipe whose write end is fed by a detached
// thread that streams the filtered XML. Caller owns the read end.
static HANDLE OpenStreamingPipe(const std::wstring& original,
                                std::shared_ptr<const std::unordered_set<std::string>> keptIds)
{
    HANDLE readEnd = nullptr, writeEnd = nullptr;
    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = FALSE;
    if (!CreatePipe(&readEnd, &writeEnd, &sa, 64 * 1024)) return nullptr;

    std::thread([writeEnd, original, keptIds]() {
        StreamFilteredToPipe(writeEnd, original, keptIds);
    }).detach();

    return readEnd;
}

// -------------------- gating + runtime toggle --------------------
//
// Two distinct phases:
//
//  1. COLD START — before the managed ExtendDB plugin has spoken. extenddb.asi has to
//     guess whether to filter, so it requires ALL of:
//       * ExtendDBParental.dat exists                 (g_configPresent)
//       * it has BigBoxEnabled=1                      (g_bigBoxEnabled)
//       * BigBox has a non-empty <LockPin> configured (g_lockPinSet)
//       * the runtime toggle is on                    (g_filteringEnabled, default true)
//     The LockPin check is what makes this correct: g_filteringEnabled defaults to true
//     so pre-managed reads are filtered, but that is only right if BigBox actually boots
//     locked — which it does only when a LockPin is set.
//
//  2. MANAGED CONTROL — once extenddb_set_filtering() has been called at least once
//     (g_managedTookOver), the managed plugin is the SOLE authority. extenddb.asi then
//     obeys g_filteringEnabled ALONE and ignores its own cold-start gate: the managed
//     side already evaluated the BigBox parental-control option + the lock/unlock state
//     before calling, so re-checking g_configPresent / g_bigBoxEnabled / g_lockPinSet
//     here would only risk fighting a stale boot-time snapshot.
//
// The managed plugin drives this via extenddb_set_filtering(): while the "BigBox
// parental control" option is on, it enables filtering on BigBoxLocked and disables it
// on BigBoxUnlocked.

static bool          g_isBigBox          = false; // host process is BigBox.exe (else LaunchBox.exe)
static bool          g_writeGuardPresent = false; // the managed write-guard plugin dll is installed
static bool          g_lockPinSet       = false; // BigBoxSettings.xml has a non-empty <LockPin>
static volatile bool g_filteringEnabled = true;  // runtime toggle, driven by the managed plugin
static volatile bool g_managedTookOver  = false; // set on the first set_filtering() call

// -------------------- BigBoxSettings hardening --------------------
//
// When extenddb.asi decides we are running BigBox under parental control
// at cold start (ExtendDBParental.dat present with BigBoxEnabled=1 AND
// a non-empty LockPin), the user is going to land on a locked BigBox.
// LaunchBox writes BigBoxSettings.xml with each "<Allow...WhileLocked>"
// flag set to whatever the user picked there — most default to true,
// which lets the locked user bypass parental control by jumping
// filters / opening Discovery Center / Retroarch netplay browser /
// emulator folders / shutdown menu etc.
//
// extenddb.asi runs BEFORE BigBox starts the .NET runtime, so it can
// rewrite that XML on disk before BigBox reads it: every flag in
// kLockedFlags below is forced to "false". The file is left untouched
// when we are not in cold-start lock mode (no config / no LockPin /
// not BigBox) so the LB side stays in control of its own settings.
//
// Implementation is intentionally lo-fi: a substring find/replace on
// the raw bytes, no XML parser, no schema changes. Elements that are
// already "false" are skipped to keep the diff minimal.

static const char* kLockedFlags[] = {
    "AllowSettingStarRatingsWhileLocked",
    "AllowOpeningGameFoldersWhileLocked",
    "AllowOpeningGameImageFoldersWhileLocked",
    "AllowOpeningEmulatorsWhileLocked",
    "AllowFavoritingGamesWhileLocked",
    "AllowHidingGamesWhileLocked",
    "AllowMarkingGamesAsBrokenWhileLocked",
    "AllowModifyingProgressWhileLocked",
    "AllowSleepWhileLocked",
    "AllowShutDownWhileLocked",
    "AllowRebootWhileLocked",
    "AllowChangeViewWhileLocked",
    "AllowChangeImageTypeWhileLocked",
    "AllowNavigateToGameDiscoveryCenterWhileLocked",
    "AllowChangeFilterAllGamesWhileLocked",
    "AllowChangeFilterPlatformsWhileLocked",
    "AllowChangeFilterPlatformCategoriesWhileLocked",
    "AllowViewRetroarchNetplayBrowserWhileLocked",
    "AllowChangeFilterPlaylistsWhileLocked",
    "AllowChangeFilterGenresWhileLocked",
    "AllowChangeFilterDevelopersWhileLocked",
    "AllowChangeFilterPublishersWhileLocked",
    "AllowChangeFilterSeriesWhileLocked",
    "AllowChangeFilterStatusesWhileLocked",
    "AllowChangeFilterSourcesWhileLocked",
    "AllowChangeFilterRatingsWhileLocked",
    "AllowChangeFilterPlayModesWhileLocked",
    "AllowChangeFilterRegionsWhileLocked",
    "AllowThemesDemoWhileLocked",
    "AllowSearchWhileLocked",
};

// Forces every <Allow*WhileLocked> flag in BigBoxSettings.xml to "false"
// when we are in cold-start lock mode. No-op otherwise. Logs a one-line
// summary. Called once, before the CreateFileW hook is installed.
static void EnforceBigBoxLockRestrictions(const std::wstring& settingsPath)
{
    if (!g_configPresent)  { return; }
    if (!g_bigBoxEnabled)  { return; }
    if (!g_lockPinSet)     { return; }

    std::ifstream in(settingsPath.c_str(), std::ios::binary);
    if (!in)
    {
        Log("[Boot] BigBoxSettings.xml not found for lock-restriction patch: " + Narrow(settingsPath));
        return;
    }
    std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    in.close();

    int changed = 0, missing = 0;
    for (const char* name : kLockedFlags)
    {
        std::string openTag  = std::string("<")  + name + ">";
        std::string closeTag = std::string("</") + name + ">";
        size_t a = content.find(openTag);
        if (a == std::string::npos) { ++missing; continue; }
        a += openTag.size();
        size_t b = content.find(closeTag, a);
        if (b == std::string::npos) { ++missing; continue; }

        std::string current = content.substr(a, b - a);
        // Already false (case-insensitive) — skip to keep diffs minimal.
        if (current.size() == 5 &&
            (current[0]=='f'||current[0]=='F') &&
            (current[1]=='a'||current[1]=='A') &&
            (current[2]=='l'||current[2]=='L') &&
            (current[3]=='s'||current[3]=='S') &&
            (current[4]=='e'||current[4]=='E'))
            continue;

        content.replace(a, b - a, "false");
        ++changed;
    }

    if (changed == 0)
    {
        char msg[256];
        snprintf(msg, sizeof(msg),
                 "[Boot] BigBox lock restrictions already enforced (changed=0, missing=%d).", missing);
        Log(msg);
        return;
    }

    std::ofstream out(settingsPath.c_str(), std::ios::binary | std::ios::trunc);
    if (!out)
    {
        Log("[Boot] BigBoxSettings.xml: write open failed for lock-restriction patch.");
        return;
    }
    out.write(content.data(), (std::streamsize)content.size());
    out.close();

    char msg[256];
    snprintf(msg, sizeof(msg),
             "[Boot] BigBox lock restrictions: forced %d flag(s) to false (missing=%d).",
             changed, missing);
    Log(msg);
}

// Reads BigBoxSettings.xml and returns true if <LockPin> exists with non-empty content.
// Called from DllMain *before* the hook is installed, so a plain read is fine.
static bool ReadLockPinPresent(const std::wstring& settingsPath)
{
    std::ifstream in(settingsPath.c_str(), std::ios::binary);
    if (!in) { Log("[Boot] BigBoxSettings.xml not found: " + Narrow(settingsPath)); return false; }
    std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    auto a = content.find("<LockPin>");
    if (a == std::string::npos) return false;
    a += 9; // strlen("<LockPin>")
    auto b = content.find("</LockPin>", a);
    if (b == std::string::npos) return false;
    auto value = content.substr(a, b - a);
    // Trim whitespace.
    size_t s = value.find_first_not_of(" \t\r\n");
    size_t e = value.find_last_not_of(" \t\r\n");
    return (s != std::string::npos && e != std::string::npos);
}

// -------------------- Settings.xml: region priorities --------------------
//
// LaunchBox/BigBox pick which regional release (and thus which <Rating>) to
// show from <RegionPriorities> in LB\Data\Settings.xml — an ordered,
// comma-separated list like "North America,United States". extenddb.asi makes
// sure "World" is part of that list so World-region releases are considered
// too; it is appended at the end, with no space after the comma, only when
// missing. Unlike the BigBox lock hardening this runs UNCONDITIONALLY in
// both LaunchBox.exe and BigBox.exe at load (no parental-control gate).
//
// Lo-fi like EnforceBigBoxLockRestrictions: substring find of the one
// element, comma-split to test membership, single in-place rewrite. No-op
// when the element is absent or already lists World (any case).
static void EnforceRegionPrioritiesWorld(const std::wstring& settingsPath)
{
    std::ifstream in(settingsPath.c_str(), std::ios::binary);
    if (!in)
    {
        Log("[Boot] Settings.xml not found for RegionPriorities patch: " + Narrow(settingsPath));
        return;
    }
    std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    in.close();

    const std::string openTag  = "<RegionPriorities>";
    const std::string closeTag = "</RegionPriorities>";
    size_t a = content.find(openTag);
    if (a == std::string::npos) { Log("[Boot] Settings.xml: <RegionPriorities> not found, skipped."); return; }
    a += openTag.size();
    size_t b = content.find(closeTag, a);
    if (b == std::string::npos) { Log("[Boot] Settings.xml: </RegionPriorities> not found, skipped."); return; }

    std::string current = content.substr(a, b - a);

    // Already present as a whole comma-token (case-insensitive)? -> nothing to do.
    for (size_t start = 0; start <= current.size(); )
    {
        size_t comma = current.find(',', start);
        size_t len   = (comma == std::string::npos ? current.size() : comma) - start;
        if (ToLowerCopy(TrimCopy(current.substr(start, len))) == "world")
        {
            Log("[Boot] Settings.xml: RegionPriorities already contains World, unchanged.");
            return;
        }
        if (comma == std::string::npos) break;
        start = comma + 1;
    }

    // Append "World" right after the last non-whitespace char (no space after
    // the comma), preserving any trailing whitespace already inside the tag.
    size_t lastNonWs = current.find_last_not_of(" \t\r\n");
    std::string replacement = (lastNonWs == std::string::npos)
        ? "World"
        : current.substr(0, lastNonWs + 1) + ",World" + current.substr(lastNonWs + 1);
    content.replace(a, b - a, replacement);

    std::ofstream out(settingsPath.c_str(), std::ios::binary | std::ios::trunc);
    if (!out)
    {
        Log("[Boot] Settings.xml: write open failed for RegionPriorities patch.");
        return;
    }
    out.write(content.data(), (std::streamsize)content.size());
    out.close();

    Log("[Boot] Settings.xml: appended World to RegionPriorities (\""
        + TrimCopy(current) + "\" -> \"" + TrimCopy(replacement) + "\").");
}

static bool FilteringActive()
{
    // Once the managed plugin has taken control, it is the sole authority — and its very call proves
    // the write-guard is here, so we obey it (the boot-time file check is moot at that point).
    if (g_managedTookOver)
        return g_filteringEnabled;

    // SAFETY INTERLOCK: never filter unmanaged unless the write-guard plugin is installed. A filtered
    // read without the write-guard could let a save persist the filtered subset → data loss.
    if (!g_writeGuardPresent) return false;

    // Cold-start gate (before the managed plugin has spoken), PER PROCESS:
    //   • BigBox boots locked only when a LockPin is set → require it there;
    //   • LaunchBox has no such notion → our config's LaunchBoxEnabled alone decides.
    if (!g_configPresent || !g_filteringEnabled) return false;
    if (g_isBigBox) return g_bigBoxEnabled && g_lockPinSet;
    return g_launchBoxEnabled;
}

// Exported control channel: the managed plugin calls this to enable/disable filtering.
extern "C" __declspec(dllexport) void __stdcall litebox_parental_set_filtering(int enabled)
{
    g_filteringEnabled = (enabled != 0);
    g_managedTookOver = true; // from now on the managed DLL is the sole authority
    Log(std::string("[Control] litebox_parental_set_filtering -> ")
        + (g_filteringEnabled ? "ENABLED" : "disabled") + " (managed control)");
}

// -------------------- CreateFileW hook (reads only) --------------------

using CreateFileW_t = HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
static CreateFileW_t g_orig_CreateFileW = nullptr;

static HANDLE WINAPI Hook_CreateFileW(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
                                      LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition,
                                      DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
{
    if (!t_suppress && lpFileName != nullptr) {
        std::wstring path(lpFileName);
        bool isPlatformXml = ContainsCI(path, L"\\Data\\Platforms\\") && EndsWithCI(path, L".xml");
        bool isWrite = (dwDesiredAccess & GENERIC_WRITE) != 0;
        if (isPlatformXml && !isWrite && FilteringActive()) {
            auto keptIds = GetOrComputeKeptIds(path);
            if (keptIds) {
                HANDLE pipe = OpenStreamingPipe(path, keptIds);
                if (pipe != nullptr) {
                    Log("[Redirect] " + Narrow(path) + " -> <pipe streaming, " +
                        std::to_string(keptIds->size()) + " ids>");
                    return pipe;
                }
            }
        }
    }
    return g_orig_CreateFileW(lpFileName, dwDesiredAccess, dwShareMode,
                              lpSecurityAttributes, dwCreationDisposition,
                              dwFlagsAndAttributes, hTemplateFile);
}

// -------------------- exported bridge --------------------
//
// Opens the *real* file on disk, bypassing our own CreateFileW hook by calling the
// MinHook trampoline directly. The managed plugin uses this to read the unfiltered
// platform XML when merging BigBox's saved subset back into the full library.

extern "C" __declspec(dllexport) HANDLE __stdcall litebox_parental_open_real_file(LPCWSTR path)
{
    if (path == nullptr || g_orig_CreateFileW == nullptr) return INVALID_HANDLE_VALUE;
    return g_orig_CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
}

// -------------------- DllMain --------------------

static std::wstring HostExeName()
{
    wchar_t exePath[MAX_PATH] = {};
    if (GetModuleFileNameW(nullptr, exePath, MAX_PATH) == 0) return L"";
    return PathFindFileNameW(exePath);
}

static bool RunningInBigBox()
{
    return _wcsicmp(HostExeName().c_str(), L"BigBox.exe") == 0;
}

// The read-filter must ONLY touch the third-party apps. winhttp.dll sits in Core, so ANY Core
// process that loads it (LiteBox.exe itself — which has its own native parental filtering and
// writes Data\ legitimately — a helper, an updater…) drags this ASI in. Attach inert unless the
// host is exactly LaunchBox.exe or BigBox.exe.
static bool RunningInLaunchBoxOrBigBox()
{
    auto n = HostExeName();
    return _wcsicmp(n.c_str(), L"LaunchBox.exe") == 0 || _wcsicmp(n.c_str(), L"BigBox.exe") == 0;
}

static void InitLogPath()
{
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    std::wstring dir(exePath);
    auto slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos) dir.resize(slash + 1);
    g_logPath = dir + L"litebox-parental.log";
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(hModule);

    InitLogPath();

    // Resolve this .asi's own full path + its folder (LB\Core), and load the
    // parental config. Done in BOTH processes — the anti-tamper check below
    // must run everywhere, not just in BigBox (the LaunchBox-side filtering
    // lives in the same plugin).
    std::wstring modPath, coreDir;
    {
        wchar_t buf[MAX_PATH] = {};
        GetModuleFileNameW(hModule, buf, MAX_PATH);
        modPath = buf;
        coreDir = modPath;
        auto slash = coreDir.find_last_of(L"\\/");
        if (slash != std::wstring::npos) coreDir.resize(slash + 1);
    }
    Log("[Boot] litebox-parental.asi " LITEBOX_PARENTAL_ASI_VERSION " (built " __DATE__ " " __TIME__ ") — module: "
        + Narrow(modPath));

    LoadParentalConfig(coreDir + L"litebox-parental.dat");

    // Logging turns on once we know parental is configured (litebox-parental.dat present) — a stock
    // install with no parental config stays silent. The boot banner above is dropped on purpose
    // (restart with a config in place to capture it).
    g_debugLog = g_configPresent;

    // Host-process guard: never touch anything but the two third-party apps. LiteBox.exe (its own
    // native parental filtering + legitimate Data\ writes) and any other Core process that loads
    // winhttp.dll attach inert here — no config read acted on, no hook, no Settings.xml touch.
    if (!RunningInLaunchBoxOrBigBox())
    {
        Log("[Boot] host is not LaunchBox.exe / BigBox.exe (" + Narrow(HostExeName()) + ") — attaching inert.");
        return TRUE;
    }

    g_isBigBox = RunningInBigBox();

    // Safety interlock (NOT anti-tamper): the read-filter only runs when the managed write-guard plugin
    // is installed — without it a filtered read + a save would drop the hidden games. Missing → no filter.
    g_writeGuardPresent = WriteGuardPluginPresent(coreDir);

    // Attach inert unless parental is configured for THIS process.
    bool scopeOn = g_isBigBox ? g_bigBoxEnabled : g_launchBoxEnabled;
    if (!g_configPresent || !scopeOn)
    {
        Log(std::string("[Boot] attached to ") + (g_isBigBox ? "BigBox.exe" : "LaunchBox.exe")
            + " — parental not configured for this process, filter inactive.");
        return TRUE;
    }

    // BigBox-only cold-start hardening: BigBox boots locked only when a LockPin is set, so read it and,
    // while still pre-.NET, force every <Allow*WhileLocked> flag false on disk (Discovery Center, filter
    // changes, Retroarch netplay browser, shutdown, …). LaunchBox has no such settings.
    if (g_isBigBox)
    {
        const std::wstring bbSettingsPath = coreDir + L"..\\Data\\BigBoxSettings.xml";
        g_lockPinSet = ReadLockPinPresent(bbSettingsPath);
        Log(std::string("[Boot] LockPin configured = ") + (g_lockPinSet ? "true" : "false"));
        EnforceBigBoxLockRestrictions(bbSettingsPath);
    }

    // Install the read-filter hook in BOTH LaunchBox.exe and BigBox.exe. FilteringActive gates each read
    // per process; the managed plugin drives the runtime on/off via litebox_parental_set_filtering().
    if (MH_Initialize() != MH_OK) { Log("[Boot] MH_Initialize failed"); return TRUE; }

    auto installHook = [](LPVOID target, LPVOID detour, LPVOID* trampoline, const char* name) -> bool {
        if (MH_CreateHook(target, detour, trampoline) != MH_OK) { Log(std::string("[Boot] MH_CreateHook(") + name + ") failed"); return false; }
        if (MH_EnableHook(target) != MH_OK) { Log(std::string("[Boot] MH_EnableHook(") + name + ") failed"); return false; }
        Log(std::string("[Boot] ") + name + " hook installed");
        return true;
    };

    installHook((LPVOID)&CreateFileW, (LPVOID)&Hook_CreateFileW, (LPVOID*)&g_orig_CreateFileW, "CreateFileW");
    Log(std::string("[Boot] litebox-parental attached to ") + (g_isBigBox ? "BigBox.exe" : "LaunchBox.exe")
        + " — read-filter armed.");
    return TRUE;
}
