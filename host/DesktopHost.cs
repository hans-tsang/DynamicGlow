// Dynamic Glow · desktop host (fork of Deskland, GPL-3.0)
//
// Does four things:
//   1. Attaches a borderless window below the desktop icons (wallpaper layer) that shows the current theme page in WebView2 (e.g. prototype/flat.html?wallpaper=1)
//   0. Settings window (prototype/settings.html): pick a theme and tweak parameters, synced live to the wallpaper and saved to settings.json; one-click sharing (the wallpaper page renders an anonymized desktop image)
//   2. Reads the real desktop icon positions every 400 ms (cross-process messages to the desktop ListView) and sends them to the page only when they change
//   3. Pauses animation while the foreground window is maximized / fullscreen; the tray icon can pause, reload and exit (the original wallpaper is restored on exit)
//   4. When Explorer rebuilds the wallpaper layer (e.g. Win+D), our window is destroyed with it → reattach automatically
//
// Compiled with the .NET Framework compiler that ships with Windows (C# 5 syntax); see build.ps1.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

static class Program
{
    public static readonly string ExitEventName = "DynamicGlowHost_Exit" + InstallProfile.Suffix;
    public static readonly string MenuEventName = "DynamicGlowHost_Menu" + InstallProfile.Suffix;
    public static readonly string SettingsEventName = "DynamicGlowHost_Settings" + InstallProfile.Suffix;

    // Signal the running instance (exit / show menu)
    static void Signal(string name)
    {
        Native.AllowSetForegroundWindow(-1);            // Let the running instance bring its menu to the foreground
        EventWaitHandle ev;
        if (EventWaitHandle.TryOpenExisting(name, out ev)) ev.Set();
    }

    [STAThread]
    static void Main(string[] args)
    {
        // DynamicGlow.exe --exit     : ask the running instance to quit (restores the original wallpaper)
        // DynamicGlow.exe --menu     : show the tray menu at the mouse position
        // DynamicGlow.exe --settings : open the settings window
        if (args.Length > 0 && args[0] == "--exit") { Signal(ExitEventName); return; }
        if (args.Length > 0 && args[0] == "--menu") { Signal(MenuEventName); return; }
        if (args.Length > 0 && args[0] == "--settings") { Signal(SettingsEventName); return; }
        if (args.Length > 0 && args[0] == "--exit-installed") { Environment.ExitCode = InstallProfile.StopInstalled(); return; }
        // Preview never migrates or shares the development edition's settings.                                 // Data directory and autostart entry used to be migrated from the old name (Habitat Wallpaper)
        bool fromAutostart = Array.IndexOf(args, "--autostart") >= 0;
        if (fromAutostart) AutoStartCheck.MarkLaunched();  // Autostart did run (whether or not an instance is already running)
        bool created;
        var mutex = new Mutex(true, "DynamicGlowHost_Singleton" + InstallProfile.Suffix, out created);
        if (!created)
        {
            if (!fromAutostart) Signal(SettingsEventName); // Already running: launching again = open settings (the autostart launch exits quietly)
            return;
        }
        // Work in physical pixels: desktop icon coordinates and screen size are physical pixels
        if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Log anything unexpected; a crash that would end the app is also shown, never a silent exit
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("Unhandled exception: " + e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { FatalError(e.ExceptionObject as Exception); };
#if SINGLE
        if (!AppFiles.Unpack())
        {
            MessageBox.Show("Dynamic Glow couldn't unpack its files to " + Path.Combine(Log.DataDir, "app") + ".\n\n" +
                "If an antivirus blocked it, allow DynamicGlow.exe or use the zip download instead. Details are in " + Log.FilePath,
                "Dynamic Glow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
#endif
        string missing = AppFiles.Missing();
        if (missing != null)
        {
            Log.Write("Missing app file: " + missing);
            MessageBox.Show("Dynamic Glow is missing \"" + missing + "\" next to DynamicGlow.exe.\n\n" +
                "Extract the whole zip into a folder (don't run the exe from inside the zip). If an antivirus removed a file, restore it or allow the folder, then try again.",
                "Dynamic Glow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try
        {
            Rename.Migrate();
            Run(args, AppFiles.FindPages());
        }
        catch (Exception ex) { FatalError(ex); }
        GC.KeepAlive(mutex);
    }

    static void FatalError(Exception ex)
    {
        Log.Write("Fatal exception: " + ex);
        MessageBox.Show("Dynamic Glow stopped because of an error:\n\n" + (ex == null ? "unknown error" : ex.Message) +
            "\n\nDetails are in " + Log.FilePath, "Dynamic Glow", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // Kept out of Main so a missing WebView2 DLL is reported by the check above rather than failing when Main is compiled.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Run(string[] args, string pageDir)
    {
        string runtime = null;
        try { runtime = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception ex) { Log.Write("WebView2 runtime check: " + ex.Message); }
        if (string.IsNullOrEmpty(runtime))
        {
            Log.Write("WebView2 Runtime not found");
            if (MessageBox.Show("Dynamic Glow needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\nOpen the download page now? (Choose \"Evergreen Bootstrapper\", install it, then start Dynamic Glow again.)",
                    "Dynamic Glow", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                try { Process.Start("https://developer.microsoft.com/microsoft-edge/webview2/#download"); } catch { }
            return;
        }
        // 24H2+ attach mode: by default a layered child of Progman (flickers briefly when the system wallpaper changes);
        //   --defview: inside the icon layer, below the icon list (experimental: does not render in practice); --legacy: inside WorkerW (destroyed along with it when the wallpaper changes)
        string attach = "progman";
        bool autostart = false;                           // --autostart: started at logon, do not open the settings window
        foreach (string a in args)
        {
            if (a == "--legacy") attach = "legacy";
            else if (a == "--defview") attach = "defview";
            else if (a == "--autostart") autostart = true;
            else if (!a.StartsWith("--")) pageDir = a;
        }
        WallpaperForm.AttachMode = attach;
        Application.Run(new HostContext(pageDir, !autostart));
    }
}

// Portable layout: DynamicGlow.exe, the WebView2 DLLs and the icon in one folder, pages in its "pages" subfolder.
// A development build (exe in host\bin) uses the pages in ..\..\prototype.
// The single-exe build (/define:SINGLE) carries that folder as files.zip and unpacks it once per version to %LOCALAPPDATA%\DynamicGlow\app\<hash>.
static class AppFiles
{
    public static string AppDir = AppDomain.CurrentDomain.BaseDirectory;
#if SINGLE
    public static bool Unpack()
    {
        try
        {
            byte[] zip;
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("files.zip"))
            {
                zip = new byte[res.Length];
                int n = 0; while (n < zip.Length) n += res.Read(zip, n, zip.Length - n);
            }
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(zip)).Replace("-", "").Substring(0, 12);
            string root = Path.Combine(Log.DataDir, "app"), dir = Path.Combine(root, hash);
            if (!File.Exists(Path.Combine(dir, ".ok")))
            {
                string tmp = dir + ".tmp" + Process.GetCurrentProcess().Id;
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                using (var a = new System.IO.Compression.ZipArchive(new MemoryStream(zip)))
                    foreach (var e in a.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        string target = Path.GetFullPath(Path.Combine(tmp, e.FullName));
                        if (!target.StartsWith(tmp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var src = e.Open()) using (var dst = File.Create(target)) src.CopyTo(dst);
                    }
                File.WriteAllText(Path.Combine(tmp, ".ok"), hash);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(tmp, dir);
                Log.Write("Unpacked app files to " + dir);
                foreach (string old in Directory.GetDirectories(root))        // files from older versions
                    if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(old, true); } catch { }
            }
            AppDir = dir + Path.DirectorySeparatorChar;
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
            {
                string file = Path.Combine(AppDir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            SetLoaderFolder();
            return true;
        }
        catch (Exception ex) { Log.Write("Unpacking app files failed: " + ex); return false; }
    }

    // Separate method: WebView2 types may only be touched after AssemblyResolve is set up.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void SetLoaderFolder() { Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(AppDir); }
#endif

    public static string FindPages()
    {
        foreach (string rel in new[] { "pages", @"..\..\prototype" })
        {
            string dir = Path.GetFullPath(Path.Combine(AppDir, rel));
            if (File.Exists(Path.Combine(dir, "settings.html"))) return dir;
        }
        return null;
    }

    public static string Missing()
    {
        foreach (string dll in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll" })
            if (!File.Exists(Path.Combine(AppDir, dll))) return dll;
        return FindPages() == null ? "pages" : null;
    }
}

static class Log
{
    public static readonly string DataDir = InstallProfile.DataDir;
    public static readonly string FilePath = Path.Combine(DataDir, "host.log");

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\r\n");
        }
        catch { }
    }
}

// The user's own wallpaper image: copied into %LOCALAPPDATA%\DynamicGlow\wallpaper and served as https://wall.local/<name>
static class WallpaperImage
{
    static readonly string[] Exts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".avif", ".jfif" };

    public static string Dir()
    {
        string d = Path.Combine(Log.DataDir, "wallpaper");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    // Serve https://wall.local/<name> from Dir() with a CORS header, so a canvas that draws the image can still be exported (Share image/video).
    public static void Serve(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("https://wall.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri u;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out u) || u.Host != "wall.local") return;
            string name = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/'));
            string path = Path.Combine(Dir(), name);
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..") ||
                Array.IndexOf(Exts, ext) < 0 || !File.Exists(path))
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Access-Control-Allow-Origin: *");
                return;
            }
            string mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".bmp" ? "image/bmp" :
                ext == ".gif" ? "image/gif" : ext == ".avif" ? "image/avif" : "image/jpeg";
            try
            {
                var ms = new MemoryStream(File.ReadAllBytes(path));
                e.Response = core.Environment.CreateWebResourceResponse(ms, 200, "OK",
                    "Content-Type: " + mime + "\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache");
            }
            catch (Exception ex) { Log.Write("Serving wallpaper image failed: " + ex.Message); }
        };
    }

    public static string Pick(IWin32Window owner)
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "Choose your wallpaper";
            dlg.Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.avif;*.jfif|All files|*.*";
            dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
            return Import(dlg.FileName);
        }
    }

    public static string Import(string src)
    {
        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (Array.IndexOf(Exts, ext) < 0) { MessageBox.Show("Please choose an image file (JPG, PNG, WebP, BMP, GIF, AVIF).", "Dynamic Glow"); return null; }
        return Store(src, ext, "wallpaper-", false);
    }

    // ---- The current Windows desktop wallpaper ----
    // The original file Windows was given (if it still exists), else Windows' own copy (TranscodedWallpaper, which also covers
    // slideshow and Spotlight), else the solid background colour.
    static string Transcoded { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper"); } }

    static string SystemPath()
    {
        var sb = new StringBuilder(1024);
        try { if (Native.SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0)) return sb.ToString(); } catch { }
        try { using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop")) return k == null ? "" : (k.GetValue("WallPaper") as string ?? ""); } catch { return ""; }
    }

    static string Stamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-"; } catch { return "?"; }
    }

    static string BackgroundColor()
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors")) return k == null ? "0 0 0" : (k.GetValue("Background") as string ?? "0 0 0"); } catch { return "0 0 0"; }
    }

    // Changes whenever the visible Windows wallpaper changes; cheap enough to poll.
    public static string WindowsSignature()
    {
        string p = SystemPath();
        return p + "|" + (p != "" ? Stamp(p) : "") + "|" + Stamp(Transcoded) + "|" + (p == "" ? BackgroundColor() : "");
    }

    static string SniffExt(string path)
    {
        try
        {
            var b = new byte[12];
            using (var f = File.OpenRead(path)) if (f.Read(b, 0, 12) < 12) return null;
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == 'B' && b[1] == 'M') return ".bmp";
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return ".gif";
            if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        }
        catch { }
        return null;
    }

    public static string ImportWindows()
    {
        string p = SystemPath();
        if (p != "")
        {
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (File.Exists(p) && Array.IndexOf(Exts, ext) >= 0) return Store(p, ext, "windows-", true);
            if (File.Exists(p) && (ext = SniffExt(p)) != null) return Store(p, ext, "windows-", true);
            string t = Transcoded, te;
            if (File.Exists(t) && (te = SniffExt(t)) != null) return Store(t, te, "windows-", true);
            Log.Write("Windows wallpaper not readable: " + p);
            return null;
        }
        // No picture: Windows shows a solid colour, so the wallpaper is that colour.
        try
        {
            string[] rgb = BackgroundColor().Split(' ');
            var c = Color.FromArgb(int.Parse(rgb[0]), int.Parse(rgb[1]), int.Parse(rgb[2]));
            string tmp = Path.Combine(Path.GetTempPath(), "dynamicglow-color.png");
            using (var bmp = new Bitmap(16, 16)) { using (var g = Graphics.FromImage(bmp)) g.Clear(c); bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png); }
            string name = Store(tmp, ".png", "windows-color-", true);
            try { File.Delete(tmp); } catch { }
            return name;
        }
        catch (Exception ex) { Log.Write("Windows background colour failed: " + ex.Message); return null; }
    }

    static string Store(string src, string ext, string prefix, bool quiet)
    {
        try
        {
            string dir = Dir();
            string name = prefix + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ext;
            File.Copy(src, Path.Combine(dir, name), true);
            foreach (string f in Directory.GetFiles(dir))
                if (!string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
            Log.Write("Wallpaper image: " + src + " -> " + name);
            return name;
        }
        catch (Exception ex)
        {
            Log.Write("Wallpaper image import failed: " + ex.Message);
            if (!quiet) MessageBox.Show("Couldn't use that image:\n" + ex.Message, "Dynamic Glow");
            return null;
        }
    }
}

// 2026-10-10 rename (Habitat Wallpaper → Deskland): existing users keep everything working
//   - The data directory %LOCALAPPDATA%\HabitatWallpaper (settings, autostart records, WebView2 cache) moves wholesale to %LOCALAPPDATA%\Deskland;
//     if the old app is still running and WebView2 files are locked, only the small files such as settings are copied
//   - If autostart is on: the HabitatWallpaper Run entry is replaced with a Deskland entry pointing to Deskland.exe (the user's choice is kept; it is never enabled for users who had it off)
static class Rename
{
    const string OldName = "HabitatWallpaper";
    // Settings from the pre-release name (Dynamic Island) move over once.
    public static void Migrate()
    {
        if (InstallProfile.Isolated) return;
        string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
        try { if (Directory.Exists(oldDir) && !Directory.Exists(Log.DataDir)) { Directory.Move(oldDir, Log.DataDir); Log.Write("Moved data from " + oldDir); } }
        catch (Exception ex) { Log.Write("Moving old data failed: " + ex.Message); }
    }

    static void MigrateLegacy()
    {
        string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);
        try
        {
            if (Directory.Exists(oldDir) && !Directory.Exists(Log.DataDir))
            {
                try { Directory.Move(oldDir, Log.DataDir); }
                catch
                {
                    Directory.CreateDirectory(Log.DataDir);
                    foreach (string src in Directory.GetFiles(oldDir))
                        if (!src.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) File.Copy(src, Path.Combine(Log.DataDir, Path.GetFileName(src)), false);
                }
                Log.Write("Rename: moved data directory from " + oldDir + " to " + Log.DataDir);
            }
        }
        catch (Exception ex) { Log.Write("Rename: failed to move data directory: " + ex.Message); }
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(HostContext.RunKey, true))
            {
                if (k != null && k.GetValue(OldName) != null)
                {
                    k.DeleteValue(OldName, false);
                    k.SetValue(HostContext.RunName, "\"" + Application.ExecutablePath + "\" --autostart");
                    Log.Write("Rename: replaced autostart entry " + OldName + " with " + HostContext.RunName);
                }
            }
        }
        catch (Exception ex) { Log.Write("Rename: failed to replace autostart entry: " + ex.Message); }
    }
}

