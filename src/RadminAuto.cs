// RadminAuto - launches Radmin Viewer for a host and fills in the login dialog.
//
// Radmin Viewer has no command-line switch for user name / password, so this
// wrapper starts "Radmin.exe /connect:host:port", waits for the security
// dialog, writes the credentials into its edit boxes and presses OK.
//
// It can also be started from a web page through a "radminauto://" link once
// "RadminAuto --register" has registered that URL scheme for the current user.
//
// Written in C# 5 so it builds with the csc.exe that ships with .NET Framework 4.x.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace RadminAuto
{
    internal static class Program
    {
        const int ExitOk = 0;
        const int ExitUsage = 1;
        const int ExitRadminNotFound = 2;
        const int ExitNoLoginDialog = 3;
        const int ExitLoginRejected = 4;
        const int ExitFillFailed = 5;

        static bool verbose;
        // Started from a radminauto:// link: there is nobody watching a console, so errors go to a message box.
        static bool urlMode;

        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0].Equals("--register", StringComparison.OrdinalIgnoreCase))
                return UrlProtocol.Register();
            if (args.Length == 1 && args[0].Equals("--unregister", StringComparison.OrdinalIgnoreCase))
                return UrlProtocol.Unregister();

            Options opt;
            string url = UrlProtocol.FindUrlArgument(args);
            if (url != null)
            {
                // Only the link itself is trusted; any other argument could have been smuggled in by the link.
                urlMode = true;
                Native.HideOwnConsole();
                try
                {
                    opt = Options.FromUrl(url);
                }
                catch (ArgumentException ex)
                {
                    return Fail(ExitUsage, "Invalid link: " + ex.Message);
                }
            }
            else
            {
                try
                {
                    opt = Options.Parse(args);
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine("Error: " + ex.Message);
                    Console.Error.WriteLine("Run 'RadminAuto --help' for usage.");
                    return ExitUsage;
                }
            }
            if (opt.ShowHelp)
            {
                PrintUsage();
                return ExitOk;
            }
            verbose = opt.Verbose;

            string radmin = FindRadmin(opt.RadminPath);
            if (radmin == null)
            {
                return Fail(ExitRadminNotFound, "Radmin.exe not found. Use --radmin <path> or set RADMIN_PATH.");
            }

            // Remember login dialogs that already exist so we never type into someone else's.
            HashSet<IntPtr> existing = new HashSet<IntPtr>();
            foreach (LoginDialog d in LoginDialog.FindAll())
                existing.Add(d.Handle);

            string radminArgs = BuildRadminArgs(opt);
            Log("Starting \"" + radmin + "\" " + radminArgs);
            ProcessStartInfo psi = new ProcessStartInfo(radmin, radminArgs);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(radmin);
            Process.Start(psi);

            LoginDialog dlg = WaitForNewDialog(existing, opt.Host, opt.TimeoutSeconds * 1000);
            if (dlg == null)
            {
                return Fail(ExitNoLoginDialog, "Radmin login dialog for " + opt.Host + " did not appear within " +
                    opt.TimeoutSeconds + "s (host unreachable, or credentials already saved in Radmin?).");
            }
            Log("Login dialog found: \"" + dlg.Title + "\" (user field: " + (dlg.UserEdit != IntPtr.Zero ? "yes" : "no") + ")");
            if (verbose)
                dlg.DumpControls();
            // Give the dialog a moment to finish initialising before we touch it.
            Thread.Sleep(300);

            if (!dlg.Fill(opt.User, opt.Password, opt.Domain))
            {
                return Fail(ExitFillFailed, "Could not write credentials into the Radmin login dialog.");
            }
            if (opt.NoSubmit)
            {
                Console.WriteLine("Credentials filled (not submitted because of --no-submit).");
                return ExitOk;
            }

            existing.Add(dlg.Handle);
            if (!dlg.Submit())
            {
                return Fail(ExitLoginRejected, "Radmin login dialog is still open after pressing OK. Run with -v and send the output.");
            }
            // A wrong password makes Radmin ask again with a fresh dialog.
            if (WaitForNewDialog(existing, opt.Host, 2000) != null)
            {
                return Fail(ExitLoginRejected, "Radmin asked for credentials again for " + opt.Host + " (wrong user name or password?).");
            }

            Console.WriteLine("Logged in to " + opt.Host + ":" + opt.Port);
            return ExitOk;
        }

        static int Fail(int exitCode, string message)
        {
            Console.Error.WriteLine("Error: " + message);
            if (urlMode)
                Native.ShowError(message);
            return exitCode;
        }

        static LoginDialog WaitForNewDialog(HashSet<IntPtr> exclude, string host, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                LoginDialog fallback = null;
                foreach (LoginDialog d in LoginDialog.FindAll())
                {
                    if (exclude.Contains(d.Handle))
                        continue;
                    if (d.Title.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0)
                        return d;
                    if (fallback == null)
                        fallback = d;
                }
                if (fallback != null)
                    return fallback;
                if (sw.ElapsedMilliseconds >= timeoutMs)
                    return null;
                Thread.Sleep(200);
            }
        }

        internal static bool WaitUntilGone(IntPtr hwnd, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd))
                    return true;
                Thread.Sleep(200);
            }
            return false;
        }

        static string BuildRadminArgs(Options opt)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("/connect:").Append(opt.Host).Append(':').Append(opt.Port);
            if (opt.Through != null)
                sb.Append(" /through:").Append(opt.Through);
            if (opt.FullScreen)
                sb.Append(" /fullscreen");
            if (opt.ViewOnly)
                sb.Append(" /noinput");
            foreach (string extra in opt.ExtraRadminArgs)
                sb.Append(' ').Append(extra);
            return sb.ToString();
        }

        static string FindRadmin(string explicitPath)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitPath))
                candidates.Add(explicitPath);
            string env = Environment.GetEnvironmentVariable("RADMIN_PATH");
            if (!string.IsNullOrEmpty(env))
                candidates.Add(env);
            candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Radmin.exe"));
            foreach (string pf in new string[] {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (!string.IsNullOrEmpty(pf))
                    candidates.Add(Path.Combine(pf, @"Radmin Viewer 3\Radmin.exe"));
            }

            foreach (string c in candidates)
            {
                string path = c;
                if (Directory.Exists(path))
                    path = Path.Combine(path, "Radmin.exe");
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
            // An explicit path that does not exist is an error, not something to silently replace.
            return null;
        }

        internal static void Log(string message)
        {
            if (verbose)
                Console.WriteLine("[RadminAuto] " + message);
        }

        static void PrintUsage()
        {
            Console.WriteLine(
@"RadminAuto - open Radmin Viewer and log in automatically.

Usage:
  RadminAuto <host[:port]> <user> <password> [options]
  RadminAuto --host <host> [--port <port>] --user <user> --pass <password> [options]
  RadminAuto ""radminauto://connect?host=<host>&port=<port>&user=<user>&pass=<password>""
  RadminAuto --register | --unregister

Options:
  --host <host>        IP or host name of the Radmin Server
  --port <port>        Radmin Server port (default 4899)
  -u, --user <user>    user name (omit for password-only Radmin security)
  -p, --pass <pass>    password
  --pass-env <VAR>     read the password from environment variable VAR
  --domain <domain>    domain, for Windows security logins that ask for one
  --through <h[:p]>    connect through an intermediate Radmin Server
  --fullscreen         open in full screen mode (/fullscreen)
  --viewonly           view only, no keyboard/mouse input (/noinput)
  --radmin <path>      path to Radmin.exe (or set RADMIN_PATH)
  --timeout <sec>      seconds to wait for the login dialog (default 30)
  --no-submit          fill the dialog but do not press OK
  -v, --verbose        print what is happening
  -h, --help           show this help
  -- <args...>         pass the remaining arguments to Radmin.exe unchanged
  --register           make radminauto:// links open this program (current user)
  --unregister         remove the radminauto:// link handler

Link parameters: host (or ip), port, user, pass (or password), domain,
through, fullscreen=1, viewonly=1. URL-encode values (encodeURIComponent).

Exit codes:
  0 ok, 1 bad arguments, 2 Radmin.exe not found, 3 login dialog not shown,
  4 login rejected / dialog still open, 5 could not fill the dialog");
        }
    }

    internal sealed class Options
    {
        public string Host;
        public int Port = 4899;
        public string User = "";
        public string Password = "";
        public string Domain;
        public string Through;
        public bool FullScreen;
        public bool ViewOnly;
        public string RadminPath;
        public int TimeoutSeconds = 30;
        public bool NoSubmit;
        public bool Verbose;
        public bool ShowHelp;
        public List<string> ExtraRadminArgs = new List<string>();

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            List<string> positional = new List<string>();
            bool portGiven = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "-h": case "--help": case "/?":
                        o.ShowHelp = true;
                        return o;
                    case "--host": o.Host = Value(args, ref i); break;
                    case "--port": o.Port = ParsePort(Value(args, ref i)); portGiven = true; break;
                    case "-u": case "--user": o.User = Value(args, ref i); break;
                    case "-p": case "--pass": case "--password": o.Password = Value(args, ref i); break;
                    case "--pass-env":
                        {
                            string name = Value(args, ref i);
                            string v = Environment.GetEnvironmentVariable(name);
                            if (v == null)
                                throw new ArgumentException("environment variable " + name + " is not set");
                            o.Password = v;
                            break;
                        }
                    case "--domain": o.Domain = Value(args, ref i); break;
                    case "--through": o.Through = Value(args, ref i); break;
                    case "--fullscreen": o.FullScreen = true; break;
                    case "--viewonly": o.ViewOnly = true; break;
                    case "--radmin": o.RadminPath = Value(args, ref i); break;
                    case "--timeout":
                        {
                            int t;
                            if (!int.TryParse(Value(args, ref i), out t) || t <= 0)
                                throw new ArgumentException("--timeout must be a positive number of seconds");
                            o.TimeoutSeconds = t;
                            break;
                        }
                    case "--no-submit": o.NoSubmit = true; break;
                    case "-v": case "--verbose": o.Verbose = true; break;
                    case "--":
                        for (i++; i < args.Length; i++)
                            o.ExtraRadminArgs.Add(args[i]);
                        break;
                    default:
                        if (a.StartsWith("-") && a.Length > 1)
                            throw new ArgumentException("unknown option " + a);
                        positional.Add(a);
                        break;
                }
            }

            if (positional.Count > 3)
                throw new ArgumentException("too many arguments; expected <host[:port]> <user> <password>");
            if (positional.Count >= 1 && o.Host == null) o.Host = positional[0];
            if (positional.Count >= 2) o.User = positional[1];
            if (positional.Count >= 3) o.Password = positional[2];

            o.Finish(portGiven);
            return o;
        }

        // radminauto://connect?host=1.2.3.4&port=4899&user=u&pass=p&fullscreen=1
        // Only connection settings are read from a link: nothing in it can choose which program runs.
        public static Options FromUrl(string url)
        {
            Options o = new Options();
            bool portGiven = false;

            string rest = url.Substring(url.IndexOf(':') + 1).TrimStart('/');
            int hash = rest.IndexOf('#');
            if (hash >= 0)
                rest = rest.Substring(0, hash);
            int q = rest.IndexOf('?');
            string authority = (q >= 0 ? rest.Substring(0, q) : rest).TrimEnd('/');
            string query = q >= 0 ? rest.Substring(q + 1) : "";

            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0)
                    continue;
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq)).ToLowerInvariant();
                string val = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1));
                switch (key)
                {
                    case "host": case "ip": o.Host = val; break;
                    case "port": o.Port = ParsePort(val); portGiven = true; break;
                    case "user": case "u": o.User = val; break;
                    case "pass": case "password": case "p": o.Password = val; break;
                    case "domain": o.Domain = val; break;
                    case "through": o.Through = val.Length > 0 ? val : null; break;
                    case "fullscreen": o.FullScreen = IsTrue(val); break;
                    case "viewonly": o.ViewOnly = IsTrue(val); break;
                    // Anything else (radmin path, timeout, extra Radmin switches...) is deliberately ignored.
                }
            }

            // Also accept radminauto://1.2.3.4:4899/?user=...
            if (string.IsNullOrEmpty(o.Host) && authority.Length > 0
                && !authority.Equals("connect", StringComparison.OrdinalIgnoreCase))
                o.Host = Uri.UnescapeDataString(authority);

            o.Finish(portGiven);
            return o;
        }

        void Finish(bool portGiven)
        {
            if (string.IsNullOrEmpty(Host))
                throw new ArgumentException("missing host");

            int colon = Host.LastIndexOf(':');
            if (colon > 0)
            {
                int p = ParsePort(Host.Substring(colon + 1));
                if (!portGiven) Port = p;
                Host = Host.Substring(0, colon);
            }

            // Host and through end up on Radmin's command line, so they must not contain spaces,
            // quotes or slashes that would add extra Radmin switches.
            if (!IsHostName(Host))
                throw new ArgumentException("invalid host '" + Host + "'");
            if (Through != null)
            {
                int tc = Through.LastIndexOf(':');
                string th = tc > 0 ? Through.Substring(0, tc) : Through;
                if (tc > 0)
                    ParsePort(Through.Substring(tc + 1));
                if (!IsHostName(th))
                    throw new ArgumentException("invalid through host '" + Through + "'");
            }
        }

        static bool IsHostName(string s)
        {
            if (s.Length == 0 || s.Length > 253)
                return false;
            foreach (char c in s)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == '.' || c == '-' || c == '_'))
                    return false;
            }
            return true;
        }

        static bool IsTrue(string v)
        {
            v = v.Trim().ToLowerInvariant();
            return v == "" || v == "1" || v == "true" || v == "yes" || v == "on";
        }

        static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException(args[i] + " needs a value");
            return args[++i];
        }

        static int ParsePort(string s)
        {
            int p;
            if (!int.TryParse(s, out p) || p < 1 || p > 65535)
                throw new ArgumentException("invalid port '" + s + "'");
            return p;
        }
    }

    internal static class UrlProtocol
    {
        const string Scheme = "radminauto";
        const string KeyPath = @"Software\Classes\" + Scheme;

        public static string FindUrlArgument(string[] args)
        {
            foreach (string a in args)
            {
                if (a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        // Per-user registration (HKCU), so no administrator rights are needed.
        public static int Register()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                key.SetValue("", "URL:RadminAuto Protocol");
                key.SetValue("URL Protocol", "");
                using (RegistryKey icon = key.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", "\"" + exe + "\",0");
                using (RegistryKey cmd = key.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", "\"" + exe + "\" --url \"%1\"");
            }
            Console.WriteLine(Scheme + ":// links now open \"" + exe + "\".");
            Console.WriteLine("Run --register again if you move this file.");
            return 0;
        }

        public static int Unregister()
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
            Console.WriteLine(Scheme + ":// link handler removed.");
            return 0;
        }
    }

    // A Radmin window that contains a password edit box.
    internal sealed class LoginDialog
    {
        public IntPtr Handle;
        public string Title;
        public IntPtr PasswordEdit;
        public IntPtr UserEdit;
        public IntPtr DomainEdit;

        public static List<LoginDialog> FindAll()
        {
            HashSet<uint> radminPids = new HashSet<uint>();
            foreach (Process p in Process.GetProcessesByName("Radmin"))
            {
                radminPids.Add((uint)p.Id);
                p.Dispose();
            }

            List<LoginDialog> result = new List<LoginDialog>();
            if (radminPids.Count == 0)
                return result;

            Native.EnumWindows(delegate (IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (radminPids.Contains(pid) && Native.IsWindowVisible(hwnd))
                {
                    LoginDialog d = TryCreate(hwnd);
                    if (d != null)
                        result.Add(d);
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        static LoginDialog TryCreate(IntPtr hwnd)
        {
            // Children come back in z-order, which for dialogs is the tab order:
            // user name, password, then (if present) domain.
            List<IntPtr> edits = new List<IntPtr>();
            Native.EnumChildWindows(hwnd, delegate (IntPtr child, IntPtr lParam)
            {
                if (Native.ClassNameOf(child).Equals("Edit", StringComparison.OrdinalIgnoreCase)
                    && Native.IsWindowVisible(child) && Native.IsWindowEnabled(child))
                    edits.Add(child);
                return true;
            }, IntPtr.Zero);

            int pwIndex = edits.FindIndex(delegate (IntPtr e)
            {
                return (Native.GetWindowLong(e, Native.GWL_STYLE) & Native.ES_PASSWORD) != 0;
            });
            if (pwIndex < 0)
                return null;

            LoginDialog d = new LoginDialog();
            d.Handle = hwnd;
            d.Title = Native.TextOf(hwnd);
            d.PasswordEdit = edits[pwIndex];
            for (int i = 0; i < edits.Count; i++)
            {
                if (i == pwIndex)
                    continue;
                if (i < pwIndex && d.UserEdit == IntPtr.Zero)
                    d.UserEdit = edits[i];
                else if (i > pwIndex && d.DomainEdit == IntPtr.Zero)
                    d.DomainEdit = edits[i];
            }
            return d;
        }

        public bool Fill(string user, string password, string domain)
        {
            if (UserEdit != IntPtr.Zero)
            {
                if (!Native.SetText(UserEdit, user ?? ""))
                    return false;
            }
            else if (!string.IsNullOrEmpty(user))
            {
                Program.Log("Dialog has no user name field; ignoring the user name.");
            }

            if (domain != null && DomainEdit != IntPtr.Zero && !Native.SetText(DomainEdit, domain))
                return false;

            return Native.SetText(PasswordEdit, password ?? "");
        }

        // Presses OK, trying progressively more forceful ways until the dialog closes.
        public bool Submit()
        {
            IntPtr ok = FindOkButton();
            int okId = ok != IntPtr.Zero ? Native.GetDlgCtrlID(ok) : Native.IDOK;
            Program.Log(ok != IntPtr.Zero
                ? "OK button: id=" + okId + " text=\"" + Native.TextOf(ok) + "\""
                : "OK button not found, using IDOK");

            Native.ForceForeground(Handle);

            if (ok != IntPtr.Zero)
            {
                Program.Log("Submit: BM_CLICK on OK button");
                Native.PostMessage(ok, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                if (Program.WaitUntilGone(Handle, 3000))
                    return true;
            }

            Program.Log("Submit: WM_COMMAND id=" + okId);
            Native.PostMessage(Handle, Native.WM_COMMAND, (IntPtr)((Native.BN_CLICKED << 16) | (okId & 0xFFFF)), ok);
            if (Program.WaitUntilGone(Handle, 3000))
                return true;

            // The dialog manager turns Enter in an edit box into a click on the default button.
            Program.Log("Submit: Enter key posted to the password box");
            Native.PostMessage(PasswordEdit, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, (IntPtr)0x001C0001);
            Native.PostMessage(PasswordEdit, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, (IntPtr)unchecked((int)0xC01C0001));
            if (Program.WaitUntilGone(Handle, 3000))
                return true;

            // Last resort: a real key press, only if the dialog really has the keyboard focus.
            Native.ForceForeground(Handle);
            if (Native.GetForegroundWindow() == Handle)
            {
                Program.Log("Submit: real Enter key press");
                Native.PressKey(Native.VK_RETURN);
                if (Program.WaitUntilGone(Handle, 5000))
                    return true;
            }
            else
            {
                Program.Log("Submit: could not bring the dialog to the foreground");
            }
            return false;
        }

        IntPtr FindOkButton()
        {
            IntPtr byId = IntPtr.Zero, byText = IntPtr.Zero, byDefault = IntPtr.Zero;
            Native.EnumChildWindows(Handle, delegate (IntPtr child, IntPtr lParam)
            {
                if (!Native.ClassNameOf(child).Equals("Button", StringComparison.OrdinalIgnoreCase)
                    || !Native.IsWindowVisible(child) || !Native.IsWindowEnabled(child))
                    return true;
                int kind = Native.GetWindowLong(child, Native.GWL_STYLE) & Native.BS_TYPEMASK;
                if (kind != Native.BS_PUSHBUTTON && kind != Native.BS_DEFPUSHBUTTON)
                    return true;
                if (byId == IntPtr.Zero && Native.GetDlgCtrlID(child) == Native.IDOK)
                    byId = child;
                if (byText == IntPtr.Zero && Native.TextOf(child).Replace("&", "").Trim()
                        .Equals("OK", StringComparison.OrdinalIgnoreCase))
                    byText = child;
                if (byDefault == IntPtr.Zero && kind == Native.BS_DEFPUSHBUTTON)
                    byDefault = child;
                return true;
            }, IntPtr.Zero);
            if (byId != IntPtr.Zero) return byId;
            if (byText != IntPtr.Zero) return byText;
            return byDefault;
        }

        public void DumpControls()
        {
            Native.EnumChildWindows(Handle, delegate (IntPtr child, IntPtr lParam)
            {
                string cls = Native.ClassNameOf(child);
                bool isEdit = cls.Equals("Edit", StringComparison.OrdinalIgnoreCase);
                Program.Log(string.Format("  control class={0} id={1} style=0x{2:X8} visible={3} enabled={4} text=\"{5}\"",
                    cls, Native.GetDlgCtrlID(child), Native.GetWindowLong(child, Native.GWL_STYLE),
                    Native.IsWindowVisible(child), Native.IsWindowEnabled(child),
                    isEdit ? "" : Native.TextOf(child)));
                return true;
            }, IntPtr.Zero);
        }
    }

    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int ES_PASSWORD = 0x20;
        public const uint WM_SETTEXT = 0x000C;
        public const uint WM_COMMAND = 0x0111;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint BM_CLICK = 0x00F5;
        public const int BS_TYPEMASK = 0x0F;
        public const int BS_PUSHBUTTON = 0x00;
        public const int BS_DEFPUSHBUTTON = 0x01;
        public const int VK_RETURN = 0x0D;
        const int VK_MENU = 0x12;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const int SW_RESTORE = 9;
        const int SW_HIDE = 0;
        const uint MB_ICONERROR = 0x10;
        const uint MB_SETFOREGROUND = 0x10000;
        const uint MB_TOPMOST = 0x40000;
        public const int IDOK = 1;
        public const int BN_CLICKED = 0;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        static extern uint GetConsoleProcessList(uint[] processIds, uint count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int maxCount);

        public static string ClassNameOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TextOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // Windows only lets the foreground process change the foreground window; attaching to the
        // current foreground thread's input queue (plus an Alt tap) lifts that restriction.
        public static void ForceForeground(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            if (GetForegroundWindow() == hwnd)
                return;

            uint pid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(me, fgThread, false);
            }
            Thread.Sleep(150);
        }

        // Hides the console window Windows creates when a link starts us, but never the
        // console of a cmd window we were started from (that one has other processes attached).
        public static void HideOwnConsole()
        {
            IntPtr console = GetConsoleWindow();
            if (console != IntPtr.Zero && GetConsoleProcessList(new uint[4], 4) == 1)
                ShowWindow(console, SW_HIDE);
        }

        public static void ShowError(string message)
        {
            MessageBox(IntPtr.Zero, message, "RadminAuto", MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST);
        }

        public static void PressKey(int vk)
        {
            keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        // WM_SETTEXT is marshalled across processes, so it works on another app's edit boxes.
        public static bool SetText(IntPtr hwnd, string text)
        {
            IntPtr result;
            IntPtr ok = SendMessageTimeout(hwnd, WM_SETTEXT, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 5000, out result);
            return ok != IntPtr.Zero && result != IntPtr.Zero;
        }
    }
}
