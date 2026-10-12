// RadminAuto - launches Radmin Viewer for a host and fills in the login dialog.
//
// Radmin Viewer has no command-line switch for user name / password, so this
// wrapper starts "Radmin.exe /connect:host:port", waits for the security
// dialog, writes the credentials into its edit boxes and presses OK.
//
// Written in C# 5 so it builds with the csc.exe that ships with .NET Framework 4.x.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

        static int Main(string[] args)
        {
            Options opt;
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
            if (opt.ShowHelp)
            {
                PrintUsage();
                return ExitOk;
            }
            verbose = opt.Verbose;

            string radmin = FindRadmin(opt.RadminPath);
            if (radmin == null)
            {
                Console.Error.WriteLine("Error: Radmin.exe not found. Use --radmin <path> or set RADMIN_PATH.");
                return ExitRadminNotFound;
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
                Console.Error.WriteLine("Error: login dialog did not appear within " + opt.TimeoutSeconds +
                    "s (host unreachable, or credentials already saved in Radmin?).");
                return ExitNoLoginDialog;
            }
            Log("Login dialog found: \"" + dlg.Title + "\" (user field: " + (dlg.UserEdit != IntPtr.Zero ? "yes" : "no") + ")");

            if (!dlg.Fill(opt.User, opt.Password, opt.Domain))
            {
                Console.Error.WriteLine("Error: could not write credentials into the login dialog.");
                return ExitFillFailed;
            }
            if (opt.NoSubmit)
            {
                Console.WriteLine("Credentials filled (not submitted because of --no-submit).");
                return ExitOk;
            }

            existing.Add(dlg.Handle);
            dlg.Submit();

            if (!WaitUntilGone(dlg.Handle, 10000))
            {
                Console.Error.WriteLine("Error: login dialog is still open after pressing OK.");
                return ExitLoginRejected;
            }
            // A wrong password makes Radmin ask again with a fresh dialog.
            if (WaitForNewDialog(existing, opt.Host, 2000) != null)
            {
                Console.Error.WriteLine("Error: Radmin asked for credentials again (wrong user name or password?).");
                return ExitLoginRejected;
            }

            Console.WriteLine("Logged in to " + opt.Host + ":" + opt.Port);
            return ExitOk;
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

        static bool WaitUntilGone(IntPtr hwnd, int timeoutMs)
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

            if (string.IsNullOrEmpty(o.Host))
                throw new ArgumentException("missing host");

            // Accept "host:port" unless it looks like an IPv6 address.
            int colon = o.Host.LastIndexOf(':');
            if (colon > 0 && o.Host.IndexOf(':') == colon)
            {
                int p = ParsePort(o.Host.Substring(colon + 1));
                if (!portGiven) o.Port = p;
                o.Host = o.Host.Substring(0, colon);
            }
            return o;
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

        public void Submit()
        {
            // Send BN_CLICKED for IDOK to the dialog: the same thing pressing the OK button does,
            // but it does not need the window to be in the foreground.
            IntPtr ok = Native.GetDlgItem(Handle, Native.IDOK);
            IntPtr wParam = (IntPtr)((Native.BN_CLICKED << 16) | Native.IDOK);
            Native.PostMessage(Handle, Native.WM_COMMAND, wParam, ok);
        }
    }

    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int ES_PASSWORD = 0x20;
        public const uint WM_SETTEXT = 0x000C;
        public const uint WM_COMMAND = 0x0111;
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
        public static extern IntPtr GetDlgItem(IntPtr dialog, int id);

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

        // WM_SETTEXT is marshalled across processes, so it works on another app's edit boxes.
        public static bool SetText(IntPtr hwnd, string text)
        {
            IntPtr result;
            IntPtr ok = SendMessageTimeout(hwnd, WM_SETTEXT, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 5000, out result);
            return ok != IntPtr.Zero && result != IntPtr.Zero;
        }
    }
}