// Detect whether autostart was blocked (e.g. disabled by a security suite's "startup manager": the registry entry remains but is not allowed to run at logon):
// record the time on every --autostart launch; when the user opens the app manually with autostart on and more than 3 minutes since logon,
// but there is no autostart record after this logon → the logon launch did not run
static class AutoStartCheck
{
    static string LastPath { get { return Path.Combine(Log.DataDir, "autostart.last"); } }
    static string EnabledPath { get { return Path.Combine(Log.DataDir, "autostart.enabled"); } }

    static void Stamp(string path)
    {
        try { Directory.CreateDirectory(Log.DataDir); File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString()); } catch { }
    }
    static DateTime ReadStamp(string path)
    {
        try { return new DateTime(long.Parse(File.ReadAllText(path).Trim()), DateTimeKind.Utc); } catch { return DateTime.MinValue; }
    }
    public static void MarkLaunched() { Stamp(LastPath); }
    public static void MarkEnabled() { Stamp(EnabledPath); }

    // Time of the current logon (UTC): LogonTime from WTSQuerySessionInformation(WTSSessionInfo)
    public static DateTime LogonTimeUtc()
    {
        IntPtr buf; int bytes;
        if (!Native.WTSQuerySessionInformation(IntPtr.Zero, -1, 24, out buf, out bytes)) return DateTime.MinValue;
        try { return bytes >= 216 ? DateTime.FromFileTimeUtc(Marshal.ReadInt64(buf, 200)) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
        finally { Native.WTSFreeMemory(buf); }
    }

    // Running security software (only named in the notice so the user knows where to change it)
    public static string SecuritySoftware()
    {
        string[,] known = { { "HipsTray", "Huorong" }, { "HipsDaemon", "Huorong" }, { "360Tray", "360 Total Security" }, { "360sd", "360 Antivirus" },
                            { "QQPCTray", "Tencent PC Manager" }, { "kxetray", "Kingsoft Antivirus" }, { "2345SafeTray", "2345 Security" } };
        for (int i = 0; i < known.GetLength(0); i++)
            if (Process.GetProcessesByName(known[i, 0]).Length > 0) return known[i, 1];
        return "";
    }

    // Returns null if fine; if blocked, returns the running security software name ("" if unrecognized); the settings page builds the localized notice
    public static string Detect(bool autoStartOn)
    {
        if (!autoStartOn) return null;
        DateTime logon = LogonTimeUtc();
        if (logon == DateTime.MinValue || (DateTime.UtcNow - logon).TotalMinutes < 3) return null;   // Just logged on: startup entries may not have run yet
        if (ReadStamp(EnabledPath) > logon) return null;                                           // Autostart was enabled after this logon
        if (ReadStamp(LastPath) > logon) return null;                                              // Autostart ran at this logon
        string sw = SecuritySoftware();
        Log.Write("Autostart did not run at this logon (logged on at " + logon.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + ")" + (sw != "" ? "; running security software: " + sw : ""));
        return sw;
    }
}

