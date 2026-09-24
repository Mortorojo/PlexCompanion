// PlexCompanion v1.0.1 — single-file C# (.NET Framework 4) app for Plex for Windows.
// One exe, one process: setup wizard, background watcher, and uninstaller all live here.
//
// Build: double-click build.bat (or run build.ps1) — see README for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------------------------------------------------------------------
// P/Invoke — the Windows API functions the app uses
// ---------------------------------------------------------------------------
static class P
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLong(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, uint attr, ref int value, int size);

    // shared rounded-rectangle path (was duplicated in RBtn, HotKeyBox, SetupWindow)
    public static GraphicsPath RndRect(RectangleF b, int r)
    {
        GraphicsPath p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(b.Left, b.Top, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Top, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ---------------------------------------------------------------------------
// Human-readable hotkey text (was duplicated 3x; the App copy garbled F-keys)
// ---------------------------------------------------------------------------
static class HotText
{
    public static string Text(int mods, int vk)
    {
        string m = "";
        if ((mods & 0x0002) != 0) m += "Ctrl + ";
        if ((mods & 0x0001) != 0) m += "Alt + ";
        if ((mods & 0x0004) != 0) m += "Shift + ";
        string k;
        if (vk >= 0x30 && vk <= 0x39) k = (vk - 0x30).ToString();
        else if (vk >= 0x41 && vk <= 0x5A) k = ((char)vk).ToString();
        else if (vk >= 0x60 && vk <= 0x69) k = "Numpad" + (vk - 0x60);
        else if (vk >= 0x70 && vk <= 0x87) k = "F" + (vk - 0x60);
        else if (vk == 0x20) k = "Space";
        else k = "0x" + vk.ToString("X");
        return m + k;
    }
}

// ---------------------------------------------------------------------------
// Logger (append to plexcompanion.log next to the exe)
// ---------------------------------------------------------------------------
static class Log
{
    static string _path;
    public static void Init(string root)
    {
        // prefer logging next to the exe; if that folder isn't writable
        // (e.g. Program Files), fall back to the per-user AppData folder
        try
        {
            string probe = System.IO.Path.Combine(root, ".pcwrite");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            _path = System.IO.Path.Combine(root, "plexcompanion.log");
        }
        catch
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Directory.CreateDirectory(System.IO.Path.Combine(lad, "PlexCompanion"));
            _path = System.IO.Path.Combine(lad, "PlexCompanion", "plexcompanion.log");
        }
    }
    public static void Write(string msg)
    {
        try
        {
            string ts = DateTime.Now.ToString("yyyyMMddHHmmss");
            File.AppendAllText(_path, ts + "  " + msg + "\r\n");
        }
        catch { }
    }
}

// ---------------------------------------------------------------------------
// Registry config (HKCU\Software\PlexCompanion + Apps & Features + Run key)
// ---------------------------------------------------------------------------
static class Cfg
{
    const string APP = @"Software\PlexCompanion";
    const string UN  = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PlexCompanion";
    const string RUN = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string VERSION = "1.0.1";   // single source of truth — UI + Apps & Features
    const string REPO = "Mortorojo/PlexCompanion";

    // ---- update check (watcher startup only) ----
    // Compare semver-ish versions; missing/extra segments count as 0.
    public static bool NewerThan(string newer, string current)
    {
        int[] a = ParseVer(newer), b = ParseVer(current);
        for (int i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }
    static int[] ParseVer(string v)
    {
        int[] r = { 0, 0, 0 };
        if (string.IsNullOrEmpty(v)) return r;
        v = v.Trim().TrimStart('v');
        string[] parts = v.Split('.');
        for (int i = 0; i < 3 && i < parts.Length; i++)
        {
            int x;
            if (int.TryParse(parts[i], out x)) r[i] = x;
        }
        return r;
    }
    // "v1.0.1" -> "1.0.1"
    public static string StripTag(string tag)
    {
        return (tag ?? "").Trim().TrimStart('v');
    }
    public class UpdateInfo { public string Tag; public string AssetUrl; }
    // Fetch the latest release (tag + first asset download URL), or null
    // (offline / repo gone / no release). ~5s hard cap so startup never hangs.
    public static UpdateInfo LatestRelease()
    {
        try
        {
            UseTls12();
            var req = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(
                "https://api.github.com/repos/" + REPO + "/releases/latest");
            req.KeepAlive = false;
            req.Timeout = 5000;
            req.UserAgent = "PlexCompanion/" + VERSION;
            using (var resp = req.GetResponse())
            using (var sr = new System.IO.StreamReader(resp.GetResponseStream()))
            {
                string body = sr.ReadToEnd();
                var u = new UpdateInfo();
                u.Tag = ExtractStr(body, "tag_name");
                u.AssetUrl = ExtractStr(body, "browser_download_url");
                return u.Tag != null ? u : null;
            }
        }
        catch { return null; }
    }
    // .NET 4.0 defaults to TLS 1.0/1.1, which modern Windows disables — GitHub
    // (and github.com asset downloads) need TLS 1.2, so force it on.
    public static void UseTls12()
    {
        try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; }
        catch { }
    }

    // Pull a string field from the release JSON without a JSON library.
    static string ExtractStr(string body, string key)
    {
        int i = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i = body.IndexOf(":", i);
        if (i < 0) return null;
        i = body.IndexOf('"', i + 1);
        if (i < 0) return null;
        int j = body.IndexOf('"', i + 1);
        if (j < 0) return null;
        return body.Substring(i + 1, j - i - 1);
    }

    public static string GetPlexPath()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(APP, false))
            return k == null ? null : k.GetValue("PlexPath") as string;
    }
    public static void SetPlexPath(string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(APP)) k.SetValue("PlexPath", value);
    }
    public static string GetAppearance()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(APP, false))
            return k == null ? null : k.GetValue("Appearance") as string;
    }
    public static void SetAppearance(string value)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(APP)) k.SetValue("Appearance", value);
    }
    public static bool HasConfig()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(APP, false))
            return k != null && k.GetValue("PlexPath") != null;
    }
    public static bool HasApp()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(UN, false))
            return k != null && k.GetValue("DisplayName") != null;
    }
    public static int GetHotMods()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(APP, false))
            return k == null ? 1 : (int)(k.GetValue("HotMods") ?? 1);
    }
    public static int GetHotVk()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(APP, false))
            return k == null ? 0x51 : (int)(k.GetValue("HotVk") ?? 0x51);
    }
    public static void SetHotKey(int mods, int vk)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(APP))
        {
            k.SetValue("HotMods", mods, RegistryValueKind.DWord);
            k.SetValue("HotVk", vk, RegistryValueKind.DWord);
        }
    }
    public static void SetAutoUpdate(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(APP))
            k.SetValue("AutoUpdate", on ? 1 : 0, RegistryValueKind.DWord);
    }
    public static bool GetAutoUpdate()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(APP, false))
            return k == null || k.GetValue("AutoUpdate") == null ? true : (int)(int)k.GetValue("AutoUpdate") != 0;
    }

    public static void RegisterApp(string exe, string root)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(UN))
        {
            k.SetValue("DisplayName", "PlexCompanion");
            k.SetValue("DisplayIcon", "\"" + exe + "\"");
            k.SetValue("DisplayVersion", Cfg.VERSION);
            k.SetValue("Publisher", "Mort");
            k.SetValue("InstallLocation", root);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("UninstallString", "\"" + exe + "\" /remove");
            k.SetValue("QuietUninstallString", "\"" + exe + "\" /remove");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    public static void AddStartup(string exe)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RUN))
            k.SetValue("PlexCompanionWatcher", "\"" + exe + "\"");
    }
    public static void RemoveStartup()
    {
        var k = Registry.CurrentUser.OpenSubKey(RUN, true);
        if (k != null) { k.DeleteValue("PlexCompanionWatcher", false); k.Close(); }
    }

    public static void RemoveApp()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(APP, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UN, false); } catch { }
    }
}

// ---------------------------------------------------------------------------
// Shortcuts (WScript.Shell COM)
// ---------------------------------------------------------------------------
static class Shortcuts
{
    public static List<string> Remove()
    {
        var gone = new List<string>();
        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PlexCompanion.lnk");
        string sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PlexCompanion.lnk");
        if (File.Exists(desk)) { File.Delete(desk); gone.Add(desk); }
        if (File.Exists(sm)) { File.Delete(sm); gone.Add(sm); }
        return gone;
    }
}

