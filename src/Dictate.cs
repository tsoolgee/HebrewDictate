// Hebrew dictation for Windows, in the spirit of Win+H.
// Speech recognition is Chrome's built-in Web Speech API (Google's engine), hosted in a small
// Chrome app window. This program owns the hotkey and types the recognized text into whatever
// window has focus. Build with build.bat (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

class DictateApp : Form
{
    const int Port = 48731;
    const string Lang = "he-IL";
    const bool VoiceCommands = true;          // "נקודה" -> ".", "שורה חדשה" -> Enter, ...
    const int HotkeyId = 1;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x312;

    // Tried in order; the first one nobody else has registered wins. (Win+H itself is reserved by Windows.)
    static readonly object[][] HotkeyChoices =
    {
        new object[] { "Ctrl+Alt+D", MOD_CONTROL | MOD_ALT, (uint)0x44 },
        new object[] { "Ctrl+Alt+H", MOD_CONTROL | MOD_ALT, (uint)0x48 },
        new object[] { "Ctrl+Shift+H", MOD_CONTROL | MOD_SHIFT, (uint)0x48 }
    };
    string hotkeyName;
    MicButton mic;
    ToolStripMenuItem startupItem, widgetItem;

    static readonly Dictionary<string, string> Commands = new Dictionary<string, string>
    {
        { "נקודה", "." },
        { "פסיק", "," },
        { "נקודתיים", ":" },
        { "סימן שאלה", "?" },
        { "סימן קריאה", "!" },
        { "שורה חדשה", "\n" },
        { "ירידת שורה", "\n" },
        { "פסקה חדשה", "\n\n" }
    };

    readonly object gate = new object();
    bool listening;
    int version;
    int pollers;
    DateTime stoppedAt = DateTime.MinValue;


    readonly string token;
    readonly string chromePath;
    HttpListener listener;
    NotifyIcon tray;
    Icon idleIcon, onIcon;