static class Json
{
    static readonly JavaScriptSerializer S = new JavaScriptSerializer();
    public static object Parse(string s) { return S.DeserializeObject(s); }
    public static string Stringify(object o) { return S.Serialize(o); }
    public static Dictionary<string, object> Obj(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
}

// User settings: current theme + per-theme parameters (only changed values are stored; others use theme defaults)
// Stored in %LOCALAPPDATA%\DynamicGlow\settings.json
static class Settings
{
    static readonly string FilePath = Path.Combine(Log.DataDir, "settings.json");
    public static string Theme = "custom";
    public static string Lang = "auto";                // UI language: "auto" (follow system) or one of I18n.Supported
    public static Dictionary<string, Dictionary<string, object>> Params = new Dictionary<string, Dictionary<string, object>>();

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var root = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null) return;
            object v;
            if (root.TryGetValue("theme", out v) && v is string) Theme = (string)v;
            if (root.TryGetValue("lang", out v) && v is string) Lang = (string)v;
            if (root.TryGetValue("params", out v) && v is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)v)
                {
                    var d = kv.Value as Dictionary<string, object>;
                    if (d != null) Params[kv.Key] = d;
                }
            Log.Write("Settings loaded: theme " + Theme);
        }
        catch (Exception ex) { Log.Write("Failed to read settings (using defaults): " + ex.Message); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.DataDir);
            File.WriteAllText(FilePath, Json.Stringify(Json.Obj("version", 1, "theme", Theme, "lang", Lang, "params", Params)), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Write("Failed to save settings: " + ex.Message); }
    }

    public static Dictionary<string, object> For(string theme)
    {
        Dictionary<string, object> d;
        if (!Params.TryGetValue(theme, out d)) Params[theme] = d = new Dictionary<string, object>();
        return d;
    }
}

// UI language (2026-10-14): string tables live in prototype/i18n/<lang>.json — ui for the settings page, host for the tray menu and dialogs, themes for theme names and parameters (the Simplified Chinese theme text is themes.json itself).
// The "Language" setting defaults to "Follow system": use the Windows UI language; unsupported languages fall back to English
static class I18n
{
    // When adding a language, also update LANGS in the settings page and the preview-edition export language list.
    public static readonly string[] Supported = { "en" };
    public static string Code = "en", Sys = "en";     // English only
    static Dictionary<string, object> cur, en, zh;

    public static string FromCulture(System.Globalization.CultureInfo c) { return "en"; }

    public static void Load(string pageDir, string setting)
    {
        Sys = Code = "en";
        en = cur = Read(pageDir, "en");
        zh = null;
    }

    static Dictionary<string, object> Read(string pageDir, string code)
    {
        try
        {
            var root = Json.Parse(File.ReadAllText(Path.Combine(Path.Combine(pageDir, "i18n"), code + ".json"), Encoding.UTF8)) as Dictionary<string, object>;
            object h;
            return root != null && root.TryGetValue("host", out h) ? h as Dictionary<string, object> : null;
        }
        catch (Exception ex) { Log.Write("Failed to read language file (" + code + "): " + ex.Message); return null; }
    }

    // Look up a string: current language → English → Simplified Chinese; {name} is replaced with the paired values that follow
    public static string T(string key, params string[] kv)
    {
        object v = null;
        bool found = (cur != null && cur.TryGetValue(key, out v)) || (en != null && en.TryGetValue(key, out v)) || (zh != null && zh.TryGetValue(key, out v));
        string str = found ? (v as string ?? key) : key;
        for (int i = 0; i + 1 < kv.Length; i += 2) str = str.Replace("{" + kv[i] + "}", kv[i + 1]);
        return str;
    }
}