// ---------------------------------------------------------------------------
// Kill stale instances (other PlexCompanion*.exe copies)
// ---------------------------------------------------------------------------
static class Stale
{
    public static bool IsPlexCompanion(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
                return p.ProcessName.IndexOf("PlexCompanion", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    public static void Kill(params int[] excludePids)
    {
        var exclude = new HashSet<int>();
        if (excludePids != null) foreach (var x in excludePids) exclude.Add(x);
        int me = Process.GetCurrentProcess().Id;
        // kill other PlexCompanion*.exe instances (installed copy, source, strays)
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == me || exclude.Contains(p.Id)) continue;
            string n = p.ProcessName;
            if (n.IndexOf("PlexCompanion", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { p.Kill(); Log.Write("killed stale " + n + " " + p.Id); } catch { }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Hidden message-loop form (owns the hotkey, receives WM_HOTKEY)
// ---------------------------------------------------------------------------
class HiddenForm : Form
{
    public event Action<int> HotKey;
    const int WM_HOTKEY = 0x0312;

    public HiddenForm()
    {
        this.ShowInTaskbar = false;
        this.FormBorderStyle = FormBorderStyle.None;
        this.Size = new Size(1, 1);
        this.Location = new Point(-20, -20);
        this.Opacity = 0;
    }

    // WS_EX_TOOLWINDOW: keeps this message-loop window out of the taskbar,
    // the Alt+Tab list, and Task Manager's Applications tab (otherwise it
    // appears there as a nameless entry with the default icon).
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && HotKey != null)
            HotKey((int)m.WParam);
        base.WndProc(ref m);
    }
}

// ---------------------------------------------------------------------------
// The watcher / app
// ---------------------------------------------------------------------------
class App
{
    const int HOTID = 1;
    uint _hotMods = 0x0001, _hotVk = 0x51;   // default Alt+Q, overridden from registry

    readonly string _appearance;
    readonly string _root;

    bool _attached = false;
    bool _frameHidden = false;
    bool _darkApplied = false;
    int[] _origRect = null;

    HiddenForm _form;
    Timer _poll;

    public App(string appearance, string root)
    {
        _appearance = appearance;
        _root = root;
    }

    public void Run()
    {
        Log.Write("app starting (appearance=" + _appearance + ")");
        // NOTE: Stale.Kill() is called by Main (only for the installed copy),
        // NOT here — killing here would kill the source process that launched us.

        _form = new HiddenForm();
        _hotMods = (uint)Cfg.GetHotMods();
        _hotVk = (uint)Cfg.GetHotVk();
        bool ok = P.RegisterHotKey(_form.Handle, HOTID, _hotMods, _hotVk);
        if (!ok)
        {
            Log.Write("WARN another app owns this hotkey (" + HotName() + ") - exiting (a watcher is already running)");
            return;
        }
        Log.Write(HotName() + " hotkey registered (held for the app's whole lifetime)");

        _form.HotKey += OnHotKey;
        LoadState();

        _poll = new Timer { Interval = 150 };
        _poll.Tick += OnPoll;
        _poll.Start();

        _form.Show();
        Application.Run(_form);
    }

    string HotName()
    {
        return HotText.Text((int)_hotMods, (int)_hotVk);
    }

    void OnPoll(object s, EventArgs e)
    {
        Process plex = FindPlex();
        if (plex != null && !_attached)
        {
            Log.Write("Plex detected PID=" + plex.Id + " - attaching");
            if (_appearance == "dark") ApplyDarkOnce();
            _attached = true;
            _frameHidden = false;
        }
        else if (plex == null && _attached)
        {
            Log.Write("Plex exited - going back to wait (stays armed)");
            _attached = false;
            _darkApplied = false;
        }
    }

    void OnHotKey(int id)
    {
        if (id != HOTID || !_attached) return;
        Process plex = FindPlex();
        if (plex == null) { Log.Write(HotName() + " pressed but Plex not found - ignored"); return; }
        IntPtr fg = P.GetForegroundWindow();
        uint fpid; P.GetWindowThreadProcessId(fg, out fpid);
        if (fpid != (uint)plex.Id) { Log.Write(HotName() + " pressed but Plex not in focus - ignored"); return; }

        IntPtr h = plex.MainWindowHandle;
        if (_frameHidden)
        {
            RestoreFrame(h);
            _frameHidden = false;
            if (_appearance == "dark") ApplyDarkOnce();
        }
        else
        {
            HideFrame(h);
            _frameHidden = true;
        }
    }

    public static Process FindPlex()
    {
        foreach (var p in Process.GetProcessesByName("Plex"))
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) return p; } catch { }
        }
        return null;
    }

    void ApplyDarkOnce()
    {
        if (_darkApplied) return;
        Process plex = FindPlex();
        if (plex == null) return;
        int v = 1;
        for (int attr = 20; attr >= 19; attr--)
        {
            if (P.DwmSetWindowAttribute(plex.MainWindowHandle, (uint)attr, ref v, 4) == 0)
            {
                Log.Write("dark title bar: applied (attr " + attr + ")");
                _darkApplied = true;
                return;
            }
        }
        Log.Write("dark title bar: FAILED (dwmapi non-zero for both attributes)");
    }

    void HideFrame(IntPtr h)
    {
        P.RECT wr, cr; P.POINT pt = new P.POINT();
        P.GetWindowRect(h, out wr);
        P.GetClientRect(h, out cr);
        P.ClientToScreen(h, ref pt);

        long style = (long)P.GetWindowLong(h, -16);
        long m = 0x00C00000 | 0x00040000 | 0x00800000;
        P.SetWindowLong(h, -16, (IntPtr)(style & ~m));
        P.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0016);

        int captionH = pt.Y - wr.T;
        int newH = cr.B + captionH;
        P.MoveWindow(h, pt.X, wr.T, cr.R, newH, false);