    static void Log(string m)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "HebrewDictate.log"), DateTime.Now.ToString("HH:mm:ss") + " " + m + "\r\n"); } catch { }
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "HebrewDictate_SingleInstance", out created))
        {
            if (!created) { Log("another instance holds the mutex"); return; }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            try { Application.Run(new DictateApp()); }
            catch (Exception ex)
            {
                string log = Path.Combine(Path.GetTempPath(), "HebrewDictate.log");
                File.WriteAllText(log, ex.ToString());
                MessageBox.Show("שגיאה לא צפויה:\n" + ex.Message, "הקלדה קולית");
            }
        }
    }

    public DictateApp()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        var forceHandle = Handle;

        token = NewToken();
        chromePath = FindChrome();
        if (chromePath == null)
        {
            Log("chrome not found");
            MessageBox.Show("Google Chrome לא נמצא במחשב. התוכנה צריכה אותו לזיהוי הדיבור.", "הקלדה קולית");
            Environment.Exit(1);
        }

        foreach (object[] choice in HotkeyChoices)
        {
            if (RegisterHotKey(Handle, HotkeyId, (uint)choice[1] | MOD_NOREPEAT, (uint)choice[2]))
            {
                hotkeyName = (string)choice[0];
                break;
            }
            Log("hotkey " + choice[0] + " is taken");
        }
        if (hotkeyName == null)
        {
            MessageBox.Show("לא הצלחתי לרשום קיצור מקשים. כל הקיצורים האפשריים תפוסים על ידי תוכנות אחרות.", "הקלדה קולית");
            Environment.Exit(1);
        }

        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            listener.Start();
        }
        catch (Exception ex)
        {
            Log("listener failed: " + ex);
            MessageBox.Show("לא הצלחתי להפעיל את השרת המקומי על פורט " + Port + ":\n" + ex.Message, "הקלדה קולית");
            Environment.Exit(1);
        }
        var t = new Thread(ServeLoop);
        t.IsBackground = true;
        t.Start();

        idleIcon = MakeIcon(Color.Gray);
        onIcon = MakeIcon(Color.Red);
        var menu = new ContextMenuStrip();
        menu.Items.Add("התחל / עצור  (" + hotkeyName + ")", null, delegate { Toggle(); });
        menu.Items.Add("הצג / הסתר חלון זיהוי", null, delegate { SetChromeVisible(chromeHidden); });
        menu.Items.Add("פתח מחדש את חלון הזיהוי", null, delegate { LaunchChrome(); });
        startupItem = new ToolStripMenuItem("הפעל עם Windows");
        startupItem.Click += delegate { SetAutostart(!AutostartEnabled()); };
        menu.Items.Add(startupItem);
        widgetItem = new ToolStripMenuItem("הצג כפתור מיקרופון צף");
        widgetItem.Click += delegate { mic.Visible = !mic.Visible; SaveWidget(); };
        menu.Items.Add(widgetItem);
        menu.Opening += delegate { startupItem.Checked = AutostartEnabled(); widgetItem.Checked = mic.Visible; };
        menu.Items.Add("יציאה", null, delegate { ExitApp(); });
        tray = new NotifyIcon();
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Toggle(); };
        UpdateTray();
        tray.Visible = true;

        mic = new MicButton(delegate { Toggle(); }, delegate { return listening; }, menu);
        mic.Moved += delegate { SaveWidget(); };
        bool widgetVisible;
        mic.Location = LoadWidget(out widgetVisible);
        if (widgetVisible) mic.Show();

        LaunchChrome();
        tray.ShowBalloonTip(3000, "הקלדה קולית", "מוכן. " + hotkeyName + " להתחלה ולעצירה.", ToolTipIcon.Info);
    }

    protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) Toggle();
        base.WndProc(ref m);
    }

    // ---- state ----

    void Toggle()
    {
        bool relaunch;
        lock (gate)
        {
            listening = !listening;
            version++;
            if (!listening) stoppedAt = DateTime.UtcNow;
            relaunch = listening && pollers == 0;
            Monitor.PulseAll(gate);
        }
        if (relaunch)
        {
            LaunchChrome();
            tray.ShowBalloonTip(3000, "הקלדה קולית", "חלון הזיהוי נפתח מחדש. חזרו לשדה הטקסט והמשיכו לדבר.", ToolTipIcon.Info);
        }
        UpdateTray();
        if (listening) lock (typeLock) { live = ""; lastNeedsSpace = false; }

    }

    void UpdateTray()
    {
        tray.Icon = listening ? onIcon : idleIcon;
        if (mic != null) mic.Redraw();
        tray.Text = listening ? "הקלדה קולית: מקליט" : "הקלדה קולית: כבוי (" + hotkeyName + ")";
    }

    void ExitApp()
    {
        lock (gate) { listening = false; version++; Monitor.PulseAll(gate); }
        tray.Visible = false;
        mic.Close();
        CloseChromeWindow();
        UnregisterHotKey(Handle, HotkeyId);
        try { listener.Stop(); } catch { }
        Application.Exit();
    }

    // ---- local server (page <-> app) ----

    void ServeLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { HandleRequest(ctx); }
                catch { try { ctx.Response.Abort(); } catch { } }
            });
        }
    }

    void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        string path = req.Url.AbsolutePath;

        // Only our own page may talk to us: fixed host (blocks DNS rebinding) plus a random token.
        if (req.Headers["Host"] != "127.0.0.1:" + Port) { Send(res, 403, "text/plain", "forbidden"); return; }

        if (req.HttpMethod == "GET" && path == "/")
        {
            if (req.QueryString["t"] != token) { Send(res, 403, "text/plain", "forbidden"); return; }
            Send(res, 200, "text/html", PageHtml.Replace("__TOKEN__", token).Replace("__LANG__", Lang).Replace("__KEY__", hotkeyName));
            return;
        }
        if (req.Headers["X-Token"] != token) { Send(res, 403, "text/plain", "forbidden"); return; }

        if (path == "/wait")
        {
            int v;
            if (!int.TryParse(req.QueryString["v"], out v)) v = -1;
            int cur;
            bool on;
            lock (gate)
            {
                pollers++;
                if (version == v) Monitor.Wait(gate, 25000);
                pollers--;
                cur = version;
                on = listening;
            }
            Send(res, 200, "application/json", "{\"v\":" + cur + ",\"on\":" + (on ? "true" : "false") + "}");
        }
        else if (path == "/type" && req.HttpMethod == "POST")
        {
            string body;
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            OnLive(body, true);
            Send(res, 200, "text/plain", "ok");
        }
        else if (path == "/live" && req.HttpMethod == "POST")
        {
            string body;
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            OnLive(body, false);
            Send(res, 200, "text/plain", "ok");
        }
        else if (path == "/status" && req.HttpMethod == "POST")
        {
            string body;
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            BeginInvoke(new Action(delegate { OnPageStatus(body); }));
            Send(res, 200, "text/plain", "ok");
        }
        else Send(res, 404, "text/plain", "not found");
    }

    static void Send(HttpListenerResponse res, int code, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = code;
        res.ContentType = type + "; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }

    void OnPageStatus(string status)
    {
        if (status == "show") { chromeWanted = true; SetChromeVisible(true); return; }
        if (status == "hide") { chromeWanted = false; SetChromeVisible(false); return; }
        if (status.StartsWith("error:")) SetChromeVisible(true);   // let the user see/fix permission problems
        ShowPageError(status);
    }

    void ShowPageError(string status)
    {
        if (status.StartsWith("error:not-allowed") || status.StartsWith("error:service-not-allowed"))
            tray.ShowBalloonTip(6000, "הקלדה קולית", "אין הרשאת מיקרופון. אשרו אותה בחלון הזיהוי (Chrome) ולחצו שוב.", ToolTipIcon.Error);
        else if (status.StartsWith("error:audio-capture"))
            tray.ShowBalloonTip(6000, "הקלדה קולית", "לא נמצא מיקרופון.", ToolTipIcon.Error);
        else if (status.StartsWith("error:network"))
            tray.ShowBalloonTip(6000, "הקלדה קולית", "אין חיבור לשירות הזיהוי של גוגל. בדקו את האינטרנט.", ToolTipIcon.Error);
        else if (status.StartsWith("error:language-not-supported"))
            tray.ShowBalloonTip(6000, "הקלדה קולית", "Chrome לא תומך בשפה " + Lang + ".", ToolTipIcon.Error);
    }

    // ---- text handling ----

    readonly object typeLock = new object();
    string live = "";            // what is currently typed for the phrase being recognized
    IntPtr liveWindow;
    static bool lastNeedsSpace;  // the previous phrase ended in text, so the next one starts with a space

    // Called for every interim update (final == false) and for the final result of a phrase (final == true).
    // The phrase is typed as it grows; when the recognizer revises it, only the changed tail is backspaced and retyped.
    void OnLive(string raw, bool final)
    {
        lock (gate)
        {
            // A final result can arrive just after stopping; still type it.
            if (!listening && (DateTime.UtcNow - stoppedAt).TotalSeconds > 4) return;
        }
        lock (typeLock)
        {
            string body = ProcessText(raw);
            if (body.Length == 0) return;
            string target = Lead(body) + body;
            IntPtr fg = GetForegroundWindow();
            if (live.Length > 0 && fg != liveWindow) live = "";   // focus moved to another window: leave the old text alone
            liveWindow = fg;
            TypeText(Edit(live, target));
            live = target;
            if (final)
            {
                lastNeedsSpace = !target.EndsWith("\n");
                live = "";
            }
        }
    }

    // Keystrokes that turn the text typed so far into the target: backspace the part that changed, type the rest.
    static string Edit(string live, string target)
    {
        int k = 0;
        while (k < live.Length && k < target.Length && live[k] == target[k]) k++;
        return new string('\b', live.Length - k) + target.Substring(k);
    }

    static string Lead(string body)
    {
        return lastNeedsSpace && ".,:?!\n".IndexOf(body[0]) < 0 ? " " : "";
    }

    static string ProcessText(string raw)
    {
        string[] w = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        bool attach = true;   // the next word follows directly (start of phrase, or after a new line)
        for (int i = 0; i < w.Length; i++)
        {
            string cmd = null;
            int used = 1;
            if (VoiceCommands)
            {
                if (i + 1 < w.Length && Commands.TryGetValue(w[i] + " " + w[i + 1], out cmd)) used = 2;
                else Commands.TryGetValue(w[i], out cmd);
            }
            if (cmd != null)
            {
                sb.Append(cmd);
                attach = cmd.StartsWith("\n");
                i += used - 1;
            }
            else
            {
                if (!attach) sb.Append(' ');
                sb.Append(w[i]);
                attach = false;
            }
        }
        return sb.ToString();
    }

    // ---- Chrome ----

    void LaunchChrome()
    {
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HebrewDictate", "profile");
        var wa = Screen.PrimaryScreen.WorkingArea;
        float dpi;
        using (var sg = Graphics.FromHwnd(IntPtr.Zero)) dpi = sg.DpiX / 96f;   // Chrome takes the position in scaled units
        string args = string.Format(
            "--user-data-dir=\"{0}\" --app=\"http://127.0.0.1:{1}/?t={2}\" --window-size=340,150 --window-position={3},{4} " +
            "--no-first-run --no-default-browser-check --disable-background-timer-throttling " +
            "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows",
            profile, Port, token, Math.Max(0, (int)((wa.Right - 350) / dpi)), Math.Max(0, (int)((wa.Bottom - 160) / dpi)));
        chromeHidden = false;
        chromeWanted = false;
        // Hide the window as soon as it exists so it never lingers in the taskbar.
        var hideTimer = new System.Windows.Forms.Timer();
        int tries = 0;
        hideTimer.Interval = 150;
        hideTimer.Tick += delegate
        {
            if (chromeWanted || SetChromeVisible(false) || ++tries > 80) { hideTimer.Stop(); hideTimer.Dispose(); }
        };
        hideTimer.Start();
        try { Process.Start(chromePath, args); }
        catch (Exception ex) { MessageBox.Show("לא הצלחתי לפתוח את Chrome:\n" + ex.Message, "הקלדה קולית"); }
    }

    static string FindChrome()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        };
        foreach (string c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    // ---- the recognition window (hidden once the mic is allowed) ----

    bool chromeHidden, chromeWanted;   // chromeWanted: the page asked for the window (e.g. to approve the mic)
    delegate bool EnumProc(IntPtr h, IntPtr l);

    IntPtr FindChromeWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            // Chrome wraps right-to-left titles in invisible direction marks.
            if (title.ToString().Trim('‪', '‫', '‬', '‭', '‮', '‎', '‏') != "הקלדה קולית") return true;
            // The Claude app is Electron too, so make sure this really is our Chrome.
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try
            {
                string exe = Process.GetProcessById((int)pid).MainModule.FileName;
                if (!string.Equals(exe, chromePath, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    bool SetChromeVisible(bool visible)
    {
        IntPtr h = FindChromeWindow();
        if (h == IntPtr.Zero) return false;
        ShowWindow(h, visible ? 9 : 0);   // 9 = restore, also un-minimizes
        if (visible)
        {
            // Bring it to the front and centre: it could be off-screen or behind other windows.
            RECT r;
            GetWindowRect(h, out r);
            var wa = Screen.PrimaryScreen.WorkingArea;
            int w = r.R - r.L, ht = r.B - r.T;
            SetWindowPos(h, new IntPtr(-1), wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - ht) / 2, 0, 0, 0x1 | 0x40);   // TOPMOST, NOSIZE | SHOWWINDOW
            SetForegroundWindow(h);
        }
        chromeHidden = !visible;
        return true;
    }

    void CloseChromeWindow()
    {
        IntPtr h = FindChromeWindow();
        if (h != IntPtr.Zero) PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- floating mic button + autostart ----

    static string WidgetFile
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HebrewDictate", "widget.txt"); }
    }

    static Point LoadWidget(out bool visible)
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        Point pos = new Point(wa.Right - 90, wa.Bottom - 130);
        visible = true;
        try
        {
            string[] p = File.ReadAllText(WidgetFile).Split(',');
            Point saved = new Point(int.Parse(p[0]), int.Parse(p[1]));
            foreach (Screen s in Screen.AllScreens) if (s.WorkingArea.Contains(saved)) pos = saved;
            visible = p[2] == "1";
        }
        catch { }
        return pos;
    }

    void SaveWidget()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WidgetFile));
            File.WriteAllText(WidgetFile, mic.Left + "," + mic.Top + "," + (mic.Visible ? "1" : "0"));
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool AutostartEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HebrewDictate") != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("HebrewDictate", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("HebrewDictate", false);
        }
    }

    // ---- helpers ----

    static string NewToken()
    {
        var bytes = new byte[16];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
        var sb = new StringBuilder();
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    static Icon MakeIcon(Color c)
    {
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var br = new SolidBrush(c)) g.FillEllipse(br, 1, 1, 14, 14);
            }
            IntPtr h = bmp.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return icon;
        }
    }

    // ---- typing into the focused window ----

    static void TypeText(string s)
    {
        var list = new List<INPUT>();
        foreach (char c in s)
        {
            if (c == '\r') continue;
            if (c == '\n') { list.Add(Key(0x0D, 0, 0)); list.Add(Key(0x0D, 0, KEYEVENTF_KEYUP)); }
            else if (c == '\b') { list.Add(Key(0x08, 0, 0)); list.Add(Key(0x08, 0, KEYEVENTF_KEYUP)); }
            else { list.Add(Key(0, c, KEYEVENTF_UNICODE)); list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)); }
        }
        for (int i = 0; i < list.Count; i += 100)
        {
            int n = Math.Min(100, list.Count - i);
            SendInput((uint)n, list.GetRange(i, n).ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }

    const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    static INPUT Key(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT();
        i.type = 1;
        i.u.ki.wVk = vk;
        i.u.ki.wScan = scan;
        i.u.ki.dwFlags = flags;
        return i;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    // ---- the recognition page (runs in Chrome) ----

    const string PageHtml = @"<!doctype html>
<html lang='he' dir='rtl'><head><meta charset='utf-8'><title>הקלדה קולית</title>
<style>
body{margin:0;height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;
font-family:'Segoe UI',Arial,sans-serif;background:#2b2b2b;color:#eee;transition:background .2s}
body.on{background:#b3261e}
#s{font-size:22px;font-weight:600}
#i{font-size:14px;opacity:.9;margin-top:8px;min-height:20px;padding:0 10px;text-align:center}
#h{font-size:12px;opacity:.6;margin-top:6px}
</style></head><body>
<div id='s'>מוכן</div><div id='i'></div><div id='h' dir='ltr'>__KEY__</div>
<script>
var T='__TOKEN__', H={'X-Token':T};
var SR=window.SpeechRecognition||window.webkitSpeechRecognition;
var want=false, running=false, rec=null;
function $(id){return document.getElementById(id);}
function ui(){
  document.body.className=want?'on':'';
  $('s').textContent=want?(running?'מקליט...':'מתחבר...'):'מוכן';
  if(!want)$('i').textContent='';
}
var q=[], busy=false;
async function pump(){
  if(busy)return;busy=true;
  while(q.length){var it=q.shift();try{await fetch(it[0],{method:'POST',headers:H,body:it[1]});}catch(_){}}
  busy=false;
}
function enqueue(path,body){
  if(path==='/live'&&q.length&&q[q.length-1][0]==='/live')q[q.length-1]=[path,body];else q.push([path,body]);
  pump();
}
function post(path,body){
  return fetch(path,{method:'POST',headers:H,body:body}).catch(function(){});
}
if(!SR){
  $('s').textContent='הדפדפן לא תומך בזיהוי דיבור';
}else{
  rec=new SR();
  rec.lang='__LANG__';
  rec.continuous=true;
  rec.interimResults=true;
  rec.onstart=function(){running=true;ui();hideOnce();};
  rec.onresult=function(e){
    var interim='';
    for(var i=e.resultIndex;i<e.results.length;i++){
      var r=e.results[i];
      if(r.isFinal){enqueue('/type',r[0].transcript);}else{interim+=r[0].transcript;}
    }
    if(interim){enqueue('/live',interim);}
    $('i').textContent=interim;
  };
  rec.onerror=function(e){
    if(e.error==='no-speech'||e.error==='aborted')return;
    post('/status','error:'+e.error);
    if(e.error==='not-allowed'||e.error==='service-not-allowed'||e.error==='audio-capture'||e.error==='language-not-supported')want=false;
  };
  rec.onend=function(){
    running=false;ui();
    if(want)setTimeout(function(){try{rec.start();}catch(_){}},250);
  };
}
function ensureMic(go){
  try{navigator.permissions.query({name:'microphone'}).then(function(p){
    if(p.state!=='granted'){hid=false;$('i').textContent='אם מופיעה בקשה, לחצו על אפשר';post('/status','show');}
    go();
  },function(){go();});}catch(_){go();}
}
function apply(on){
  want=on;
  if(!rec)return;
  if(on&&!running){ensureMic(function(){try{rec.start();}catch(_){}});}
  if(!on&&running){try{rec.stop();}catch(_){}}
  ui();
}
var hid=false;
function hideOnce(){if(!hid){hid=true;post('/status','hide');}}
try{navigator.permissions.query({name:'microphone'}).then(function(p){
  if(p.state==='granted'){hideOnce();return;}
  // First run: ask for the microphone now, with the window in front so the prompt can be clicked.
  $('i').textContent='לחצו על אפשר כדי לאשר את המיקרופון';
  post('/status','show');
  navigator.mediaDevices.getUserMedia({audio:true}).then(function(s){
    s.getTracks().forEach(function(tr){tr.stop();});

    $('i').textContent='';hid=false;hideOnce();
  },function(){post('/status','error:not-allowed');});
});}catch(_){}
var v=-1, fails=0;
(async function loop(){
  for(;;){
    try{
      var r=await fetch('/wait?v='+v,{headers:H});
      if(!r.ok)throw 0;
      var s=await r.json();
      fails=0;v=s.v;apply(s.on);
    }catch(_){
      if(++fails>5){window.close();return;}
      await new Promise(function(ok){setTimeout(ok,1000);});
    }
  }
})();
ui();
</script></body></html>";
}

// A small round mic button that floats above other windows and never takes focus when clicked,
// so the text field you were typing in stays active. Drag to move, right-click for the menu.
// It is drawn into a per-pixel-alpha layered window: smooth edges, a soft shadow, and the
// recording pulse can radiate outside the button itself.
class MicButton : Form
{
    const float Canvas = 96f;        // design units; the button is a circle of radius Radius in the middle
    const float Radius = 26f;
    const float PulsePeriod = 1.8f;  // seconds per wave

    readonly Action onClick;
    readonly Func<bool> isOn;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly float scale;
    Point downScreen, downLocation;
    bool pressed, dragged;

    public event EventHandler Moved;

    public MicButton(Action onClick, Func<bool> isOn, ContextMenuStrip menu)
    {
        this.onClick = onClick;
        this.isOn = isOn;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Hand;
        ContextMenuStrip = menu;
        using (var screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
        int px = (int)Math.Round(Canvas * scale);
        Size = new Size(px, px);
        anim.Interval = 16;
        anim.Tick += delegate { if (this.isOn() && Visible) Redraw(); };
        anim.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x8 | 0x80000;   // NOACTIVATE | TOOLWINDOW | TOPMOST | LAYERED
            return cp;
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        Redraw();
    }

    // Only the circle itself reacts to the mouse, not the faint pulse around it.
    bool OnButton(Point p)
    {
        float dx = p.X - Width / 2f, dy = p.Y - Height / 2f;
        float r = Radius * scale + 3;
        return dx * dx + dy * dy <= r * r;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && OnButton(e.Location)) { pressed = true; dragged = false; downScreen = Cursor.Position; downLocation = Location; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (pressed)
        {
            int dx = Cursor.Position.X - downScreen.X, dy = Cursor.Position.Y - downScreen.Y;
            if (dragged || Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
            {
                dragged = true;
                Location = new Point(downLocation.X + dx, downLocation.Y + dy);
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pressed)
        {
            pressed = false;
            if (dragged) { if (Moved != null) Moved(this, EventArgs.Empty); }
            else onClick();
        }
        base.OnMouseUp(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) anim.Dispose();
        base.Dispose(disposing);
    }

    // ---- drawing ----

    public void Redraw()
    {
        if (!IsHandleCreated || !Visible) return;
        using (Bitmap bmp = Frame(isOn(), (float)clock.Elapsed.TotalSeconds))
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBmp);
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    static Color Mix(Color a, Color b, float k)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
    }

    // One frame of the button at time t (seconds).
    internal Bitmap Frame(bool on, float t)
    {
        int px = (int)Math.Round(Canvas * scale);
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(scale, scale);
            float c = Canvas / 2;
            Color red = Color.FromArgb(217, 48, 37);

            // soft shadow
            for (int i = 0; i < 6; i++)
            {
                float r = Radius + 5 - i;
                using (var b = new SolidBrush(Color.FromArgb(9, 0, 0, 0))) g.FillEllipse(b, c - r, c - r + 1.5f, 2 * r, 2 * r);
            }

            float beat = (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * t / PulsePeriod));
            if (on)
            {
                // three waves, spread evenly in time, each easing out while it fades
                for (int i = 0; i < 3; i++)
                {
                    float u = (t / PulsePeriod + i / 3f) % 1f;
                    float inv = 1f - u;
                    float e = 1f - inv * inv * inv;
                    float r = Radius + e * (c - Radius - 1);
                    int a = (int)(150 * inv * inv);
                    using (var fill = new SolidBrush(Color.FromArgb(a / 3, red))) g.FillEllipse(fill, c - r, c - r, 2 * r, 2 * r);
                    using (var ring = new Pen(Color.FromArgb(a, red), 2.2f)) g.DrawEllipse(ring, c - r, c - r, 2 * r, 2 * r);
                }
            }

            using (var body = new SolidBrush(on ? Mix(red, Color.FromArgb(240, 84, 70), beat) : Color.White))
                g.FillEllipse(body, c - Radius, c - Radius, 2 * Radius, 2 * Radius);
            if (!on) using (var rim = new Pen(Color.FromArgb(218, 220, 224), 1.2f)) g.DrawEllipse(rim, c - Radius, c - Radius, 2 * Radius, 2 * Radius);

            // the microphone, drawn in a 64-unit box and scaled to the button (it also breathes while recording)
            float k = Radius / 32f * (on ? 1f + 0.04f * beat : 1f);
            g.TranslateTransform(c, c);
            g.ScaleTransform(k, k);
            g.TranslateTransform(-32, -32);
            Color capsule = on ? Color.White : Color.FromArgb(66, 133, 244);
            Color stand = on ? Color.White : Color.FromArgb(52, 168, 83);
            using (var path = new GraphicsPath())
            {
                path.AddArc(25, 12, 14, 14, 180, 180);
                path.AddArc(25, 22, 14, 14, 0, 180);
                path.CloseFigure();
                using (var b = new SolidBrush(capsule)) g.FillPath(b, path);
            }
            using (var pen = new Pen(stand, 3))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, 20, 22, 24, 22, 0, 180);
                g.DrawLine(pen, 32, 44, 32, 50);
                g.DrawLine(pen, 26, 51, 38, 51);
            }
        }
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