// Application lifetime: the tray icon stays resident; the wallpaper window is recreated whenever it disappears
// Open the menu: left / right click the tray icon, hotkey Ctrl+Alt+H (or --menu)
// Open settings: launch the app (starts it first if not running), double-click the tray icon, menu → Settings… (or --settings)
class HostContext : ApplicationContext
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "DynamicGlow";
    static readonly MethodInfo ShowTrayMenu =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    readonly string pageDir;
    readonly NotifyIcon tray;
    readonly MenuItem pauseItem, autoStartItem, settingsItem, reloadItem, exitItem;
    readonly System.Windows.Forms.Timer watch;
    readonly EventWaitHandle exitEvent, menuEvent, settingsEvent;
    readonly HotkeyWindow hotkey = new HotkeyWindow();
    readonly Queue<DateTime> restarts = new Queue<DateTime>();
    WallpaperForm form, dead;
    SettingsForm settingsWin;
    DateTime nextStart = DateTime.MaxValue, saveDue = DateTime.MaxValue;
    bool manualPause, exiting;
    string autoStartSw;                                 // Autostart did not run (checked only on manual launch): running security software name ("" if unrecognized); null if fine
    string updateVersion, updateUrl, updateStatus = "", updateError = "";
    double updateProgress = -1;                         // -1 = no percentage to show for the current stage

    public HostContext(string pageDir, bool showSettings)
    {
        this.pageDir = pageDir;
        Log.Write("Starting, page directory: " + pageDir + (showSettings ? "" : " (autostart)"));
        if (showSettings) autoStartSw = AutoStartCheck.Detect(IsAutoStart());
        Settings.Load();
        I18n.Load(pageDir, Settings.Lang);
        exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        menuEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.MenuEventName);
        settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.SettingsEventName);

        // Hotkey Ctrl+Alt+H: show the menu at the cursor
        bool hotkeyOk = hotkey.Register(Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Keys.H);
        hotkey.Pressed += delegate { ShowMenu(); };
        Log.Write(hotkeyOk ? "Hotkey Ctrl+Alt+H registered" : "Failed to register hotkey Ctrl+Alt+H (possibly in use by another program)");

        pauseItem = new MenuItem(I18n.T("trayPause"), delegate
        {
            manualPause = !manualPause;
            pauseItem.Checked = manualPause;
            if (form != null) form.SetManualPause(manualPause);
        });
        autoStartItem = new MenuItem(I18n.T("trayAutostart"), delegate { SetAutoStart(!autoStartItem.Checked); });
        autoStartItem.Checked = IsAutoStart();
        if (autoStartItem.Checked && AutoStartValue() != "")
        {
            try
            {
                if (CreateAutoStartShortcut())
                {
                    DeleteRunAutoStart();
                    Log.Write("Autostart migrated to the current user's Startup folder");
                }
                else if (AutoStartValue().IndexOf("--autostart") < 0)
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))   // Add the missing argument to the fallback entry
                        k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --autostart");
            }
            catch (Exception ex) { Log.Write("Failed to migrate autostart, keeping the original entry: " + ex.Message); }
        }
        settingsItem = new MenuItem(I18n.T("traySettings"), delegate { OpenSettings(); });
        settingsItem.DefaultItem = true;                // Bold: main entry point

        tray = new NotifyIcon();
        tray.Icon = AppIcon.Create();
        tray.Text = "Dynamic Glow";
        tray.ContextMenu = new ContextMenu(new[] {
            settingsItem,
            pauseItem,
            reloadItem = new MenuItem(I18n.T("trayReload"), delegate { if (form != null) form.Reload(); }),
            autoStartItem,
            new MenuItem("-"),
            exitItem = new MenuItem(I18n.T("trayExit"), delegate { Exit(); }),
        });
        tray.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMenu(); };
        tray.DoubleClick += delegate { OpenSettings(); };   // Double-click the tray icon: open settings
        tray.Visible = true;
        ShowWelcomeOnce(hotkeyOk);

        watch = new System.Windows.Forms.Timer();
        watch.Interval = 500;
        watch.Tick += delegate { Watch(); };
        watch.Start();
        SyncWindowsWallpaper(true);
        wallTimer = new System.Windows.Forms.Timer();
        wallTimer.Interval = 3000;
        wallTimer.Tick += delegate { SyncWindowsWallpaper(false); };
        wallTimer.Start();
        StartForm();
        if (showSettings) OpenSettings();                // Launched by the user: open settings
    }

    // UI language changed: update the tray menu text
    void ApplyTrayTexts()
    {
        settingsItem.Text = I18n.T("traySettings");
        pauseItem.Text = I18n.T("trayPause");
        reloadItem.Text = I18n.T("trayReload");
        autoStartItem.Text = I18n.T("trayAutostart");
        exitItem.Text = I18n.T("trayExit");
    }

    // ---------------------------------------------------------------- Settings window
    public void OpenSettings()
    {
        if (exiting) return;
        if (settingsWin == null || settingsWin.IsDisposed)
        {
            settingsWin = new SettingsForm(pageDir, this);
            settingsWin.FormClosed += delegate { settingsWin = null; };
            Log.Write("Opening settings window");
        }
        if (settingsWin.WindowState == FormWindowState.Minimized) settingsWin.WindowState = FormWindowState.Normal;
        settingsWin.Show();
        // When launched externally with SW_HIDE, WinForms may already report Visible = true
        // while the native window stays hidden; Activate / Show again do not fix it, so restore explicitly.
        // SW_RESTORE handles both hidden and minimized; SW_SHOW does not restore a minimized window.
        Native.ShowWindow(settingsWin.Handle, Native.SW_RESTORE);
        settingsWin.Activate();
    }

    string SettingsMessage()
    {
        return Json.Stringify(Json.Obj("t", "settings", "v", Settings.For(Settings.Theme)));
    }

    string UpdateStateMessage()
    {
        return Json.Stringify(Json.Obj("t", "updateState", "version", updateVersion,
            "status", updateStatus, "progress", updateProgress, "error", updateError));
    }

    void PostUpdateState()
    {
        if (settingsWin != null) settingsWin.Post(UpdateStateMessage());
    }

    // Only for development or non-Store update sources; the Store edition always uses StoreContext and never accepts URLs from the page.
    public void OfferUpdate(string version, string url)
    {
        Uri uri;
        if (string.IsNullOrWhiteSpace(version) || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Update info requires a version and a valid HTTPS URL");
        updateVersion = version.Trim();
        updateUrl = uri.AbsoluteUri;
        updateStatus = "available";
        updateProgress = -1;
        updateError = "";
        PostUpdateState();
    }

    void SaveSoon() { saveDue = DateTime.Now.AddMilliseconds(600); }

    // By default the wallpaper page shows whatever the Windows desktop wallpaper is, and follows it when it changes.
    // Choosing an image in settings switches source to "file" until the user goes back to the Windows wallpaper.
    System.Windows.Forms.Timer wallTimer;
    string wallSig;
    void SyncWindowsWallpaper(bool force)
    {
        object src;
        Settings.For("custom").TryGetValue("source", out src);
        if ((src as string) == "file") return;
        string sig = WallpaperImage.WindowsSignature();
        if (!force && sig == wallSig) return;
        wallSig = sig;
        string name = WallpaperImage.ImportWindows();
        if (name != null) ApplyImage("custom", name, "windows");
    }

    void ApplyImage(string theme, string name, string source)
    {
        var d = Settings.For(theme);
        d["image"] = name;
        d["source"] = source;
        if (theme == Settings.Theme && form != null)
            form.Post(Json.Stringify(Json.Obj("t", "settings", "v", Json.Obj("image", name))));
        SaveSoon();
        if (settingsWin != null) settingsWin.Post(Json.Stringify(Json.Obj("t", "imagePicked", "name", name, "source", source)));
    }

    bool ThemeAllowed(string id)
    {
        return Themes.Allowed(pageDir, id, delegate(string product) { return false; });
    }

    // Messages from the settings page:hello / set / reset / theme / autostart / lang / updateProgram
    public void OnSettingsMessage(string s)
    {
        var m = Json.Parse(s) as Dictionary<string, object>;
        if (m == null || settingsWin == null) return;
        object t;
        m.TryGetValue("t", out t);
        switch (t as string)
        {
            case "hello":
                Rectangle scr = Screen.PrimaryScreen.Bounds;
                settingsWin.Post(Json.Stringify(Json.Obj("t", "init", "theme", Settings.Theme, "params", Settings.Params,
                    "screen", Json.Obj("w", scr.Width, "h", scr.Height), "autostart", IsAutoStart(),
                    "autostartWarn", autoStartSw != null, "autostartSw", autoStartSw ?? "",
                    "lang", Settings.Lang, "langEff", I18n.Code, "langSys", I18n.Sys)));
                if (updateVersion != null) PostUpdateState();
                if (form != null && form.LastIcons != "") settingsWin.Post(form.LastIcons);
                break;
            case "set":
            {
                string theme = m["theme"] as string, key = m["key"] as string;
                if (theme == null || key == null) return;
                Settings.For(theme)[key] = m["value"];
                if (theme == Settings.Theme && form != null)
                    form.Post(Json.Stringify(Json.Obj("t", "settings", "v", Json.Obj(key, m["value"]))));
                SaveSoon();
                break;
            }
            case "reset":
            {
                string theme = m["theme"] as string;
                if (theme == null) return;
                object keepImage = null, keepSource = null;
                Dictionary<string, object> old;
                if (Settings.Params.TryGetValue(theme, out old)) { old.TryGetValue("image", out keepImage); old.TryGetValue("source", out keepSource); }
                Settings.Params.Remove(theme);
                if (keepImage != null) Settings.For(theme)["image"] = keepImage;   // resetting sliders never forgets the chosen image
                if (keepSource != null) Settings.For(theme)["source"] = keepSource;
                if (theme == Settings.Theme && form != null) form.Post("{\"t\":\"reset\"}");
                SaveSoon();
                Log.Write("Reset to defaults: " + theme);
                break;
            }
            case "pickImage":
            {
                string theme = m.ContainsKey("theme") ? m["theme"] as string : null;
                if (theme == null || !ThemeAllowed(theme)) return;
                var win = settingsWin;
                win.BeginInvoke((MethodInvoker)delegate
                {
                    string name = WallpaperImage.Pick(win);
                    if (name == null) return;
                    ApplyImage(theme, name, "file");
                });
                break;
            }
            case "useWindowsWallpaper":
            {
                Settings.For("custom")["source"] = "windows";
                SyncWindowsWallpaper(true);
                break;
            }
            case "theme":
            {
                string id = m["id"] as string;
                if (id == null || !ThemeAllowed(id))
                {
                    settingsWin.Post(Json.Stringify(Json.Obj("t", "themeChanged", "id", Settings.Theme)));
                    Log.Write("Rejected switch to unlicensed or unknown theme: " + (id ?? "null"));
                    return;
                }
                string page = Themes.PageFor(pageDir, id);
                if (page == null || id == Settings.Theme) return;
                Settings.Theme = id;
                SaveSoon();
                if (form != null) form.LoadPage(page);
                Log.Write("Switching theme: " + id);
                break;
            }
            case "autostartWarnSeen":
                autoStartSw = null;
                break;
            case "lang":                                    // Change UI language: save it, update the tray menu, tell the settings page which one is in effect
            {
                object v;
                if (!m.TryGetValue("v", out v) || !(v is string)) return;
                Settings.Lang = (string)v;
                SaveSoon();
                I18n.Load(pageDir, Settings.Lang);
                ApplyTrayTexts();
                settingsWin.Post(Json.Stringify(Json.Obj("t", "lang", "v", Settings.Lang, "eff", I18n.Code, "sys", I18n.Sys)));
                break;
            }
            case "share":
                if (form == null) { PostShareResult(false, null, I18n.T("errNotReady")); break; }
                form.RequestSnapshot(OnShareImage);
                break;
            case "shareVideo":
                if (form == null) { PostShareResult(false, null, I18n.T("errNotReady")); break; }
                form.RequestRecording(OnShareVideo);
                break;
            case "openShare":
                if (lastShare != null && File.Exists(lastShare)) Process.Start(lastShare);
                break;
            case "openWebsite":
                Process.Start("https://github.com/BLEZZ0102/Deskland");
                break;
            case "revealShare":
                if (lastShare != null && File.Exists(lastShare)) Process.Start("explorer.exe", "/select,\"" + lastShare + "\"");
                break;
            case "updateProgram":
                if (updateUrl != null) Process.Start(updateUrl);
                break;
            case "autostart":
                SetAutoStart(m["v"] is bool && (bool)m["v"]);
                settingsWin.Post(Json.Stringify(Json.Obj("t", "autostart", "v", IsAutoStart())));
                break;
        }
    }

    // ---- One-click share: image rendered by the wallpaper page → saved to "Pictures\Deskland", copied to the clipboard, thumbnail sent back to settings ----
    string lastShare;                                   // Path of the latest share image (the settings page may only ask us to open this one)
    void OnShareImage(string thumb, string png, string err)
    {
        if (err != null || png == null) { PostShareResult(false, null, err ?? I18n.T("errNoImage")); Log.Write("Failed to create share image: " + err); return; }
        try
        {
            int comma = png.IndexOf(',');
            byte[] bytes = Convert.FromBase64String(comma >= 0 ? png.Substring(comma + 1) : png);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Dynamic Glow");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "Dynamic Glow " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png");
            File.WriteAllBytes(path, bytes);
            lastShare = path;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                {
                    var data = new DataObject();
                    data.SetImage(new Bitmap(img));                       // Most programs (chat apps etc.) accept this
                    data.SetData("PNG", false, new MemoryStream(bytes));  // PNG-aware programs (browsers, Office) use this; sharper
                    Clipboard.SetDataObject(data, true);
                }
            }
            catch (Exception ex) { Log.Write("Failed to copy share image to clipboard: " + ex.Message); }
            Log.Write("Share image created: " + path);
            if (settingsWin != null)
                settingsWin.Post(Json.Stringify(Json.Obj("t", "shareResult", "ok", true, "thumb", thumb, "path", path)));
        }
        catch (Exception ex)
        {
            Log.Write("Failed to save share image: " + ex);
            PostShareResult(false, null, ex.Message);
        }
    }
    void PostShareResult(bool ok, string path, string msg)
    {
        if (settingsWin != null) settingsWin.Post(Json.Stringify(Json.Obj("t", "shareResult", "ok", ok, "path", path, "msg", msg)));
    }
    // Share animation: saved as MP4 (WebM if unsupported), file placed on the clipboard (paste into a chat window to send it),
    // the settings page plays it directly via https://share.local/<file name>
    void OnShareVideo(string mime, string poster, string video, string err)
    {
        if (err != null || video == null) { PostShareResult(false, null, err ?? I18n.T("errNoVideo")); Log.Write("Failed to record share animation: " + err); return; }
        try
        {
            int comma = video.IndexOf(',');
            byte[] bytes = Convert.FromBase64String(comma >= 0 ? video.Substring(comma + 1) : video);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Dynamic Glow");
            Directory.CreateDirectory(dir);
            string name = "Dynamic Glow " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + (mime == "video/webm" ? ".webm" : ".mp4");
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            lastShare = path;
            try
            {
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(path);
                Clipboard.SetFileDropList(files);
            }
            catch (Exception ex) { Log.Write("Failed to copy share animation to clipboard: " + ex.Message); }
            Log.Write("Share animation recorded: " + path + " (" + (bytes.Length / 1024) + " KB)");
            if (settingsWin != null)
            {
                settingsWin.MapShareFolder(dir);
                settingsWin.Post(Json.Stringify(Json.Obj("t", "shareResult", "ok", true, "kind", "video", "thumb", poster, "path", path,
                    "url", "https://share.local/" + Uri.EscapeDataString(name))));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Failed to save share animation: " + ex);
            PostShareResult(false, null, ex.Message);
        }
    }

    // Show the tray menu at the cursor (NotifyIcon's built-in logic handles the foreground window so clicking elsewhere closes the menu)
    void ShowMenu()
    {
        if (exiting) return;
        autoStartItem.Checked = IsAutoStart();
        if (ShowTrayMenu != null) ShowTrayMenu.Invoke(tray, null);
    }

    // On first run, show a system notification telling the user where the menu is
    void ShowWelcomeOnce(bool hotkeyOk)
    {
        try
        {
            string flag = Path.Combine(Log.DataDir, "welcome.shown");
            if (File.Exists(flag)) return;
            Directory.CreateDirectory(Log.DataDir);
            tray.ShowBalloonTip(10000, I18n.T("welcomeTitle"),
                I18n.T("welcomeBody", "hotkey", hotkeyOk ? I18n.T("welcomeHotkey") : ""), ToolTipIcon.Info);
            File.WriteAllText(flag, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex) { Log.Write("First-run notification failed: " + ex.Message); }
    }

    // Autostart: prefer a shortcut in the current user's Startup folder; legacy Run entries are migrated at launch.
    // Scheduled tasks are not used since they need extra permissions; if creating the shortcut fails, the Run entry is kept as a fallback.
    static string AutoStartShortcutPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Dynamic Glow.lnk"); }
    }

    static object OpenAutoStartShortcut()
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        try
        {
            return shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { AutoStartShortcutPath });
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static bool AutoStartShortcutEnabled()
    {
        if (!File.Exists(AutoStartShortcutPath)) return false;
        object link = null;
        try
        {
            link = OpenAutoStartShortcut();
            string target = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty,
                null, link, null) as string;
            string arguments = link.GetType().InvokeMember("Arguments", BindingFlags.GetProperty,
                null, link, null) as string;
            return string.Equals(target, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && arguments != null && arguments.IndexOf("--autostart", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); }
    }

    static bool CreateAutoStartShortcut()
    {
        object link = null;
        try
        {
            link = OpenAutoStartShortcut();
            link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty,
                null, link, new object[] { Application.ExecutablePath });
            link.GetType().InvokeMember("Arguments", BindingFlags.SetProperty,
                null, link, new object[] { "--autostart" });
            link.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty,
                null, link, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
            link.GetType().InvokeMember("IconLocation", BindingFlags.SetProperty,
                null, link, new object[] { Application.ExecutablePath + ",0" });
            link.GetType().InvokeMember("Description", BindingFlags.SetProperty,
                null, link, new object[] { "Dynamic Glow" });
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            return File.Exists(AutoStartShortcutPath);
        }
        catch (Exception ex) { Log.Write("Failed to create Startup folder shortcut: " + ex.Message); return false; }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); }
    }

    static string AutoStartValue()
    {
        if (InstallProfile.Isolated) return "";
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
        {
            string v = k == null ? null : k.GetValue(RunName) as string;
            return v ?? "";
        }
    }

    static void DeleteRunAutoStart()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (k != null) k.DeleteValue(RunName, false);
    }

    static bool IsAutoStart()
    {
        return AutoStartShortcutEnabled()
            || AutoStartValue().IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void SetAutoStart(bool on)
    {
        if (InstallProfile.Isolated) return;
        try
        {
            if (on)
            {
                bool shortcut = CreateAutoStartShortcut();
                if (shortcut) DeleteRunAutoStart();
                else
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                        k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --autostart");
                AutoStartCheck.MarkEnabled();
                Log.Write(shortcut ? "Autostart enabled (current user Startup folder)" : "Autostart enabled (legacy Run entry fallback)");
            }
            else
            {
                DeleteRunAutoStart();
                if (File.Exists(AutoStartShortcutPath)) File.Delete(AutoStartShortcutPath);
                Log.Write(IsAutoStart() ? "Failed to disable autostart; the entry still exists" : "Autostart disabled");
            }
        }
        catch (Exception ex) { Log.Write("Failed to set autostart: " + ex.Message); }
        autoStartItem.Checked = IsAutoStart();
    }

    void StartForm()
    {
        nextStart = DateTime.MaxValue;
        if (!ThemeAllowed(Settings.Theme)) Settings.Theme = "custom";
        string page = ThemeAllowed(Settings.Theme) ? Themes.PageFor(pageDir, Settings.Theme) : null;
        if (page == null)
        {
            Log.Write("No licensed theme available to load, retrying in 5 seconds");
            nextStart = DateTime.Now.AddSeconds(5);
            return;
        }
        form = new WallpaperForm(pageDir, page, manualPause);
        form.SettingsJson = SettingsMessage;
        form.IconsChanged += delegate(string json) { if (settingsWin != null) settingsWin.Post(json); };
        form.RecordProgress += delegate(string p) { if (settingsWin != null) settingsWin.Post("{\"t\":\"recProgress\",\"p\":" + p + "}"); };
        form.HandleDestroyed += OnFormGone;
        form.Show();
    }

    void OnFormGone(object sender, EventArgs e)
    {
        var f = (WallpaperForm)sender;
        if (f != form) return;
        form = null;
        dead = f;                                        // Disposed later in the timer to avoid reentrancy in the destroy callback
        if (exiting) return;
        if (f.Fatal) { Exit(); return; }

        // Destroyed repeatedly within 1 minute: stop retrying to avoid fighting Explorer
        restarts.Enqueue(DateTime.Now);
        while (restarts.Count > 0 && (DateTime.Now - restarts.Peek()).TotalSeconds > 60) restarts.Dequeue();
        if (restarts.Count > 5)
        {
            Log.Write("Wallpaper window destroyed more than 5 times within 1 minute, giving up");
            MessageBox.Show(I18n.T("errRebuilt") + "\n\n" + I18n.T("errSeeLog", "path", Log.FilePath), "Dynamic Glow");
            Exit();
            return;
        }
        Log.Write("Wallpaper window destroyed (Explorer rebuilt the wallpaper layer), reattaching in 1 second");
        nextStart = DateTime.Now.AddSeconds(1);
    }

    void Watch()
    {
        if (dead != null) { try { dead.Dispose(); } catch { } dead = null; }
        if (exitEvent.WaitOne(0)) { Exit(); return; }
        if (menuEvent.WaitOne(0)) ShowMenu();
        if (settingsEvent.WaitOne(0)) OpenSettings();
        if (DateTime.Now >= saveDue) { saveDue = DateTime.MaxValue; Settings.Save(); }
        if (form == null && !exiting && DateTime.Now >= nextStart) StartForm();
    }

    void Exit()
    {
        if (exiting) return;
        exiting = true;
        watch.Stop();
        if (wallTimer != null) wallTimer.Stop();
        if (saveDue != DateTime.MaxValue) Settings.Save();
        if (settingsWin != null) { try { settingsWin.Close(); } catch { } }
        hotkey.Dispose();
        tray.Visible = false;
        if (form != null) form.Shutdown();
        Log.Write("Exiting");
        ExitThread();
    }
}