        // save the TRUE window rect (not client-derived) so restore is lossless —
        // client width here used to shave ~16px off every hide/restore cycle
        _origRect = new[] { wr.L, wr.T, wr.R - wr.L, wr.B - wr.T };
        SaveState();
        Log.Write(HotName() + " -> HIDE vis=" + cr.R + "x" + newH + " caption=" + captionH + " orig=" + (wr.R - wr.L) + "x" + (wr.B - wr.T));
    }

    void RestoreFrame(IntPtr h)
    {
        int[] o = _origRect ?? new[] { 0, 0, 1280, 720 };
        long style = (long)P.GetWindowLong(h, -16);
        long m = 0x00C00000 | 0x00040000 | 0x00800000;
        P.SetWindowLong(h, -16, (IntPtr)(style | m));
        P.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0016);
        P.MoveWindow(h, o[0], o[1], o[2], o[3], false);
        Log.Write(HotName() + " -> RESTORE win=" + o[2] + "x" + o[3] + "@" + o[0] + "," + o[1]);
    }

    void SaveState()
    {
        if (_origRect == null) return;
        try
        {
            string path = Path.Combine(_root, "frame_state.json");
            File.WriteAllText(path,
                "{\"origX\":" + _origRect[0] + ",\"origY\":" + _origRect[1] +
                ",\"origW\":" + _origRect[2] + ",\"origH\":" + _origRect[3] + "}");
        }
        catch { }
    }

    void LoadState()
    {
        try
        {
            string path = Path.Combine(_root, "frame_state.json");
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                int x = ParseInt(json, "origX");
                int y = ParseInt(json, "origY");
                int w = ParseInt(json, "origW");
                int h = ParseInt(json, "origH");
                if (w > 0 && h > 0) _origRect = new[] { x, y, w, h };
            }
        }
        catch { }
    }

    static int ParseInt(string json, string key)
    {
        string k = "\"" + key + "\":";
        int i = json.IndexOf(k, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return 0;
        i += k.Length;
        int j = i;
        while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
        if (j == i) return 0;
        return int.Parse(json.Substring(i, j - i));
    }
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------
static class Program
{
    [System.STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                string log = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PlexCompanion", "plexcompanion.log");
                System.IO.File.AppendAllText(log, "UNHANDLED: " + e.ExceptionObject + "\r\n");
            }
            catch { }
        };
        string root = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string install = Path.Combine(lad, "PlexCompanion");
        bool amInstalled = string.Equals(Path.GetFullPath(root), Path.GetFullPath(install),
            StringComparison.OrdinalIgnoreCase);
        bool hasRegistry = Cfg.HasConfig() || Cfg.HasApp();

        // ---- self-install handoff (source fires the installed copy, then exits) ----
        // only a true "source" run (no install state anywhere) does this — a copy
        // already installed to a custom location must NOT bounce back to AppData
        if (!amInstalled && !hasRegistry)
        {
            Directory.CreateDirectory(install);
            string target = Path.Combine(install, "PlexCompanion.exe");
            File.Copy(Process.GetCurrentProcess().MainModule.FileName, target, true);

            var fwd = new List<string>();
            for (int i = 0; i < args.Length; i++)
                fwd.Add("\"" + args[i].Replace("\"", "\\\"") + "\"");
            var psi = new ProcessStartInfo(target, string.Join(" ", fwd));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            // The source never holds the Alt+Q hotkey (only the installed copy does),
            // so it can exit immediately — leaving exactly one process (the watcher).
            return 0;
        }

        string exe = Process.GetCurrentProcess().MainModule.FileName;
        Log.Init(root);

        // ---- dispatch ----
        if (args.Any(a => string.Equals(a, "/remove", StringComparison.OrdinalIgnoreCase)))
            return RunRemove(root, exe);
        if (args.Any(a => string.Equals(a, "/update", StringComparison.OrdinalIgnoreCase)))
            return RunUpdateCheck(root, exe, true) ? 0 : 1;
        bool forceSetup = args.Any(a => string.Equals(a, "/setup", StringComparison.OrdinalIgnoreCase));
        Log.Write("==== starting (installed=" + amInstalled + ") ====");

        // If a parent PlexCompanion process launched us, this is the installed
        // (canonical) copy and the parent is the source launcher — never kill it.
        int parentPid = 0;
        try
        {
            int pp = Process.GetCurrentProcess().Id;
            var s = new ManagementObjectSearcher(
                "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pp);
            foreach (ManagementObject mo in s.Get())
            {
                int ppid = (int)mo["ParentProcessId"];
                if (Stale.IsPlexCompanion(ppid)) parentPid = ppid;
                break;
            }
        }
        catch { }

        if (amInstalled)
        {
            if (parentPid != 0)
                Stale.Kill(parentPid);   // exclude the source launcher
            else
                Stale.Kill();            // launched directly (e.g. startup key) — kill all others
        }

        string appearance = Cfg.GetAppearance() ?? "dark";
        string plexPath = Cfg.GetPlexPath();
        bool needSetup = forceSetup || string.IsNullOrEmpty(plexPath) || !File.Exists(plexPath);

        if (needSetup)
        {
            Log.Write(forceSetup ? "setup forced via /setup" : "first run (or config invalid) - setup wizard");
            string defPlex = string.IsNullOrEmpty(plexPath)
                ? @"C:\Program Files\Plex\Plex\Plex.exe" : plexPath;

            bool firstRun = !Cfg.HasConfig();
            int defMods = Cfg.GetHotMods(), defVk = Cfg.GetHotVk();
            var w = new SetupWindow(defPlex, appearance, root, firstRun, defMods, defVk);
            if (w.ShowDialog() != DialogResult.OK)
            {
                Log.Write("setup: cancelled - nothing installed");
                return 1;
            }

            string path = w.PlexPathValue;
            if (string.IsNullOrEmpty(path)) path = defPlex;
            if (!File.Exists(path))
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "Plex.exe")))
                    path = Path.Combine(path, "Plex.exe");
                else
                {
                    MessageBox.Show(
                        "Plex.exe not found at:\n\n  " + path + "\n\nRe-run setup (launch with /setup) and pick the correct location.",
                        "PlexCompanion \u2014 Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Log.Write("setup: path not found: " + path);
                    return 1;
                }
            }

            Cfg.SetPlexPath(path);
            Cfg.SetAppearance(w.AppearanceValue);
            Cfg.SetHotKey(w.HotMods, w.HotVk);
            Cfg.SetAutoUpdate(w.CheckForUpdates);

            // ---- optional relocate (page 2 install location) ----
            if (!string.IsNullOrEmpty(w.InstallLocation))
            {
                string newDir = Path.GetFullPath(w.InstallLocation.Trim());
                string curRoot = Path.GetFullPath(root);
                if (!string.Equals(newDir, curRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string newExe = Path.Combine(newDir, "PlexCompanion.exe");
                    bool ok = InstallTo(newDir, exe);
                    if (ok)
                    {
                        Cfg.RegisterApp(newExe, newDir);
                        Cfg.AddStartup(newExe);
                        Log.Write("setup: relocated install -> " + newDir);
                        if (w.LaunchPlex)
                        {
                            try { Process.Start(path); Log.Write("launched Plex (pre-relocate)"); }
                            catch (Exception ex2) { Log.Write("launch Plex failed: " + ex2.Message); }
                        }
                        var psi = new ProcessStartInfo(newExe);
                        psi.UseShellExecute = false; psi.CreateNoWindow = true;
                        Process.Start(psi);
                        // drop the old default-location folder if we're leaving it
                        string defaultDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlexCompanion");
                        if (string.Equals(curRoot, defaultDir, StringComparison.OrdinalIgnoreCase))
                            ScheduleFolderDelete(curRoot);
                        Log.Write("setup: complete (relocated) - exiting");
                        return 0;
                    }
                    MessageBox.Show(
                        "Couldn't install to:\n\n  " + newDir + "\n\nPick another folder, or allow the elevation prompt if one was shown.",
                        "PlexCompanion \u2014 Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            Cfg.RegisterApp(exe, root);
            Log.Write("setup: saved registry config  Plex=" + path + "  appearance=" + w.AppearanceValue);

            var okList = new List<string>();
            Cfg.AddStartup(exe);
            okList.Add("startup entry (background watcher)");
            foreach (var o in okList) Log.Write("setup: created " + o);
            Log.Write("setup: registered in Apps & Features");

            appearance = w.AppearanceValue;
            plexPath = path;

            // launch Plex (first run only, if the user asked)
            if (!w.LaunchPlex)
            {
                Log.Write("launch Plex skipped (not requested)");
            }
            else
            try
            {
                var pp = Process.Start(plexPath);
                Log.Write("launched Plex PID=" + pp.Id);
                System.Threading.Thread.Sleep(800);
                // apply dark title bar once
                if (appearance == "dark")
                {
                    bool applied = false;
                    for (int i = 0; i < 40 && !applied; i++)
                    {
                        var pl = App.FindPlex();
                        if (pl != null)
                        {
                            int v = 1;
                            for (int attr = 20; attr >= 19; attr--)
                            {
                                if (P.DwmSetWindowAttribute(pl.MainWindowHandle, (uint)attr, ref v, 4) == 0)
                                {
                                    Log.Write("dark title bar: applied (attr " + attr + ")");
                                    applied = true;
                                    break;
                                }
                            }
                        }
                        if (!applied) System.Threading.Thread.Sleep(250);
                    }
                    if (!applied) Log.Write("dark title bar: window never appeared, skipped");
                }
            }
            catch (Exception ex)
            {
                Log.Write("launch Plex failed: " + ex.Message);
            }

            Log.Write("setup: complete - entering watcher loop (stays armed)");
        }

        // ---- update check (once per watcher start; silent when offline / up to date) ----
        if (Cfg.GetAutoUpdate() && RunUpdateCheck(root, exe, false))
            return 0;   // update swap is running — exit so it can replace the exe

        // ---- enter the watcher loop ----
        var app = new App(appearance, root);
        app.Run();
        return 0;
    }

    // Check GitHub for a newer release. force=false (watcher startup): only shows
    // a dialog when a newer version exists. force=true (/update, for testing):
    // always shows a dialog (update offer or "up to date").
    // Returns true when the update swap has been scheduled — the caller must
    // then EXIT (not enter the watcher loop) so it releases the exe's image
    // lock before the background copy overwrites it.
    static bool RunUpdateCheck(string root, string exe, bool force)
    {
        var info = Cfg.LatestRelease();
        if (info == null || info.Tag == null)
        {
            if (force)
                MessageBox.Show("Couldn't reach the update server (offline, or no release published).",
                    "PlexCompanion — Update check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log.Write("update check: no result (offline?)");
            return false;
        }
        string newVer = Cfg.StripTag(info.Tag);
        if (!Cfg.NewerThan(newVer, Cfg.VERSION) && !force)
        {
            Log.Write("update check: up to date (" + newVer + ")");
            return false;
        }
        if (!Cfg.NewerThan(newVer, Cfg.VERSION))
        {
            MessageBox.Show("PlexCompanion is up to date (version " + newVer + ").",
                "PlexCompanion — Update check", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        Log.Write("update check: newer release " + newVer + " (have " + Cfg.VERSION + ")");
        var n = new UpdateNotice(newVer, Cfg.VERSION, info.AssetUrl);
        if (n.ShowDialog() != DialogResult.OK)
        {
            Log.Write("update: user chose 'Later' - watcher keeps running");
            return false;
        }

        // ---- download the new exe to a temp file ----
        string tmp = Path.Combine(Path.GetTempPath(), "PlexCompanion-" + newVer + ".exe");
        n.SetBusy("Downloading " + newVer + "…");
        bool dl = DownloadExe(info.AssetUrl, tmp);
        if (!dl)
        {
            n.SetBusy(null);
            MessageBox.Show(n, "Download failed — check your connection and try again later.",
                "PlexCompanion — Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log.Write("update: download failed");
            return false;
        }
        Log.Write("update: downloaded to " + tmp + " (" + new FileInfo(tmp).Length + " bytes)");

        // ---- swap (fire-and-forget: the app exits right after this returns,
        // dropping its own lock on the exe; the swap kills any remaining
        // instances then retries the copy until it lands) ----
        n.SetBusy("Installing " + newVer + "…");
        string rootN = Path.GetFullPath(root).TrimEnd('\\');
        string dest = Path.Combine(rootN, "PlexCompanion.exe");
        string script =
            "$dest = " + PSQ(dest) + "; $tmp = " + PSQ(tmp) + "; " +
            "for ($i = 0; $i -lt 24; $i++) { " +
            "  $p = Get-Process -Name PlexCompanion -ErrorAction SilentlyContinue; " +
            "  if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; " +
            "  $p | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue; " +
            "  Start-Sleep -Milliseconds 500 } " +
            "  else { " +
            "  try { Copy-Item -LiteralPath $tmp -Destination $dest -Force; " +
            "  if ((Get-Item -LiteralPath $dest).Length -eq (Get-Item -LiteralPath $tmp).Length) { " +
            "    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; " +
            "    Start-Process -LiteralPath $dest; exit 0 } } catch { } } } " +
            "exit 1";
        try
        {
            StartPowerShell(script, !TestWritable(rootN));   // elevate only if the install folder isn't user-writable
            Log.Write("update: swap launched, exiting to release the exe lock");
        }
        catch (Exception ex)
        {
            Log.Write("update: swap launch failed: " + ex.Message);
            return false;
        }
        return true;
    }

    static bool DownloadExe(string url, string dest)
    {
        try
        {
            Cfg.UseTls12();
            var req = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(url);
            req.KeepAlive = false;
            req.Timeout = 15000;
            req.UserAgent = "PlexCompanion/" + Cfg.VERSION;
            using (var resp = req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var fs = new FileStream(dest, FileMode.Create))
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }
            return File.Exists(dest) && new FileInfo(dest).Length > 1000;
        }
        catch { return false; }
    }

    static bool TestWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".pcwrite");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Single-quoted PowerShell string literal ('' escaping) — safe inside a script.
    static string PSQ(string s)
    {
        return "'" + s.Replace("'", "''") + "'";
    }

    // Launch PowerShell with an encoded (UTF-16LE, Base64) command — the command
    // line contains only Base64 characters, so no path content can be re-parsed
    // by cmd or re-split by the shell (replaces the old injectable "cmd /c ..."
    // string concatenations).
    static Process StartPowerShell(string script, bool elevate)
    {
        string b64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64);
        psi.UseShellExecute = true;
        if (elevate) psi.Verb = "runas";
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        return Process.Start(psi);
    }

    static bool InstallTo(string dir, string srcExe)
    {
        string target = Path.Combine(dir, "PlexCompanion.exe");
        // plain (user-writable) attempt first — no prompt
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(srcExe, target, true);
            return File.Exists(target);
        }
        catch { }
        // needs rights (Program Files & co) — UAC-elevate the one step that writes
        try
        {
            string script = "New-Item -ItemType Directory -Path " + PSQ(dir) + " -Force | Out-Null; " +
                            "Copy-Item -LiteralPath " + PSQ(srcExe) + " -Destination " + PSQ(target) + " -Force";
            Process pr = StartPowerShell(script, true);
            pr.WaitForExit(60000);
            return File.Exists(target);
        }
        catch (Exception ex)
        {
            Log.Write("elevated copy failed: " + ex.Message);
            return false;
        }
    }

    static void ScheduleFolderDelete(string dir)
    {
        try
        {
            string script = "Start-Sleep -Seconds 3; " +
                "if (Test-Path -LiteralPath " + PSQ(dir) + ") { " +
                "Remove-Item -LiteralPath " + PSQ(dir) + " -Recurse -Force }";
            StartPowerShell(script, !TestWritable(dir));
            Log.Write("scheduled folder delete -> " + dir);
        }
        catch (Exception ex) { Log.Write("folder delete failed: " + ex.Message); }
    }

    static int RunRemove(string root, string exe)
    {
        Log.Write("==== /remove: starting ====");
        Stale.Kill();
        foreach (var p in Process.GetProcessesByName("Plex")) { try { p.Kill(); } catch { } }
        System.Threading.Thread.Sleep(400);

        var removed = new List<string>();
        foreach (var f in Shortcuts.Remove()) removed.Add(f);
        Cfg.RemoveStartup();
        removed.Add("startup entry (background watcher)");
        Cfg.RemoveApp();
        removed.Add("registry: HKCU\\Software\\PlexCompanion + Apps & Features");
        foreach (var f in new[] { Path.Combine(root, "config.json"), Path.Combine(root, "frame_state.json") })
            if (File.Exists(f)) { File.Delete(f); removed.Add(Path.GetFileName(f)); }
        Log.Write("remove: " + string.Join(" | ", removed));
        var done = new RemoveDone();
        done.ShowDialog();

        // self-delete the install folder
        string install = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlexCompanion");
        string rootN = Path.GetFullPath(root).TrimEnd('\\');
        string instN = Path.GetFullPath(install).TrimEnd('\\');
        if (Directory.Exists(install) && rootN.Equals(instN, StringComparison.OrdinalIgnoreCase))
        {
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("TEMP") ?? root;
            try
            {
                string script = "Start-Sleep -Seconds 3; " +
                    "if (Test-Path -LiteralPath " + PSQ(root) + ") { " +
                    "Remove-Item -LiteralPath " + PSQ(root) + " -Recurse -Force }";
                StartPowerShell(script, !TestWritable(root));
                Log.Write("remove: scheduled folder self-delete -> " + root);
            }
            catch (Exception ex)
            {
                Log.Write("remove: folder self-delete failed: " + ex.Message);
            }
        }

        Log.Write("remove: done");
        return 0;
    }
}

public class SetupWindow : Form
{
    void OnFormKeyDown(object s, KeyEventArgs e)
  {
    if (_hotkeyBox == null || !_hotkeyBox.Capturing) return;
    if (e.KeyCode == Keys.Escape)
    {
      _hotkeyBox.EscapeCancel();
      e.Handled = true;
      return;
    }
    int mods = 0;
    if (Control.ModifierKeys.HasFlag(Keys.Control)) mods |= 0x0002;
    if (Control.ModifierKeys.HasFlag(Keys.Alt)) mods |= 0x0001;
    if (Control.ModifierKeys.HasFlag(Keys.Shift)) mods |= 0x0004;
    Keys k = e.KeyCode;
    bool plain = (k >= Keys.A && k <= Keys.Z) || (k >= Keys.D0 && k <= Keys.D9) ||
                 (k >= Keys.F1 && k <= Keys.F24) || k == Keys.Space;
    if (mods != 0 && plain)
    {
      int vk;
      if (k >= Keys.A && k <= Keys.Z) vk = k - Keys.A + 0x41;
      else if (k >= Keys.D0 && k <= Keys.D9) vk = k - Keys.D0 + 0x30;
      else if (k >= Keys.F1 && k <= Keys.F24) vk = (int)k;
      else vk = 0x20;
      _hotkeyBox.StageCombo(mods, vk);
      e.Handled = true;
      return;
    }
    if (k == Keys.Alt || k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.LWin || k == Keys.RWin)
      e.Handled = true; // swallow bare modifier presses while capturing
  }

  void UpdateHelper()
  {
    if (_helper != null && _hotkeyBox != null)
      _helper.Text = "Hides Plex's title bar on demand — " + HotText.Text(_hotkeyBox.Mods, _hotkeyBox.Vk) + " toggles it.";
  }

  public string  PlexPathValue    { get; set; }
  public string  AppearanceValue  { get; set; }
  public string  InstallLocation  { get; set; }
  public bool    LaunchPlex       { get; set; }
  public bool    CheckForUpdates  { get; set; }
  public int     HotMods          { get; set; }
  public int     HotVk            { get; set; }
  public static readonly string WinIconB64 = "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";
  static readonly string IconB64 = "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";

  public static readonly Color Bg   = Color.FromArgb(0x0C, 0x0C, 0x0E);
  public static readonly Color Side = Color.FromArgb(0x0E, 0x0E, 0x11);
  public static readonly Color Srf  = Color.FromArgb(0x1B, 0x1B, 0x1F);
  public static readonly Color Strk = Color.FromArgb(0x2A, 0x2A, 0x30);
  public static readonly Color Div  = Color.FromArgb(0x1E, 0x1E, 0x22);
  public static readonly Color Acc  = Color.FromArgb(0xEC, 0xB0, 0x1D);
  public static readonly Color AccH = Color.FromArgb(0xF5, 0xC3, 0x3A);
  public static readonly Color AccT = Color.FromArgb(0x1A, 0x1A, 0x1A);
  public static readonly Color Tp   = Color.FromArgb(0xF5, 0xF5, 0xF7);
  public static readonly Color Ts   = Color.FromArgb(0xA0, 0xA0, 0xA6);
  public static readonly Color Tm   = Color.FromArgb(0x6B, 0x6B, 0x70);

  TextBox   _plexPath;
  TextBox   _installPath;
  RBtn      _dark, _light;
  GoldCheck _launch, _updates;
  RBtn      _cancel, _next, _back, _installBtn;
  Label     _heading;
  HotKeyBox _hotkeyBox;
  Label     _hkLabel;
  RBtn      _setBtn;
  Label     _apLabel;
  Label     _helper;
  bool      _darkSel = true, _lightSel = false;
  bool      _isFirstRun;

  public SetupWindow(string plexPath, string appearance, string installDir, bool isFirstRun, int hotMods, int hotVk)
  {
    this.Text = "PlexCompanion - Setup";
    this.BackColor = Bg;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;
    this.ClientSize = new Size(740, 460);
    this.StartPosition = FormStartPosition.CenterScreen;
    this.DoubleBuffered = true;

    _darkSel = string.Equals(appearance, "dark", StringComparison.OrdinalIgnoreCase);
    _lightSel = !_darkSel;
    _isFirstRun = isFirstRun;

    Build(plexPath, installDir, hotMods, hotVk);
    this.KeyPreview = true;
    this.KeyDown += OnFormKeyDown;
    UpdateHelper();
    ShowPage(1);
  }

  static Region Rnd(int w, int h, int r)
  {
    using (GraphicsPath p = P.RndRect(new RectangleF(0, 0, w, h), r))
      return new Region(p);
  }

  static RBtn MkBtn(string text, int x, int y, int w, int h, Color fill, Color fg, bool primary)
  {
    Color border = primary ? fill : Strk;
    Color hover = primary ? AccH : Color.FromArgb(0x23, 0x23, 0x29);
    return new RBtn(text, x, y, w, h, fill, hover, fg, border, primary, Bg);
  }

  static RBtn MkBtnCard(string text, int x, int y, int w, int h, Color fill, Color fg)
  {
    return new RBtn(text, x, y, w, h, fill, Color.FromArgb(0x23, 0x23, 0x29), fg, Strk, false, Srf);
  }

  static Panel MakeCard(int x, int y, int w, int h)
  {
    Panel card = new Panel();
    card.Bounds = new Rectangle(x, y, w, h);
    card.BackColor = Srf;
    card.Region = Rnd(w, h, 10);
    card.Paint += delegate(object s, PaintEventArgs e)
    {
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      RectangleF b = new RectangleF(0.5f, 0.5f, card.Width - 1, card.Height - 1);
      using (GraphicsPath p = P.RndRect(b, 10))
        using (Pen pen = new Pen(Strk, 1)) g.DrawPath(pen, p);
    };
    return card;
  }

  void ShowPage(int page)
  {
    this.Text = (page == 1) ? "PlexCompanion - Setup" : "PlexCompanion - Install";
    if (_heading != null) _heading.Text = (page == 1) ? "Setup Plex Companion" : "Install Plex Companion";
    if (_helper != null) _helper.Text = (page == 1) ? ("Hides Plex's title bar on demand — " + HotText.Text(_hotkeyBox.Mods, _hotkeyBox.Vk) + " toggles it.") : "Select an install location";

    _plexCard.Visible = (page == 1);
    _dark.Visible = _light.Visible = (page == 1);
    _hkLabel.Visible = (page == 1);
    _hotkeyBox.Visible = (page == 1);
    _setBtn.Visible = (page == 1);
    if (_apLabel != null) _apLabel.Visible = (page == 1);
    if (_helper != null) _helper.Visible = true;
    _instCard.Visible = (page == 2);
    _launch.Visible = _updates.Visible = (page == 2);

    _cancel.Visible = (page == 1);
    _next.Visible = (page == 1);
    _back.Visible = (page == 2);
    _installBtn.Visible = (page == 2);

    if (page == 1) SyncPills();
  }
  Panel _plexCard, _instCard;
  void Build(string plexPath, string installDir, int hotMods, int hotVk)
  {
    int W = ClientSize.Width, H = ClientSize.Height;
    int sideW = 228;

    // ---------- left sidebar ----------
    Panel side = new Panel();
    side.Bounds = new Rectangle(0, 0, sideW, H);
    side.BackColor = Side;
    this.Controls.Add(side);

    Font wmFont = new Font("Segoe UI Semibold", 19f);
    int iconSz = Math.Max(48, TextRenderer.MeasureText("Companion", wmFont).Width - 10);
    wmFont.Dispose();
    int barH = 4, verH = 20, gap = 16, gap2 = 10;
    int blockH = iconSz + gap + barH + gap2 + verH;
    int sTop = H / 2 - blockH / 2;

    Panel icon = new Panel();
    icon.Bounds = new Rectangle((sideW - iconSz) / 2, sTop, iconSz, iconSz);
    icon.BackColor = Side;
    icon.Paint += delegate(object s, PaintEventArgs e)
    {
      try
      {
        byte[] raw = System.Convert.FromBase64String(IconB64);
        using (MemoryStream ms = new MemoryStream(raw))
          using (Image img = Image.FromStream(ms))
            e.Graphics.DrawImage(img, icon.ClientRectangle);
      }
      catch { }
    };
    side.Controls.Add(icon);

    Panel bar = new Panel();
    bar.Bounds = new Rectangle((sideW - 84) / 2, sTop + iconSz + gap, 84, barH);
    bar.BackColor = Side;
    bar.Paint += delegate(object s, PaintEventArgs e)
    {
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      RectangleF b = new RectangleF(0.5f, 0.5f, bar.Width - 1, bar.Height - 1);
      using (GraphicsPath p = P.RndRect(b, 4))
        using (SolidBrush br = new SolidBrush(Acc)) g.FillPath(br, p);
    };
    side.Controls.Add(bar);

    Label ver = new Label();
    ver.Text = "version " + Cfg.VERSION;
    ver.Font = new Font("Segoe UI", 10f);
    ver.ForeColor = Tm;
    ver.TextAlign = ContentAlignment.MiddleCenter;
    ver.Bounds = new Rectangle(0, sTop + iconSz + gap + barH + gap2, sideW, verH);
    side.Controls.Add(ver);

    Panel divp = new Panel();
    divp.Bounds = new Rectangle(sideW, 0, 1, H);
    divp.BackColor = Div;
    this.Controls.Add(divp);

    // ---------- right content ----------
    int x0 = sideW + 44, x1 = W - 44, colW = x1 - x0;

    _heading = new Label();
    Label h1 = _heading;
    h1.Text = "Setup Plex Companion";
    h1.Font = new Font("Segoe UI Semibold", 18f);
    h1.ForeColor = Tp;
    h1.AutoSize = true;
    h1.Location = new Point(x0, 40);
    this.Controls.Add(h1);

    _helper = new Label();
    _helper.Font = new Font("Segoe UI", 9.5f);
    _helper.ForeColor = Ts;
    _helper.AutoSize = true;
    _helper.Location = new Point(x0, 84);
    this.Controls.Add(_helper);
    int cardY = 128, cardH = 64;
    // ---- PAGE 1: PLEX LOCATION card ----
    _plexCard = MakeCard(x0, cardY, colW, cardH);
    this.Controls.Add(_plexCard);

    Label fl = new Label();
    fl.Text = "PLEX LOCATION";
    fl.Font = new Font("Segoe UI Semibold", 8f);
    fl.ForeColor = Tm;
    fl.AutoSize = true;
    fl.Location = new Point(16, 12);
    _plexCard.Controls.Add(fl);

    _plexPath = new TextBox();
    _plexPath.Text = plexPath;
    _plexPath.Font = new Font("Segoe UI", 11f);
    _plexPath.ForeColor = Tp;
    _plexPath.BackColor = Srf;
    _plexPath.BorderStyle = BorderStyle.None;
    _plexPath.Location = new Point(16, 32);
    _plexPath.Width = colW - 150;
    _plexCard.Controls.Add(_plexPath);

    RBtn change = MkBtnCard("Change", colW - 120, 15, 104, 34, Srf, Tp);
    change.Click += delegate(object s, EventArgs e) { BrowsePlex(); };
    _plexCard.Controls.Add(change);

    // ---- PAGE 1: TOGGLE HOTKEY (click the box, press the combo) ----
    _hkLabel = new Label();
    Label hk = _hkLabel;
    hk.Text = "TOGGLE HOTKEY";
    hk.Font = new Font("Segoe UI Semibold", 8f);
    hk.ForeColor = Tm;
    hk.AutoSize = true;
    hk.Location = new Point(x0, cardY + cardH + 26);
    this.Controls.Add(hk);

    _hotkeyBox = new HotKeyBox(hotMods, hotVk);
    _hotkeyBox.Bounds = new Rectangle(x0, cardY + cardH + 26 + 22, 210, 34);
    this.Controls.Add(_hotkeyBox);
    _hotkeyBox.Changed += delegate { UpdateHelper(); };

    RBtn setBtn = MkBtn("Set", x0 + 210 + 10, cardY + cardH + 26 + 22, 70, 34, Srf, Tp, false);
    _setBtn = setBtn;
    setBtn.Click += delegate(object s2, EventArgs e2) { _hotkeyBox.Commit(); };
    this.Controls.Add(setBtn);

        // ---- PAGE 1: TITLE BAR APPEARANCE ----
    _apLabel = new Label();
    Label ap = _apLabel;
    ap.Text = "TITLE BAR APPEARANCE";
    ap.Font = new Font("Segoe UI Semibold", 8f);
    ap.ForeColor = Tm;
    ap.AutoSize = true;
    ap.Location = new Point(x0, cardY + cardH + 26 + 22 + 34 + 26);
    this.Controls.Add(ap);

    int pillW = 112, pillH = 30;
    int pillY = cardY + cardH + 26 + 22 + 34 + 26 + 22;
    _dark = MkBtn("Dark", x0, pillY, pillW, pillH, Srf, Tp, false);
    _dark.Font = new Font("Segoe UI Semibold", 11f);
    _dark.Click += delegate(object s, EventArgs e) { _darkSel = true; _lightSel = false; SyncPills(); };
    _light = MkBtn("Light", x0 + pillW + 10, pillY, pillW, pillH, Srf, Tp, false);
    _light.Font = new Font("Segoe UI Semibold", 11f);
    _light.Click += delegate(object s, EventArgs e) { _darkSel = false; _lightSel = true; SyncPills(); };
    this.Controls.Add(_dark);
    this.Controls.Add(_light);

    // ---- PAGE 2: INSTALL LOCATION card ----
    _instCard = MakeCard(x0, cardY, colW, cardH);
    this.Controls.Add(_instCard);

    Label il = new Label();
    il.Text = "INSTALL LOCATION";
    il.Font = new Font("Segoe UI Semibold", 8f);
    il.ForeColor = Tm;
    il.AutoSize = true;
    il.Location = new Point(16, 12);
    _instCard.Controls.Add(il);

    _installPath = new TextBox();
    _installPath.Text = installDir;
    _installPath.Font = new Font("Segoe UI", 11f);
    _installPath.ForeColor = Tp;
    _installPath.BackColor = Srf;
    _installPath.BorderStyle = BorderStyle.None;
    _installPath.Location = new Point(16, 32);
    _installPath.Width = colW - 150;
    _instCard.Controls.Add(_installPath);

    RBtn change2 = MkBtnCard("Change", colW - 120, 15, 104, 34, Srf, Tp);
    change2.Click += delegate(object s, EventArgs e) { BrowseInstall(); };
    _instCard.Controls.Add(change2);

    // ---- PAGE 2: OPTIONS — auto-update check first, then launch Plex (pushed down) ----
    _updates = new GoldCheck("Automatically check for updates");
    _updates.Bounds = new Rectangle(x0, cardY + cardH + 26, 320, 30);
    _updates.Checked = true;
    this.Controls.Add(_updates);

    _launch = new GoldCheck("Start Plex after installation");
    _launch.Bounds = new Rectangle(x0, cardY + cardH + 64, 320, 30);
    _launch.Checked = true;
    this.Controls.Add(_launch);

    // ---- ACTION BAR ----
    int ay = H - 74;
    _cancel = MkBtn("Cancel", x1 - 200, ay, 90, 40, Srf, Tp, false);
    _cancel.Click += delegate(object s, EventArgs e)
    {
      this.DialogResult = DialogResult.Cancel;
      this.Close();
    };

    _next = MkBtn("Next", x1 - 100, ay, 100, 40, Acc, AccT, true);
    _next.Click += delegate(object s, EventArgs e) { ShowPage(2); };

    _back = MkBtn("Back", x1 - 200, ay, 90, 40, Srf, Tp, false);
    _back.Click += delegate(object s, EventArgs e) { ShowPage(1); };

    _installBtn = MkBtn("Install", x1 - 100, ay, 100, 40, Acc, AccT, true);
    _installBtn.Click += delegate(object s, EventArgs e)
    {
      string dir = _installPath.Text.Trim();
      if (dir.Length == 0)
      {
        MessageBox.Show(this, "Pick a folder to install PlexCompanion to.",
          "PlexCompanion — Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      PlexPathValue = _plexPath.Text.Trim();
      AppearanceValue = _darkSel ? "dark" : "light";
      InstallLocation = _isFirstRun ? dir : null;
      LaunchPlex = _launch.Checked;
      CheckForUpdates = _updates.Checked;
      HotMods = _hotkeyBox.Mods;
      HotVk = _hotkeyBox.Vk;
      this.DialogResult = DialogResult.OK;
      this.Close();
    };

    this.Controls.Add(_cancel);
    this.Controls.Add(_next);
    this.Controls.Add(_back);
    this.Controls.Add(_installBtn);

    // Enter / Escape
    this.KeyPreview = true;
    this.KeyDown += delegate(object s, KeyEventArgs e)
    {
      if (e.KeyCode == Keys.Enter && !_hotkeyBox.Capturing)
      {
        if (_next.Visible) _next.PerformClick();
        else if (_installBtn.Visible) _installBtn.PerformClick();
        e.Handled = true;
      }
      if (e.KeyCode == Keys.Escape)
      {
        if (_hotkeyBox.EscapeEnded)
          return;   // Escape just cancelled a capture — window stays open
        if (_cancel.Visible) _cancel.PerformClick();
        else if (_back.Visible) _back.PerformClick();
        e.Handled = true;
      }
    };

    // taskbar icon
    try
    {
      byte[] icoRaw = System.Convert.FromBase64String(WinIconB64);
      using (MemoryStream icoMs = new MemoryStream(icoRaw))
        this.Icon = new Icon(icoMs);
    }
    catch { }

    this.Load += delegate(object s, EventArgs e) { ApplyTitleBar(); };
    this.Shown += delegate(object s, EventArgs e)
    {
      _plexPath.SelectionStart = _plexPath.TextLength;
      _plexPath.SelectionLength = 0;
    };
  }

  void SyncPills()
  {
    _dark.SetColors(_darkSel ? Acc : Srf, _darkSel ? AccH : Color.FromArgb(0x23, 0x23, 0x29), _darkSel ? AccT : Tp, _darkSel ? Acc : Strk);
    _light.SetColors(_lightSel ? Acc : Srf, _lightSel ? AccH : Color.FromArgb(0x23, 0x23, 0x29), _lightSel ? AccT : Tp, _lightSel ? Acc : Strk);
    ApplyTitleBar();
  }

  void ApplyTitleBar()
  {
    try
    {
      if (this.IsHandleCreated)
      {
        int v = _darkSel ? 1 : 0;
        P.DwmSetWindowAttribute(this.Handle, 20, ref v, 4);
        P.DwmSetWindowAttribute(this.Handle, 19, ref v, 4);
      }
    }
    catch { }
  }

  void BrowsePlex()
  {
    using (FolderBrowserDialog dlg = new FolderBrowserDialog())
    {
      dlg.Description = "Select the folder containing Plex.exe";
      if (dlg.ShowDialog() == DialogResult.OK)
      {
        string p = System.IO.Path.Combine(dlg.SelectedPath, "Plex.exe");
        _plexPath.Text = p;
        if (!System.IO.File.Exists(p))
          MessageBox.Show(this, "No Plex.exe found in that folder — double-check the path.",
            "Browse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }
  }

  void BrowseInstall()
  {
    using (FolderBrowserDialog dlg = new FolderBrowserDialog())
    {
      dlg.Description = "Select the folder to install PlexCompanion to";
      if (dlg.ShowDialog() == DialogResult.OK)
        _installPath.Text = dlg.SelectedPath;
    }
  }
}
public class RBtn : Label
{
  Color _fill, _hover, _fg, _border, _pad;
  int _rad = 8;
  bool _hovered = false;
  public RBtn(string text, int x, int y, int w, int h,
              Color fill, Color hover, Color fg, Color border, bool weight, Color pad) : base()
  {
    _fill = fill; _hover = hover; _fg = fg; _border = border; _pad = pad;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
    AutoSize = false;
    Text = text;
    Bounds = new Rectangle(x, y, w, h);
    BackColor = _pad;
    Font = weight ? new Font("Segoe UI Semibold", 11f) : new Font("Segoe UI", 11f);
    Cursor = Cursors.Hand;
  }
  public void SetColors(Color fill, Color hover, Color fg, Color border)
  { _fill = fill; _hover = hover; _fg = fg; _border = border; Invalidate(); }
  public void PerformClick() { OnClick(EventArgs.Empty); }   // public surface over Control's protected PerformClick
  protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
  protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
  protected override void OnPaint(PaintEventArgs e)
  {
    Graphics g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    Color f = _hovered ? _hover : _fill;
    RectangleF b = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
    using (GraphicsPath p = P.RndRect(b, _rad))
    {
      using (SolidBrush br = new SolidBrush(f)) g.FillPath(br, p);
      if (_border != f && _border != Color.Transparent)
        using (Pen pen = new Pen(_border, 1)) g.DrawPath(pen, p);
    }
    TextFormatFlags fl = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
    TextRenderer.DrawText(g, Text, Font, ClientRectangle, _fg, Color.Transparent, fl);
  }
}
// hotkey capture box (click, then press the desired modifier+key combo)
public class HotKeyBox : Panel
{
  public int     Mods      { get { return _inMods; } }
  public int     Vk        { get { return _inVk; } }
  public bool    Capturing { get { return _cap; } }

  int _inMods = 1, _inVk = 0x51;          // in effect (committed)
  int _stgMods = 1, _stgVk = 0x51;        // staged (pressed, not set)
  bool _cap = false, _staged = false;
  bool _escapeEnded = false;              // one-shot: true on the tick Escape cancelled a capture
  public bool EscapeEnded { get { bool v = _escapeEnded; _escapeEnded = false; return v; } }
  public event Action Changed;

  static readonly Color Bg   = SetupWindow.Bg;
  static readonly Color Srf  = SetupWindow.Srf;   // same surface as the plex location field
  static readonly Color Strk = SetupWindow.Strk;  // same border
  static readonly Color Tp   = SetupWindow.Tp;
  static readonly Color GoldSolid = Color.FromArgb(0xEC, 0xB0, 0x1D);
  static readonly Color GoldSoft  = Color.FromArgb(0xF5, 0xC3, 0x3A);  // staged: bright gold (clear, not muddy)
  static readonly Color DarkGold  = Color.FromArgb(0x14, 0x10, 0x04);
  static readonly int  Rad = 8;                      // same radius as every button/pill

  public HotKeyBox(int mods, int vk)
  {
    int m = (mods != 0) ? mods : 1;
    int v = (vk != 0) ? vk : 0x51;
    _inMods = m; _inVk = v; _stgMods = m; _stgVk = v;
    this.BackColor = Bg;   // keep corners page-colored; OnPaint draws the rounded fill
    this.DoubleBuffered = true;
    this.Cursor = Cursors.Hand;
    this.TabStop = true;
    this.Paint += OnPaintBox;
    this.MouseDown += delegate(object s, MouseEventArgs e)
    {
      if (e.Button == MouseButtons.Left) StartCapture();
    };
    Refresh();
  }

  void StartCapture()
  {
    _cap = true;
    this.Focus();
    ApplyLook();
  }

  public void StageCombo(int mods, int vk)
  {
    _stgMods = mods;
    _stgVk = vk;
    _staged = true;
    ApplyLook();
    if (Changed != null) Changed();
  }

  // Set button pressed: staged becomes in-effect
  public void Commit()
  {
    if (_staged)
    {
      _inMods = _stgMods;
      _inVk = _stgVk;
      _staged = false;
      _cap = false;
      ApplyLook();
      if (Changed != null) Changed();
    }
    else
    {
      _cap = false;
      ApplyLook();
    }
  }

  public void CancelCapture()
  {
    _cap = false;
    _staged = false;
    _stgMods = _inMods;
    _stgVk = _inVk;
    ApplyLook();
  }

  // Escape while capturing: flag it so the form's Cancel/Back handler
  // (which runs AFTER this handler, since all KeyDown handlers fire) backs off.
  public void EscapeCancel()
  {
    CancelCapture();
    _escapeEnded = true;
  }

  void ApplyLook()
  {
    this.BackColor = Bg;   // corners stay page-colored so the rounded fill shows
    this.Invalidate();
  }

  void OnPaintBox(object s, PaintEventArgs e)
  {
    Graphics g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    RectangleF b = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
    using (GraphicsPath p = P.RndRect(b, Rad))
    {
      Color fill, border;
      if (_cap && !_staged)      { fill = GoldSolid; border = GoldSolid; }
      else if (_staged)          { fill = GoldSoft;  border = Color.FromArgb(0xC9, 0x8E, 0x12); }
      else                       { fill = Srf;       border = Strk; }

      using (SolidBrush br = new SolidBrush(fill)) g.FillPath(br, p);
      using (Pen pen = new Pen(border, (_cap || _staged) ? 1.5f : 1)) g.DrawPath(pen, p);

      string text;
      Color fg;
      bool bold;
      if (_cap && !_staged)      { text = "Press a key combo..."; fg = DarkGold; bold = false; }
      else if (_staged)          { text = HotText.Text(_stgMods, _stgVk); fg = DarkGold; bold = true; }
      else                       { text = HotText.Text(_inMods, _inVk); fg = Tp; bold = false; }

      TextFormatFlags fl = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
      using (Font f = new Font("Segoe UI", 11f, bold ? FontStyle.Bold : FontStyle.Regular))
        TextRenderer.DrawText(g, text, f, this.ClientRectangle, fg, Color.Transparent, fl);
    }
  }
}
// gold rounded checkbox (custom-drawn)
public class GoldCheck : Panel
{
  public bool Checked { get { return _checked; } set { _checked = value; this.Invalidate(); } }
  bool _checked = true;

  static readonly Color Bg   = SetupWindow.Bg;
  static readonly Color Srf  = SetupWindow.Srf;
  static readonly Color Strk = SetupWindow.Strk;
  static readonly Color Acc  = SetupWindow.Acc;
  static readonly Color AccT = SetupWindow.AccT;
  static readonly Color Tp   = SetupWindow.Tp;

  public GoldCheck(string text)
  {
    this.BackColor = Bg;
    this.DoubleBuffered = true;
    this.Cursor = Cursors.Hand;

    Label lbl = new Label();
    lbl.Text = text;
    lbl.Font = new Font("Segoe UI", 11f);
    lbl.ForeColor = Tp;
    lbl.AutoSize = true;
    lbl.Location = new Point(26, 4);
    lbl.Click += delegate(object s, EventArgs e) { Checked = !Checked; };
    this.Controls.Add(lbl);

    this.Click += delegate(object s, EventArgs e) { Checked = !Checked; };
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    base.OnPaint(e);
    Graphics g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    int sz = 18, x = 0, y = (Height - sz) / 2, rad = 5;
    Rectangle r = new Rectangle(x, y, sz, sz);
    GraphicsPath p = P.RndRect(new RectangleF(r.X, r.Y, r.Width, r.Height), rad);

    if (Checked)
      using (SolidBrush b = new SolidBrush(Acc)) g.FillPath(b, p);
    else
    {
      using (SolidBrush b = new SolidBrush(Srf)) g.FillPath(b, p);
      using (Pen pen = new Pen(Strk, 1)) g.DrawPath(pen, p);
    }

    if (Checked)
      using (Pen pen = new Pen(AccT, 2.2f))
      {
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        int cx = r.X + 4, cy = r.Y + 10;
        g.DrawLine(pen, cx, cy, cx + 4, cy + 4);
        g.DrawLine(pen, cx + 4, cy + 4, cx + 9, cy - 5);
      }
    p.Dispose();
  }
}
// uninstall-complete dialog — same design language as the setup window
public class RemoveDone : Form
{
  static readonly string WinIconB64 = SetupWindow.WinIconB64;
  static readonly Color Bg   = SetupWindow.Bg;
  static readonly Color Acc  = SetupWindow.Acc;
  static readonly Color AccT = SetupWindow.AccT;
  static readonly Color Tp   = SetupWindow.Tp;
  public RemoveDone()
  {
    this.Text = "PlexCompanion - Removed";
    this.BackColor = Bg;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;
    this.ShowInTaskbar = true;
    this.ClientSize = new Size(400, 160);
    this.StartPosition = FormStartPosition.CenterScreen;
    this.DoubleBuffered = true;
    try
    {
      byte[] icoRaw = System.Convert.FromBase64String(WinIconB64);
      using (MemoryStream icoMs = new MemoryStream(icoRaw))
        this.Icon = new Icon(icoMs);
    }
    catch { /* icon is cosmetic */ }
    Label h1 = new Label();
    h1.Text = "Uninstall complete";
    h1.Font = new Font("Segoe UI Semibold", 16f);
    h1.ForeColor = Tp;
    h1.AutoSize = false;
    h1.TextAlign = ContentAlignment.MiddleCenter;
    h1.Bounds = new Rectangle(0, 36, 400, 28);

    this.Controls.Add(h1);
    RBtn ok = new RBtn("OK", (400 - 120) / 2, 84, 120, 40, Acc, SetupWindow.AccH, AccT, Acc, true, Bg);
    ok.Click += delegate(object s, EventArgs e) { this.DialogResult = DialogResult.OK; this.Close(); };
    this.Controls.Add(ok);
    this.KeyPreview = true;
    this.KeyDown += delegate(object s, KeyEventArgs e)
    {
      if (e.KeyCode == Keys.Enter)  { ok.PerformClick(); e.Handled = true; }
      if (e.KeyCode == Keys.Escape) { this.DialogResult = DialogResult.OK; this.Close(); e.Handled = true; }

    };
    this.Load += delegate(object s, EventArgs e)
    {
      try { if (this.IsHandleCreated) { int v = 1; P.DwmSetWindowAttribute(this.Handle, 20, ref v, 4); P.DwmSetWindowAttribute(this.Handle, 19, ref v, 4); } }
      catch { }
    };
  }
}
// update-available dialog — same design language as the setup/remove dialogs.
// "Update now" -> DialogResult.OK (caller then downloads + swaps); "Later" -> Cancel.
public class UpdateNotice : Form
{
  static readonly Color Bg   = SetupWindow.Bg;
  static readonly Color Srf  = SetupWindow.Srf;
  static readonly Color Strk = SetupWindow.Strk;
  static readonly Color Acc  = SetupWindow.Acc;
  static readonly Color AccH = SetupWindow.AccH;
  static readonly Color AccT = SetupWindow.AccT;
  static readonly Color Tp   = SetupWindow.Tp;
  static readonly Color Ts   = SetupWindow.Ts;

  RBtn  _later;
  Label _status;
  bool  _busy;

  public UpdateNotice(string newVersion, string currentVersion, string assetUrl)
  {
    this.Text = "PlexCompanion - Update available";
    this.BackColor = Bg;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;
    this.ShowInTaskbar = true;
    this.ClientSize = new Size(400, 192);
    this.StartPosition = FormStartPosition.CenterScreen;
    this.DoubleBuffered = true;

    try
    {
      byte[] icoRaw = System.Convert.FromBase64String(SetupWindow.WinIconB64);
      using (MemoryStream icoMs = new MemoryStream(icoRaw))
        this.Icon = new Icon(icoMs);
    }
    catch { /* icon is cosmetic */ }

    Label h1 = new Label();
    h1.Text = "Update available";
    h1.Font = new Font("Segoe UI Semibold", 16f);
    h1.ForeColor = Tp;
    h1.AutoSize = false;
    h1.TextAlign = ContentAlignment.MiddleCenter;
    h1.Bounds = new Rectangle(0, 30, 400, 28);
    this.Controls.Add(h1);

    Label sub = new Label();
    sub.Text = "Current version  v" + currentVersion + "\r\n" + "Update available  v" + newVersion;
    sub.Font = new Font("Segoe UI", 10f);
    sub.ForeColor = Ts;
    sub.AutoSize = false;
    sub.TextAlign = ContentAlignment.MiddleCenter;
    sub.Bounds = new Rectangle(0, 60, 400, 40);
    this.Controls.Add(sub);

    _status = new Label();
    _status.Font = new Font("Segoe UI", 9.5f);
    _status.ForeColor = Ts;
    _status.AutoSize = false;
    _status.TextAlign = ContentAlignment.MiddleCenter;
    _status.Bounds = new Rectangle(0, 104, 400, 22);
    _status.Visible = false;
    this.Controls.Add(_status);

    RBtn later = new RBtn("Later", 60, 128, 120, 40, Srf, Color.FromArgb(0x23, 0x23, 0x29), Tp, Strk, false, Bg);
    _later = later;
    later.Click += delegate(object s, EventArgs e) { if (_busy) return; this.DialogResult = DialogResult.Cancel; this.Close(); };
    this.Controls.Add(later);

    RBtn update = new RBtn("Update now", 220, 128, 120, 40, Acc, AccH, AccT, Acc, true, Bg);
    update.Click += delegate(object s, EventArgs e) { if (_busy) return; this.DialogResult = DialogResult.OK; this.Close(); };
    this.Controls.Add(update);

    this.KeyPreview = true;
    this.KeyDown += delegate(object s, KeyEventArgs e)
    {
      if (_busy) return;
      if (e.KeyCode == Keys.Enter)  { this.DialogResult = DialogResult.OK; this.Close(); e.Handled = true; }
      if (e.KeyCode == Keys.Escape) { this.DialogResult = DialogResult.Cancel; this.Close(); e.Handled = true; }
    };

    this.Load += delegate(object s, EventArgs e)
    {
      try { if (this.IsHandleCreated) { int v = 1; P.DwmSetWindowAttribute(this.Handle, 20, ref v, 4); P.DwmSetWindowAttribute(this.Handle, 19, ref v, 4); } }
      catch { }
    };
  }

  // Show progress text during download/install; null hides it.
  public void SetBusy(string text)
  {
    _busy = text != null;
    _status.Text = text ?? "";
    _status.Visible = !string.IsNullOrEmpty(text);
    _later.Enabled = !_busy;
    Update();
  }
}