// Hidden window that receives the global hotkey (message-only, never shown)
class HotkeyWindow : NativeWindow, IDisposable
{
    public event EventHandler Pressed;
    bool registered;

    public HotkeyWindow()
    {
        var cp = new CreateParams();
        cp.Parent = new IntPtr(-3);                     // HWND_MESSAGE: message-only window
        CreateHandle(cp);
    }

    public bool Register(uint modifiers, Keys key)
    {
        registered = Native.RegisterHotKey(Handle, 1, modifiers, (uint)key);
        return registered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /* WM_HOTKEY */ && Pressed != null) Pressed(this, EventArgs.Empty);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (registered) Native.UnregisterHotKey(Handle, 1);
        registered = false;
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

// The tray and settings window share the Deskland icon; the build script copies it to the program directory.
static class AppIcon
{
    public static Icon Create()
    {
        string ico = Path.Combine(AppFiles.AppDir, "DynamicGlow.ico");
        return File.Exists(ico) ? new Icon(ico) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// Settings window: a regular desktop window showing prototype/settings.html in WebView2
class SettingsForm : Form
{
    readonly WebView2 web;
    readonly HostContext host;
    readonly string pageDir;

    public SettingsForm(string pageDir, HostContext host)
    {
        this.pageDir = pageDir;
        this.host = host;
        Text = "Dynamic Glow";
        AccessibleName = "Dynamic Glow";
        ShowIcon = true;
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        float s = Native.GetDpiForSystem() / 96f;       // The process works in physical pixels, so scale the window size ourselves
        ClientSize = new Size((int)(1120 * s), (int)(800 * s));     // Themes in three columns on the left; preview above parameters on the right
        MinimumSize = new Size((int)(940 * s), (int)(680 * s));
        BackColor = Color.White;

        web = new WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = BackColor;
        web.CreationProperties = new CoreWebView2CreationProperties();
        web.CreationProperties.UserDataFolder = Path.Combine(Log.DataDir, "WebView2");
        Controls.Add(web);
    }

    // Paint the native title bar to match the page background: settings.html follows the system light/dark mode (light #ffffff / dark #171717),
    // so the title bar uses the same color for the system mode. DWMWA_CAPTION_COLOR / dark mode need Win11; on older systems the call fails and the default is kept
    void ThemeTitleBar()
    {
        bool light = true;
        try
        {
            object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            light = v == null || Convert.ToInt32(v) != 0;
        }
        catch { }
        int dark = light ? 0 : 1;
        int color = light ? 0x00FFFFFF : 0x00171717;      // COLORREF is 0x00BBGGRR
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE: title text and buttons follow light/dark
        Native.DwmSetWindowAttribute(Handle, 35, ref color, 4);   // DWMWA_CAPTION_COLOR
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ThemeTitleBar(); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x001A /* WM_SETTINGCHANGE */) ThemeTitleBar();   // Update the title bar when the system switches light/dark
        base.WndProc(ref m);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await web.EnsureCoreWebView2Async(null);
            if (IsDisposed) return;
            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("app.local", pageDir, CoreWebView2HostResourceAccessKind.Allow);
            WallpaperImage.Serve(core);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += delegate(object s, CoreWebView2WebMessageReceivedEventArgs a)
            {
                try { host.OnSettingsMessage(a.TryGetWebMessageAsString()); }
                catch (Exception ex) { Log.Write("Failed to handle settings message: " + ex.Message); }
            };
            string settingsUrl = "https://app.local/settings.html?v=" + Uri.EscapeDataString(Application.ProductVersion);
            Log.Write("Settings page: " + settingsUrl);
            core.Navigate(settingsUrl);
        }
        catch (Exception ex)
        {
            Log.Write("Settings window failed to start: " + ex);
            MessageBox.Show(I18n.T("errSettings", "msg", ex.Message), "Dynamic Glow");
            Close();
        }
    }

    public void Post(string json)
    {
        // The settings window is transient: if its WebView2 is dead, skip sending (reopening recovers it) rather than throwing back into the wallpaper polling
        try { if (!IsDisposed && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(json); }
        catch { }
    }

    // Folder holding shared images / animations: mapped to https://share.local/ so the settings page can show and play them directly
    bool shareMapped;
    public void MapShareFolder(string dir)
    {
        if (shareMapped || IsDisposed || web.CoreWebView2 == null) return;
        web.CoreWebView2.SetVirtualHostNameToFolderMapping("share.local", dir, CoreWebView2HostResourceAccessKind.Allow);
        shareMapped = true;
    }
}

class WallpaperForm : Form
{
    readonly WebView2 web;
    readonly System.Windows.Forms.Timer iconTimer, fgTimer;
    readonly string pageDir;
    readonly IconReader reader = new IconReader();
    // Icons are re-read only when Explorer reports a change on the icon list (plus a slow safety poll):
    // each accessibility query is a cross-process call, too costly to repeat for every icon several times a second.
    IntPtr iconHook, hookedListView;
    Native.WinEventDelegate iconEventProc;
    bool iconsDirty = true;
    int lastIconRead;
    Rectangle screen;
    IntPtr parentHwnd, insertAfter;
    string lastIcons = "";
    int lastPause = -1;
    bool manualPause, shuttingDown;

    public bool Fatal { get; private set; }

    string page;                                        // Current theme page (page in themes.json)
    public Func<string> SettingsJson;                   // Settings message sent first when the page is ready
    public event Action<string> IconsChanged;           // Forwarded to the settings preview when icons change
    public string LastIcons { get { return lastIcons; } }
    protected override bool ShowWithoutActivation { get { return true; } }

    public WallpaperForm(string pageDir, string page, bool manualPause)
    {
        this.pageDir = pageDir;
        this.page = page;
        this.manualPause = manualPause;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(26, 163, 196);
        Text = "DynamicGlowWindow";

        web = new WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = BackColor;
        web.CreationProperties = new CoreWebView2CreationProperties();
        web.CreationProperties.UserDataFolder = Path.Combine(Log.DataDir, "WebView2");
        Controls.Add(web);

        iconTimer = new System.Windows.Forms.Timer();
        iconTimer.Interval = 400;
        iconTimer.Tick += delegate { PollIcons(); };
        fgTimer = new System.Windows.Forms.Timer();
        fgTimer.Interval = 500;
        fgTimer.Tick += delegate { PollForeground(); };
    }

    public void SetManualPause(bool v) { manualPause = v; PollForeground(); }

    // ---- One-click share: ask the page to render an anonymized desktop image (no screenshot, only icon positions) ----
    Action<string, string, string> snapshotDone;           // (thumbnail, full PNG, error)
    System.Windows.Forms.Timer snapshotTimeout;
    public void RequestSnapshot(Action<string, string, string> done)
    {
        if (web.CoreWebView2 == null) { done(null, null, I18n.T("errNotReady")); return; }
        if (snapshotDone != null) { done(null, null, I18n.T("errBusyImage")); return; }
        snapshotDone = done;
        snapshotTimeout = new System.Windows.Forms.Timer();
        snapshotTimeout.Interval = 20000;
        snapshotTimeout.Tick += delegate { FinishSnapshot(null, null, I18n.T("errImageTimeout")); };
        snapshotTimeout.Start();
        try { web.CoreWebView2.PostWebMessageAsString("{\"t\":\"snapshot\"}"); }
        catch (InvalidOperationException ex) { FinishSnapshot(null, null, I18n.T("errNotReady")); WebDead(ex); }
        catch (COMException ex) { FinishSnapshot(null, null, I18n.T("errNotReady")); WebDead(ex); }
    }
    void OnSnapshot(string msg)
    {
        string[] p = msg.Split(new[] { '|' }, 3);
        if (p.Length < 3) FinishSnapshot(null, null, I18n.T("errBadData"));
        else if (p[1] == "error") FinishSnapshot(null, null, p[2]);
        else FinishSnapshot(p[1], p[2], null);
    }
    void FinishSnapshot(string thumb, string png, string err)
    {
        if (snapshotTimeout != null) { snapshotTimeout.Stop(); snapshotTimeout.Dispose(); snapshotTimeout = null; }
        var done = snapshotDone;
        snapshotDone = null;
        if (done != null) done(thumb, png, err);
    }

    // ---- Share animation: record the theme animation with the current icon layout, report progress, then send "record|format|poster|video" ----
    Action<string, string, string, string> recordDone;    // (format, poster, video, error)
    System.Windows.Forms.Timer recordTimeout;
    public event Action<string> RecordProgress;            // 0..1 (string)
    public void RequestRecording(Action<string, string, string, string> done)
    {
        if (web.CoreWebView2 == null) { done(null, null, null, I18n.T("errNotReady")); return; }
        if (recordDone != null) { done(null, null, null, I18n.T("errBusyVideo")); return; }
        recordDone = done;
        recordTimeout = new System.Windows.Forms.Timer();
        recordTimeout.Interval = 45000;
        recordTimeout.Tick += delegate { FinishRecord(null, null, null, I18n.T("errVideoTimeout")); };
        recordTimeout.Start();
        try { web.CoreWebView2.PostWebMessageAsString("{\"t\":\"record\"}"); }
        catch (InvalidOperationException ex) { FinishRecord(null, null, null, I18n.T("errNotReady")); WebDead(ex); }
        catch (COMException ex) { FinishRecord(null, null, null, I18n.T("errNotReady")); WebDead(ex); }
    }
    void OnRecord(string msg)
    {
        string[] p = msg.Split(new[] { '|' }, 4);
        if (p.Length >= 3 && p[1] == "error") FinishRecord(null, null, null, msg.Substring(13));
        else if (p.Length < 4) FinishRecord(null, null, null, I18n.T("errBadData"));
        else FinishRecord(p[1], p[2], p[3], null);
    }
    void FinishRecord(string mime, string poster, string video, string err)
    {
        if (recordTimeout != null) { recordTimeout.Stop(); recordTimeout.Dispose(); recordTimeout = null; }
        var done = recordDone;
        recordDone = null;
        if (done != null) done(mime, poster, video, err);
    }
    public void Reload()
    {
        if (web.CoreWebView2 == null) return;
        try { web.CoreWebView2.Reload(); }
        catch (InvalidOperationException ex) { WebDead(ex); }
        catch (COMException ex) { WebDead(ex); }
    }
    public void Post(string json)
    {
        if (web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsString(json); }
        catch (InvalidOperationException ex) { WebDead(ex); }
        catch (COMException ex) { WebDead(ex); }
    }

    // After a background WebView2 runtime update / browser process kill, CoreWebView2 is non-null but disposed and every call throws
    // (null checks do not catch this). Handle it like "wallpaper window destroyed by the system": tear down this window; HostContext recreates it after 1 second
    void WebDead(Exception ex)
    {
        if (shuttingDown) return;
        Log.Write("WebView2 failed (runtime update or process exit), reattaching wallpaper window: " + ex.Message);
        Shutdown();
    }
    string PageUrl { get { return "https://app.local/" + page + "?wallpaper=1"; } }

    // Switch theme: change the page; the new page receives settings and icons again once ready
    public void LoadPage(string newPage)
    {
        page = newPage;
        if (web.CoreWebView2 == null) return;
        try { web.CoreWebView2.Navigate(PageUrl); }
        catch (InvalidOperationException ex) { WebDead(ex); }
        catch (COMException ex) { WebDead(ex); }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            if (!File.Exists(Path.Combine(pageDir, page)))
            {
                Fail(I18n.T("errNoPage", "path", Path.Combine(pageDir, page)));
                return;
            }
            if (!AttachToDesktop())
            {
                Fail(I18n.T("errNoDesktop"));
                return;
            }
            await web.EnsureCoreWebView2Async(null);
            if (shuttingDown || IsDisposed) return;
            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("app.local", pageDir, CoreWebView2HostResourceAccessKind.Allow);
            WallpaperImage.Serve(core);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            // Page ready ("ready"): send settings first, then icons and pause state (the shoreline depends on the offshore distance setting)
            // One-click share: once rendered the page sends "snapshot|thumbnail|full PNG" (both data URLs; the full image is large, so no JSON)
            core.WebMessageReceived += delegate(object s, CoreWebView2WebMessageReceivedEventArgs a)
            {
                string msg = null;
                try { msg = a.TryGetWebMessageAsString(); } catch { }
                if (msg != null && msg.StartsWith("snapshot|")) { OnSnapshot(msg); return; }
                if (msg != null && msg.StartsWith("recprogress|")) { if (RecordProgress != null) RecordProgress(msg.Substring(12)); return; }
                if (msg != null && msg.StartsWith("record|")) { OnRecord(msg); return; }
                if (SettingsJson != null) core.PostWebMessageAsString(SettingsJson());
                lastIcons = ""; lastPause = -1; iconsDirty = true; PollIcons(); PollForeground();
            };
            core.Navigate(PageUrl);
            iconTimer.Start();
            fgTimer.Start();
            Log.Write("WebView2 ready: " + core.Environment.BrowserVersionString);
        }
        catch (Exception ex)
        {
            if (shuttingDown || IsDisposed) return;       // Window destroyed by the system during startup: let HostContext retry
            Log.Write("Startup failed: " + ex);
            Fail(I18n.T("errStart", "msg", ex.Message));
        }
    }

    void Fail(string msg)
    {
        Log.Write(msg);
        MessageBox.Show(msg + "\n\n" + I18n.T("errSeeLog", "path", Log.FilePath), "Dynamic Glow");
        Fatal = true;
        Shutdown();
    }

    // ---------------------------------------------------------------- Attach to the wallpaper layer
    // Two desktop structures:
    //   - Windows 11 24H2+ ("raised desktop"): under Progman are the icon layer (SHELLDLL_DefView) → system wallpaper layer (WorkerW).
    //     The system wallpaper layer is destroyed and rebuilt when the wallpaper changes (slideshow / Spotlight), so instead of attaching inside it we become a
    //     layered child of Progman (WS_EX_LAYERED, alpha 255), placed right below the icon layer and above the system wallpaper layer, and keep that order.
    //     (Progman has no surface for regular child windows to draw on, so it must be layered; layered child windows require app.manifest to declare Win8+ support)
    //   - Older versions: the icon layer is inside a top-level WorkerW, followed by the top-level WorkerW that draws the wallpaper → attach inside that WorkerW
    public static string AttachMode = "progman";
    bool raised;
    IntPtr zAnchor;                                     // Window we stay directly below (icon list / icon layer)
    IntPtr winEventHook;
    Native.WinEventDelegate winEventProc;               // Keep a reference so it is not GC'd

    static IntPtr FindClassicWorker()
    {
        IntPtr host = IntPtr.Zero;
        Native.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { host = h; return false; }
            return true;
        }, IntPtr.Zero);
        return host != IntPtr.Zero ? Native.FindWindowEx(IntPtr.Zero, host, "WorkerW", null) : IntPtr.Zero;
    }

    // Ask Explorer to create the wallpaper layer (send 0xD,1 once only; a following 0xD,0 would delete it again), wait up to 2 seconds
    static void RequestWorkerW(IntPtr progman, Func<bool> ready)
    {
        IntPtr res;
        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out res);
        for (int i = 0; i < 20 && !ready(); i++) Thread.Sleep(100);
    }

    bool AttachToDesktop()
    {
        screen = Screen.PrimaryScreen.Bounds;          // The process is DPI-aware → physical pixels
        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { Log.Write("Progman not found"); return false; }
        IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        // Before 24H2 the icon layer also starts inside Progman, and asking for the wallpaper layer moves it into a top-level WorkerW.
        // So create the layer first, then decide which structure this is.
        if (defView != IntPtr.Zero && Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) == IntPtr.Zero)
        {
            RequestWorkerW(progman, delegate
            {
                return Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) != IntPtr.Zero ||
                       Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero;
            });
            defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        string mode;
        raised = defView != IntPtr.Zero;

        if (raised)
        {
            if (Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) == IntPtr.Zero)
                RequestWorkerW(progman, delegate { return Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) != IntPtr.Zero; });
            IntPtr listView = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (AttachMode == "legacy")
            {
                parentHwnd = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                if (parentHwnd == IntPtr.Zero) { Log.Write("WorkerW not found"); return false; }
                insertAfter = IntPtr.Zero; raised = false; mode = "24H2+ legacy: WorkerW child window";
            }
            else if (AttachMode == "defview" && listView != IntPtr.Zero)
            {
                // Experimental: inside the icon layer, below the icon list (not reordered on wallpaper change, but does not render on 24H2 in practice)
                parentHwnd = defView; insertAfter = listView; zAnchor = listView;
                mode = "24H2+: layered child of the icon layer (DefView), below the icon list";
            }
            else
            {
                parentHwnd = progman; insertAfter = defView; zAnchor = defView;
                mode = "24H2+: layered child of Progman, below the icon layer";
            }
            if (raised)
            {
                int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
                Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
            }
        }
        else
        {
            IntPtr worker = FindClassicWorker();
            if (worker == IntPtr.Zero)
            {
                RequestWorkerW(progman, delegate { return FindClassicWorker() != IntPtr.Zero; });
                worker = FindClassicWorker();
            }
            if (worker == IntPtr.Zero) { Log.Write("WorkerW / SHELLDLL_DefView not found"); return false; }
            if (!Native.IsWindowVisible(worker)) Native.ShowWindow(worker, 5 /* SW_SHOW */);
            parentHwnd = worker; insertAfter = IntPtr.Zero; mode = "Classic: top-level WorkerW child window";
        }

        int style = Native.GetWindowLong(Handle, Native.GWL_STYLE);
        style = (style & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME)) | Native.WS_CHILD | Native.WS_VISIBLE;
        Native.SetWindowLong(Handle, Native.GWL_STYLE, style);
        Native.SetParent(Handle, parentHwnd);

        var pt = new Native.POINT { X = screen.X, Y = screen.Y };
        Native.ScreenToClient(parentHwnd, ref pt);
        Native.SetWindowPos(Handle, insertAfter, pt.X, pt.Y, screen.Width, screen.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        EnsureZOrder();

        if (raised)
        {
            // Fix the order as soon as Explorer creates / reorders the wallpaper layer (on wallpaper change it inserts a new WorkerW above us)
            uint pid;
            Native.GetWindowThreadProcessId(progman, out pid);
            winEventProc = delegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                if (idObject == 0 && !shuttingDown && IsHandleCreated) EnsureZOrder();
            };
            winEventHook = Native.SetWinEventHook(0x8000 /* OBJECT_CREATE */, 0x8004 /* OBJECT_REORDER */, IntPtr.Zero,
                winEventProc, pid, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
        }
        Log.Write(string.Format("Attached ({0}), screen {1}", mode, screen));
        return true;
    }

    // Keep the order: directly below zAnchor (icon list / icon layer); in Progman mode also push the system wallpaper layer to the bottom (only fixed when disturbed)
    int zFixes;
    void EnsureZOrder()
    {
        if (!raised || zAnchor == IntPtr.Zero || !Native.IsWindow(zAnchor)) return;
        bool fixedAny = false;
        if (Native.GetWindow(zAnchor, 2 /* GW_HWNDNEXT */) != Handle)
        {
            Native.SetWindowPos(Handle, zAnchor, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            fixedAny = true;
        }
        if (AttachMode == "progman")
        {
            for (IntPtr w = Native.FindWindowEx(parentHwnd, IntPtr.Zero, "WorkerW", null); w != IntPtr.Zero;
                 w = Native.FindWindowEx(parentHwnd, w, "WorkerW", null))
            {
                if (Native.GetWindow(w, 2) != IntPtr.Zero)   // Not at the bottom → push it to the bottom
                {
                    Native.SetWindowPos(w, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    fixedAny = true;
                }
            }
        }
        if (fixedAny && ++zFixes <= 20) Log.Write("Fixed desktop z-order (Explorer reordered windows)");
    }

    // ---------------------------------------------------------------- Read desktop icons
    void PollIcons()
    {
        EnsureZOrder();
        if (web.CoreWebView2 == null) return;
        if (!iconsDirty && hookedListView != IntPtr.Zero && Native.IsWindow(hookedListView) && Environment.TickCount - lastIconRead < 5000) return;
        iconsDirty = false;
        lastIconRead = Environment.TickCount;
        bool visible;
        List<Native.RECT> rects = reader.Read(out visible);
        if (rects == null) return;
        if (reader.ListView != hookedListView) HookIconList();
        var sb = new StringBuilder();
        sb.Append("{\"t\":\"icons\",\"vis\":").Append(visible ? "true" : "false").Append(",\"r\":[");
        for (int i = 0; i < rects.Count; i++)
        {
            Native.RECT r = rects[i];
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(r.Left - screen.X).Append(',').Append(r.Top - screen.Y).Append(',')
              .Append(r.Right - screen.X).Append(',').Append(r.Bottom - screen.Y).Append(']');
        }
        sb.Append("]}");
        string json = sb.ToString();
        if (json == lastIcons) return;
        lastIcons = json;
        if (IconsChanged != null) IconsChanged(json);
        Post(json);
        Log.Write(string.Format("Icons changed: {0}, visible={1}", rects.Count, visible));
    }

    void HookIconList()
    {
        UnhookIconList();
        hookedListView = reader.ListView;
        if (hookedListView == IntPtr.Zero) return;
        uint pid;
        Native.GetWindowThreadProcessId(hookedListView, out pid);
        if (iconEventProc == null)
            iconEventProc = delegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                if (hwnd == hookedListView) iconsDirty = true;
            };
        // OBJECT_CREATE .. OBJECT_LOCATIONCHANGE: icons added, removed, renamed, moved or rearranged
        iconHook = Native.SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, iconEventProc, pid, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
    }

    void UnhookIconList()
    {
        if (iconHook != IntPtr.Zero) { Native.UnhookWinEvent(iconHook); iconHook = IntPtr.Zero; }
        hookedListView = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- Pause while maximized / fullscreen
    void PollForeground()
    {
        if (web.CoreWebView2 == null) return;
        bool pause = manualPause;
        string pauseBy = "";
        IntPtr fg = Native.GetForegroundWindow();
        int cloaked = 0;
        if (!pause && fg != IntPtr.Zero && fg != Handle && Native.IsWindowVisible(fg)
            && (Native.DwmGetWindowAttribute(fg, 14 /* DWMWA_CLOAKED */, out cloaked, 4) != 0 || cloaked == 0))
        {
            var cls = new StringBuilder(64);
            Native.GetClassName(fg, cls, 64);
            string c = cls.ToString();
            if (c != "Progman" && c != "WorkerW" && c != "Shell_TrayWnd" && c != "Shell_SecondaryTrayWnd")
            {
                Native.RECT wr;
                Native.GetWindowRect(fg, out wr);
                bool covers = wr.Left <= screen.Left && wr.Top <= screen.Top && wr.Right >= screen.Right && wr.Bottom >= screen.Bottom;
                int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
                if (screen.Contains(cx, cy) && (covers || Native.IsZoomed(fg))) { pause = true; pauseBy = c; }
            }
        }
        int p = pause ? 1 : 0;
        if (p == lastPause) return;
        lastPause = p;
        Post("{\"t\":\"pause\",\"v\":" + (pause ? "true" : "false") + "}");
        Log.Write(pause ? "Paused (a window is maximized / fullscreen, or paused manually)" + (pauseBy == "" ? "" : "; foreground window class " + pauseBy) : "Resumed");
    }

    // ---------------------------------------------------------------- Exit: tear down the window and let Explorer repaint the original wallpaper
    public void Shutdown()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        iconTimer.Stop(); fgTimer.Stop();
        UnhookIconList();
        reader.Dispose();
        IntPtr parent = parentHwnd;
        try { web.Dispose(); } catch { }
        if (IsHandleCreated) { Native.SetParent(Handle, IntPtr.Zero); Hide(); }
        // Do not touch wallpaper settings (slideshow / Spotlight unaffected); just let Explorer repaint
        if (parent != IntPtr.Zero && Native.IsWindow(parent))
            Native.RedrawWindow(parent, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100);
        Close();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(winEventHook); winEventHook = IntPtr.Zero; }
        iconTimer.Stop(); fgTimer.Stop();
        UnhookIconList();
        reader.Dispose();
        base.OnHandleDestroyed(e);
    }
}

// Read icon rectangles (icon + label, physical pixels, screen coordinates) through the desktop ListView's accessibility
// interface (MSAA, as screen readers do). Nothing touches Explorer's memory, which antivirus software treats as code injection.
class IconReader : IDisposable
{
    IntPtr listView;
    public IntPtr ListView { get { return listView; } }
    Accessibility.IAccessible acc;

    static IntPtr FindListView()
    {
        IntPtr defView = IntPtr.Zero;
        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                IntPtr d = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero) { defView = d; return false; }
                return true;
            }, IntPtr.Zero);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    bool Open()
    {
        Close();
        listView = FindListView();
        if (listView == IntPtr.Zero) return false;
        Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");     // IID_IAccessible
        object o;
        if (Native.AccessibleObjectFromWindow(listView, -4 /* OBJID_CLIENT */, ref iid, out o) != 0) return false;
        acc = o as Accessibility.IAccessible;
        return acc != null;
    }

    public List<Native.RECT> Read(out bool visible)
    {
        visible = false;
        if (listView == IntPtr.Zero || acc == null || !Native.IsWindow(listView)) { if (!Open()) return null; }
        visible = Native.IsWindowVisible(listView);
        var result = new List<Native.RECT>();
        if (!visible) return result;                   // User hid desktop icons: no patches
        try
        {
            int count = (int)Native.SendMessage(listView, 0x1004 /* LVM_GETITEMCOUNT */, IntPtr.Zero, IntPtr.Zero);
            for (int i = 1; i <= count; i++)           // MSAA child ids are 1-based item indexes
            {
                int x, y, w, h;
                acc.accLocation(out x, out y, out w, out h, i);
                if (w <= 0 || h <= 0) continue;
                var r = new Native.RECT();
                r.Left = x; r.Top = y; r.Right = x + w; r.Bottom = y + h;
                result.Add(r);
            }
        }
        catch (Exception) { Close(); return null; }   // Explorer restarted: reconnect next time
        return result;
    }

    void Close()
    {
        if (acc != null) { try { Marshal.ReleaseComObject(acc); } catch { } }
        acc = null;
    }

    public void Dispose() { Close(); }
}

static class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x2, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const int SW_SHOW = 5, SW_RESTORE = 9;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder sb, uint winIni);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint vk);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] public static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
    public delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr hwnd, int objectId, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}

// Dynamic Glow has its own data and instance identity; isolated profiles support installation QA.
static class InstallProfile
{
    public static readonly string Override = Environment.GetEnvironmentVariable("DYNAMICGLOW_PROFILE_DIR");
    public static readonly bool Isolated = !string.IsNullOrEmpty(Override);
    public static readonly string DataDir = Isolated ? Path.GetFullPath(Override) : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicGlow");
    public static readonly string Suffix = MakeSuffix();
    static string MakeSuffix()
    {
        if (!Isolated) return "_DG";
        using (var hash = System.Security.Cryptography.SHA256.Create())
            return "_DG_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(DataDir.ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
    }

    // An installer may only close the instance using this exact executable path.
    public static int StopInstalled()
    {
        foreach (Process process in Process.GetProcessesByName("DynamicGlow"))
        {
            using (process)
            {
                if (process.Id == Process.GetCurrentProcess().Id) continue;
                try
                {
                    if (!string.Equals(process.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                    EventWaitHandle ev;
                    if (!EventWaitHandle.TryOpenExisting(Program.ExitEventName, out ev)) return 1;
                    using (ev) ev.Set();
                    if (!process.WaitForExit(10000)) return 1;
                }
                catch (InvalidOperationException) { }
                catch { return 1; }
            }
        }
        return 0;
    }
}
