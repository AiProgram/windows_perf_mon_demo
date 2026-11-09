// WindowsPerfMonDemo.cs
//
// Watches ETW "UI delay" events emitted by the Microsoft-Windows-Win32k
// provider and appends every delay greater than a threshold (default
// 200 ms) to a local JSONL file, one JSON object per line.
//
// The events are the same ones xperf's "uidelay" action consumes:
//
//     EventId  Task                         Meaning
//     -------  ---------------------------  ------------------------------------
//     37       InputProcessDelay            input was queued, app did not pick it up
//     38       MessageCheckDelay            delay measured when the thread checked
//                                           its message queue (DelayTimeMs)
//     172      ImmersiveInputProcessDelay   UWP variant of 37
//     173      ImmersiveMessageCheckDelay   UWP variant of 38
//
// They are enabled through the Win32k keywords
//     win:ResponseTime     0x0001000000000000
//     UIUnresponsiveness   0x0000000000040000
// at informational level, which keeps the trace small (Win32k in verbose mode
// is extremely chatty).
//
// Payload decoding is delegated to TDH (TdhGetProperty*): the manifest schema
// is looked up at runtime, so no hard-coded binary layout is used and the
// program keeps working if the payload grows a field.
//
// Win32k lives in kernel mode, so starting the session needs an elevated
// token. When started without admin rights the program re-launches itself
// through UAC (unless --no-elevate is given).
//
// Build:  powershell -ExecutionPolicy Bypass -File build.ps1
//         (uses the .NET Framework C# compiler that ships with Windows)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace WindowsPerfMonDemo
{
    // ------------------------------------------------------------------ args

    internal sealed class Options
    {
        public uint MinDelayMs = 200;
        public string OutputPath;          // null => default file next to the exe
        public string EtlPath;             // replay an existing ETL instead of live
        public int DurationSeconds;        // 0 => run until Ctrl+C
        public string SessionName;         // null => WindowsPerfMonDemo-<pid>
        public bool NoElevate;
        public bool AlreadyElevated;       // internal, set by the UAC relaunch
        public bool Quiet;
        public bool Probe;                 // print every Win32k delay event, ignore threshold
        public bool ProbeSchema;           // dump the decoded schema of each Win32k event id
        public ulong MatchAnyKeywords = 0x0001000000000000UL | 0x0000000000040000UL;
        public byte Level = 4;             // informational
        public bool CountAll;              // diagnostics: count events of every provider
        public bool ShowHelp;

        public static bool TryParse(string[] args, Options opt, out string error)
        {
            error = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string value = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0)
                {
                    value = a.Substring(eq + 1);
                    a = a.Substring(0, eq);
                }

                switch (a)
                {
                    case "-h":
                    case "--help":
                        opt.ShowHelp = true;
                        break;
                    case "--min-ms":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        uint ms;
                        if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
                        {
                            error = "--min-ms expects a non-negative integer, got '" + value + "'";
                            return false;
                        }
                        opt.MinDelayMs = ms;
                        break;
                    case "--out":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        opt.OutputPath = value;
                        break;
                    case "--etl":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        opt.EtlPath = value;
                        break;
                    case "--duration":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        int sec;
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sec) || sec < 0)
                        {
                            error = "--duration expects a non-negative number of seconds, got '" + value + "'";
                            return false;
                        }
                        opt.DurationSeconds = sec;
                        break;
                    case "--session":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        opt.SessionName = value;
                        break;
                    case "--no-elevate":
                        opt.NoElevate = true;
                        break;
                    case "--elevated":
                        opt.AlreadyElevated = true;
                        break;
                    case "--quiet":
                        opt.Quiet = true;
                        break;
                    case "--probe":
                        opt.Probe = true;
                        break;
                    case "--probe-schema":
                        opt.ProbeSchema = true;
                        break;
                    case "--keywords":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            opt.MatchAnyKeywords = ulong.MaxValue;
                        }
                        else
                        {
                            string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value;
                            ulong mask;
                            if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask))
                            {
                                error = "--keywords expects a hex mask or 'all', got '" + value + "'";
                                return false;
                            }
                            opt.MatchAnyKeywords = mask;
                        }
                        break;
                    case "--level":
                        if (!TakeValue(args, ref i, ref value, out error)) return false;
                        byte lvl;
                        if (!byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out lvl) || lvl < 1 || lvl > 5)
                        {
                            error = "--level expects 1..5, got '" + value + "'";
                            return false;
                        }
                        opt.Level = lvl;
                        break;
                    case "--count-all":
                        opt.CountAll = true;
                        break;
                    default:
                        error = "unknown argument '" + args[i] + "'";
                        return false;
                }
            }
            return true;
        }

        private static bool TakeValue(string[] args, ref int i, ref string inline, out string error)
        {
            error = null;
            if (inline != null) return true;
            if (i + 1 >= args.Length)
            {
                error = "missing value after '" + args[i] + "'";
                return false;
            }
            i++;
            inline = args[i];
            return true;
        }

        public static void PrintUsage()
        {
            Console.WriteLine(@"WindowsPerfMonDemo - log Win32k UI delays > N ms to a JSONL file

Usage:
  WindowsPerfMonDemo [options]

Options:
  --min-ms <n>       delay threshold in milliseconds, strictly greater than
                     (default 200)
  --out <path>       output .jsonl file (default: uidelay-yyyyMMdd.jsonl next
                     to the executable; rotates at midnight)
  --etl <path>       replay an existing .etl instead of starting a live session
  --duration <sec>   stop after N seconds (default 0 = until Ctrl+C)
  --session <name>   ETW session name (default WindowsPerfMonDemo-<pid>)
  --no-elevate       fail instead of asking for administrator rights
  --quiet            do not print one line per recorded event
  --probe            print every Win32k delay event, even below the threshold
  --probe-schema     dump the decoded payload schema of each Win32k event id
                     (diagnostics; use with --etl)
  --keywords <mask>  keyword mask in hex or 'all' (default: ResponseTime |
                     UIUnresponsiveness)
  --level <1..5>     ETW level to enable (default 4 informational)
  --count-all        diagnostics: count events from every provider and print
                     the top providers at exit
  -h, --help         show this help

Exit codes:
  0 ok, 1 runtime error, 2 bad arguments, 3 etl not found,
  5 administrator rights required, 7 ETW session could not be started");

            Console.WriteLine();
            Console.WriteLine("Notes:");
            Console.WriteLine("  * starting a live Win32k session needs an elevated token;");
            Console.WriteLine("    the program relaunches itself through UAC when needed.");
            Console.WriteLine("  * --etl works without elevation and is handy for validation.");
        }
    }

    // ------------------------------------------------------------------ entry

    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitRuntime = 1;
        private const int ExitUsage = 2;
        private const int ExitNoEtl = 3;
        private const int ExitNeedAdmin = 5;
        private const int ExitNoSession = 7;

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }

            Options opt = new Options();
            string parseError;
            if (!Options.TryParse(args, opt, out parseError))
            {
                Console.Error.WriteLine("error: " + parseError);
                Console.Error.WriteLine();
                Options.PrintUsage();
                return ExitUsage;
            }
            if (opt.ShowHelp)
            {
                Options.PrintUsage();
                return ExitOk;
            }

            if (opt.EtlPath == null && !IsAdministrator())
            {
                if (opt.NoElevate || opt.AlreadyElevated)
                {
                    Console.Error.WriteLine("error: administrator rights are required.");
                    Console.Error.WriteLine("       Win32k is a kernel-mode ETW provider, so a live");
                    Console.Error.WriteLine("       session can only be started with an elevated token.");
                    Console.Error.WriteLine("       Run WindowsPerfMonDemo from an elevated prompt.");
                    return ExitNeedAdmin;
                }
                return RelaunchElevated(args);
            }

            try
            {
                using (UiDelayMonitor monitor = new UiDelayMonitor(opt))
                {
                    return monitor.Run();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("fatal: " + ex.Message);
                if (Environment.GetEnvironmentVariable("UIDELAY_DEBUG") != null)
                    Console.Error.WriteLine(ex.ToString());
                return ExitRuntime;
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int RelaunchElevated(string[] args)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('"');
                sb.Append(a.Replace("\"", "\\\""));
                sb.Append('"');
            }
            sb.Append(" --elevated");

            ProcessStartInfo psi = new ProcessStartInfo(exe, sb.ToString());
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WorkingDirectory = Environment.CurrentDirectory;
            try
            {
                Process.Start(psi);
                Console.WriteLine("administrator rights requested; continue in the elevated console.");
                return ExitOk;
            }
            catch (Win32Exception ex)
            {
                Console.Error.WriteLine("error: elevation was declined or failed: " + ex.Message);
                return ExitNeedAdmin;
            }
        }
    }

    // ------------------------------------------------------------ JSON writer

    internal sealed class DelayRecord
    {
        public DateTime UtcTime;
        public int EventId;
        public int Version;
        public string EventName;
        public string DelaySource;
        public uint DelayMs;
        public uint? DelayTimeMs;
        public uint? TimeSinceInputRemoveMs;
        public uint? TimeSinceOldestInputMs;
        public uint? Flags;
        public uint? MessageId;
        public ulong? WParam;
        public int ProcessId;
        public int ThreadId;
        public string ProcessName;
        public string ImagePath;
        public string ClassName;
        public string TopLevelClassName;
        public string PackageMoniker;
        public string AppUserModelId;

        public string OneLine()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff}  {1,6} ms  {2,-28} pid={3,-6} {4}  class={5} top={6}",
                UtcTime.ToLocalTime(), DelayMs, EventName, ProcessId,
                string.IsNullOrEmpty(ProcessName) ? "?" : ProcessName,
                ClassName, TopLevelClassName);
        }
    }

    internal sealed class JsonlWriter : IDisposable
    {
        private readonly bool _rotateDaily;
        private readonly string _explicitPath;
        private readonly object _sync = new object();
        private StreamWriter _writer;
        private DateTime _day;
        private bool _disposed;

        public string CurrentPath { get; private set; }

        public JsonlWriter(string requestedPath)
        {
            if (string.IsNullOrEmpty(requestedPath))
            {
                _rotateDaily = true;
                string dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                _explicitPath = Path.Combine(dir, "uidelay-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            }
            else
            {
                _rotateDaily = false;
                _explicitPath = Path.GetFullPath(requestedPath);
            }
            Open();
        }

        private void Open()
        {
            string path = _explicitPath;
            if (_rotateDaily)
            {
                _day = DateTime.Now.Date;
                string dir = Path.GetDirectoryName(_explicitPath);
                string name = "uidelay-" + _day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl";
                path = Path.Combine(dir, name);
            }
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
            _writer.AutoFlush = true;
            CurrentPath = path;
        }

        public void Write(DelayRecord r)
        {
            string line = Format(r);
            lock (_sync)
            {
                if (_disposed) return;
                if (_rotateDaily && DateTime.Now.Date != _day)
                {
                    _writer.Flush();
                    _writer.Dispose();
                    Open();
                }
                _writer.WriteLine(line);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_writer != null)
                {
                    try { _writer.Flush(); } catch (IOException) { }
                    _writer.Dispose();
                }
            }
        }

        private static string Format(DelayRecord r)
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append('{');
            AppendString(sb, "time", r.UtcTime.ToLocalTime().ToString("o", CultureInfo.InvariantCulture));
            sb.Append(',');
            AppendString(sb, "timeUtc", r.UtcTime.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(',');
            AppendNumber(sb, "delayMs", r.DelayMs);
            sb.Append(',');
            AppendString(sb, "delaySource", r.DelaySource);
            sb.Append(',');
            AppendString(sb, "delayType", r.EventName);
            sb.Append(',');
            AppendNumber(sb, "eventId", (uint)r.EventId);
            sb.Append(',');
            AppendNumber(sb, "version", (uint)r.Version);
            sb.Append(',');
            AppendNumber(sb, "processId", (uint)r.ProcessId);
            sb.Append(',');
            AppendNumber(sb, "threadId", (uint)r.ThreadId);
            if (!string.IsNullOrEmpty(r.ProcessName))
            {
                sb.Append(',');
                AppendString(sb, "processName", r.ProcessName);
            }
            if (!string.IsNullOrEmpty(r.ImagePath))
            {
                sb.Append(',');
                AppendString(sb, "imagePath", r.ImagePath);
            }
            if (!string.IsNullOrEmpty(r.ClassName))
            {
                sb.Append(',');
                AppendString(sb, "className", r.ClassName);
            }
            if (!string.IsNullOrEmpty(r.TopLevelClassName))
            {
                sb.Append(',');
                AppendString(sb, "topLevelClassName", r.TopLevelClassName);
            }
            if (!string.IsNullOrEmpty(r.PackageMoniker))
            {
                sb.Append(',');
                AppendString(sb, "packageMoniker", r.PackageMoniker);
            }
            if (!string.IsNullOrEmpty(r.AppUserModelId))
            {
                sb.Append(',');
                AppendString(sb, "appUserModelId", r.AppUserModelId);
            }
            AppendOptionalNumber(sb, "delayTimeMs", r.DelayTimeMs);
            AppendOptionalNumber(sb, "timeSinceInputRemoveMs", r.TimeSinceInputRemoveMs);
            AppendOptionalNumber(sb, "timeSinceOldestInputMs", r.TimeSinceOldestInputMs);
            AppendOptionalNumber(sb, "messageId", r.MessageId);
            if (r.Flags.HasValue)
            {
                sb.Append(',');
                AppendString(sb, "flags", "0x" + r.Flags.Value.ToString("X", CultureInfo.InvariantCulture));
            }
            if (r.WParam.HasValue)
            {
                sb.Append(',');
                AppendString(sb, "wParam", "0x" + r.WParam.Value.ToString("X", CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendNumber(StringBuilder sb, string name, uint value)
        {
            AppendName(sb, name);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendOptionalNumber(StringBuilder sb, string name, uint? value)
        {
            if (!value.HasValue) return;
            sb.Append(',');
            AppendNumber(sb, name, value.Value);
        }

        private static void AppendString(StringBuilder sb, string name, string value)
        {
            AppendName(sb, name);
            sb.Append('"');
            AppendEscaped(sb, value);
            sb.Append('"');
        }

        private static void AppendName(StringBuilder sb, string name)
        {
            sb.Append('"');
            sb.Append(name);
            sb.Append("\":");
        }

        private static void AppendEscaped(StringBuilder sb, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------ TDH payload

    internal static class Tdh
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTY_DATA_DESCRIPTOR
        {
            public IntPtr PropertyName;
            public uint ArrayIndex;
            public uint Reserved;
        }

        private const uint ErrorSuccess = 0;

        [DllImport("tdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint TdhGetPropertySize(
            IntPtr eventRecord, uint tdhContextCount, IntPtr tdhContext,
            uint propertyDataCount, ref PROPERTY_DATA_DESCRIPTOR propertyData, out uint propertySize);

        [DllImport("tdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint TdhGetProperty(
            IntPtr eventRecord, uint tdhContextCount, IntPtr tdhContext,
            uint propertyDataCount, ref PROPERTY_DATA_DESCRIPTOR propertyData,
            uint bufferSize, byte[] buffer);

        private static readonly Dictionary<string, IntPtr> NamePointers =
            new Dictionary<string, IntPtr>(StringComparer.Ordinal);

        private static readonly object NameLock = new object();
        private static byte[] _buffer = new byte[256];

        private static IntPtr NameOf(string name)
        {
            lock (NameLock)
            {
                IntPtr p;
                if (!NamePointers.TryGetValue(name, out p))
                {
                    p = Marshal.StringToHGlobalUni(name);
                    NamePointers[name] = p;
                }
                return p;
            }
        }

        public static bool TryGetSize(IntPtr record, string name, out uint size)
        {
            PROPERTY_DATA_DESCRIPTOR desc = new PROPERTY_DATA_DESCRIPTOR();
            desc.PropertyName = NameOf(name);
            desc.ArrayIndex = uint.MaxValue;
            return TdhGetPropertySize(record, 0, IntPtr.Zero, 1, ref desc, out size) == ErrorSuccess;
        }

        public static bool TryGetUInt32(IntPtr record, string name, out uint value)
        {
            value = 0;
            uint size;
            if (!TryGetSize(record, name, out size) || size < 4) return false;
            if (!Fetch(record, name, size)) return false;
            value = BitConverter.ToUInt32(_buffer, 0);
            return true;
        }

        public static bool TryGetUInt64(IntPtr record, string name, out ulong value)
        {
            value = 0;
            uint size;
            if (!TryGetSize(record, name, out size) || size < 8) return false;
            if (!Fetch(record, name, size)) return false;
            value = BitConverter.ToUInt64(_buffer, 0);
            return true;
        }

        public static bool TryGetString(IntPtr record, string name, out string value)
        {
            value = null;
            uint size;
            if (!TryGetSize(record, name, out size)) return false;
            if (size == 0)
            {
                value = string.Empty;
                return true;
            }
            if (!Fetch(record, name, size)) return false;
            int chars = (int)(size / 2);
            int len = 0;
            while (len < chars)
            {
                if (_buffer[len * 2] == 0 && _buffer[len * 2 + 1] == 0) break;
                len++;
            }
            value = Encoding.Unicode.GetString(_buffer, 0, len * 2);
            return true;
        }

        private static bool Fetch(IntPtr record, string name, uint size)
        {
            if (_buffer.Length < size) _buffer = new byte[size + 64];
            PROPERTY_DATA_DESCRIPTOR desc = new PROPERTY_DATA_DESCRIPTOR();
            desc.PropertyName = NameOf(name);
            desc.ArrayIndex = uint.MaxValue;
            return TdhGetProperty(record, 0, IntPtr.Zero, 1, ref desc, size, _buffer) == ErrorSuccess;
        }
    }

    // -------------------------------------------------------- Win32k schema

    internal sealed class EventSchema
    {
        public bool IsDelayEvent;
        public bool HasDelayTimeMs;
        public bool HasTimeSinceOldestInputMs;
        public bool HasTimeSinceInputRemoveMs;
        public bool HasFlags;
        public bool HasMessageId;
        public bool HasWParam;
        public bool HasClassName;
        public bool HasTopLevelClassName;
        public bool HasImagePath;
        public bool HasPackageMoniker;
        public bool HasAppUserModelId;

        private static readonly string[] KnownNames =
        {
            "Flags", "DelayTimeMs", "TimeSinceInputRemoveMs", "TimeSinceOldestInputMs",
            "ClassName", "TopLevelClassName", "ImagePath", "PackageMoniker",
            "AppUserModelId", "MessageId", "WParam"
        };

        public static EventSchema Probe(IntPtr record, int eventId)
        {
            EventSchema s = new EventSchema();
            uint ignored;
            for (int i = 0; i < KnownNames.Length; i++)
            {
                if (!Tdh.TryGetSize(record, KnownNames[i], out ignored)) continue;
                switch (KnownNames[i])
                {
                    case "Flags": s.HasFlags = true; break;
                    case "DelayTimeMs": s.HasDelayTimeMs = true; break;
                    case "TimeSinceInputRemoveMs": s.HasTimeSinceInputRemoveMs = true; break;
                    case "TimeSinceOldestInputMs": s.HasTimeSinceOldestInputMs = true; break;
                    case "ClassName": s.HasClassName = true; break;
                    case "TopLevelClassName": s.HasTopLevelClassName = true; break;
                    case "ImagePath": s.HasImagePath = true; break;
                    case "PackageMoniker": s.HasPackageMoniker = true; break;
                    case "AppUserModelId": s.HasAppUserModelId = true; break;
                    case "MessageId": s.HasMessageId = true; break;
                    case "WParam": s.HasWParam = true; break;
                }
            }
            // DelayTimeMs is purpose-built, so trust it on any event id.  The
            // InputProcessDelay tasks (37/172) carry no DelayTimeMs; their
            // delay is TimeSinceOldestInputMs.  Other events (e.g. ThreadExit)
            // expose that field too but are not UI-delay events, so they are
            // only accepted through the explicit id list.
            s.IsDelayEvent = s.HasDelayTimeMs ||
                             ((eventId == 37 || eventId == 172) && s.HasTimeSinceOldestInputMs);
            return s;
        }
    }

    // ------------------------------------------------------------ ETW native

    internal static class EtwNative
    {
        public static readonly Guid Win32kProviderId = new Guid("8c416c79-d49b-4f01-a467-e56d3aa8234c");

        // win:ResponseTime | UIUnresponsiveness -- the keywords that gate the
        // InputProcessDelay / MessageCheckDelay family and nothing else.
        public const ulong DelayKeywords = 0x0001000000000000UL | 0x0000000000040000UL;

        public const uint EventTraceRealTimeMode = 0x00000100;
        public const uint ProcessTraceModeEventRecord = 0x10000000;
        public const uint ProcessTraceModeRealTime = 0x00000100;
        public const uint ControlStop = 1;
        public const uint WnodeFlagTracedGuid = 0x00020000;
        public const ulong InvalidHandleValue = unchecked((ulong)(-1));

        public const ushort EventHeaderFlag64Bit = 0x0040;
        public const ushort EventHeaderFlag32Bit = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct WNODE_HEADER
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public ulong TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_HEADER
        {
            public ushort Size;
            public ushort HeaderType;
            public ushort Flags;
            public ushort EventProperty;
            public int ThreadId;
            public int ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public ushort Id;
            public byte Version;
            public byte Channel;
            public byte Level;
            public byte Opcode;
            public ushort Task;
            public ulong Keyword;
            public uint KernelTime;
            public uint UserTime;
            public Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ETW_BUFFER_CONTEXT
        {
            public byte ProcessorNumber;
            public byte Alignment;
            public ushort LoggerId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;
            public ETW_BUFFER_CONTEXT BufferContext;
            public ushort ExtendedDataCount;
            public ushort UserDataLength;
            public IntPtr ExtendedData;
            public IntPtr UserData;
            public IntPtr UserContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize;
            public uint Version;
            public uint ProviderVersion;
            public uint NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint BuffersWritten;
            public uint StartBuffers;
            public uint PointerSize;
            public uint EventsLost;
            public uint CpuSpeedInMHz;
            public IntPtr LoggerName;
            public IntPtr LogFileName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 172)]
            public byte[] TimeZone;   // TIME_ZONE_INFORMATION, opaque
            public long BootTime;
            public long PerfFreq;
            public long StartTime;
            public uint ReservedFlags;
            public uint BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct EVENT_TRACE_LOGFILE
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string LogFileName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize;
            public uint Filled;
            public uint EventsLost;
            public IntPtr EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        // EVENT_TRACE (used inside EVENT_TRACE_LOGFILE) -- opaque to us but its
        // size matters for the layout of the fields that follow.
        [StructLayout(LayoutKind.Sequential, Size = 88)]
        public struct EVENT_TRACE
        {
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void EventRecordCallback(IntPtr eventRecord);

        [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)]
        public static extern uint StartTrace(out ulong traceHandle, string instanceName, IntPtr properties);

        [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)]
        public static extern uint ControlTrace(ulong traceHandle, string instanceName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        public static extern uint EnableTraceEx2(
            ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE logfile);

        [DllImport("advapi32.dll")]
        public static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        public static extern uint CloseTrace(ulong traceHandle);

        public const uint EnableProviderControl = 1;
        public const byte TraceLevelInformational = 4;
    }

    // -------------------------------------------------------------- monitor

    internal sealed class UiDelayMonitor : IDisposable
    {
        private const uint ErrorSuccess = 0;
        private const uint ErrorAlreadyExists = 183;

        private readonly Options _opt;
        private readonly string _requestedOutput;
        private JsonlWriter _json;
        private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
        private readonly object _sync = new object();
        private readonly Dictionary<string, EventSchema> _schemas = new Dictionary<string, EventSchema>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _processNames = new Dictionary<int, string>();
        private readonly Dictionary<string, long> _providerCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<int, long> _idCounts = new Dictionary<int, long>();
        private readonly Dictionary<int, ulong> _idKeywords = new Dictionary<int, ulong>();
        private readonly EtwNative.EventRecordCallback _eventCallback;

        private ulong _sessionHandle;
        private ulong _traceHandle;
        private IntPtr _sessionProperties;
        private Thread _pumpThread;
        private volatile bool _closing;
        private bool _sessionStarted;
        private bool _disposed;
        private long _win32kEvents;
        private long _delayEvents;
        private long _recorded;
        private uint _pumpResult;
        private DateTime _startedAt;

        private static readonly IntPtr ContextMagic = (IntPtr)0x55494445; // "U IDE"

        public UiDelayMonitor(Options opt)
        {
            _opt = opt;
            _requestedOutput = opt.OutputPath;
            _eventCallback = new EtwNative.EventRecordCallback(OnEventRecord);
            if (opt.SessionName == null)
                opt.SessionName = "WindowsPerfMonDemo-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        }

        public int Run()
        {
            _startedAt = DateTime.UtcNow;

            string etl = null;
            if (_opt.EtlPath != null)
            {
                try
                {
                    etl = Path.GetFullPath(_opt.EtlPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: bad --etl path: " + ex.Message);
                    return 3;
                }
                if (!File.Exists(etl))
                {
                    Console.Error.WriteLine("error: ETL not found: " + etl);
                    return 3;
                }
            }

            _json = new JsonlWriter(_requestedOutput);
            Console.WriteLine("WindowsPerfMonDemo");
            Console.WriteLine("  threshold : > {0} ms", _opt.MinDelayMs);
            Console.WriteLine("  filter    : keywords=0x{0:X16} level={1}", _opt.MatchAnyKeywords, _opt.Level);
            Console.WriteLine("  output    : {0}", _json.CurrentPath);
            Console.WriteLine("  process   : {0}-bit", Environment.Is64BitProcess ? 64 : 32);

            try
            {
                if (etl != null) return RunReplay(etl);
                return RunLive();
            }
            finally
            {
                PrintSummary();
            }
        }

        // ------------------------------------------------------------- replay

        private int RunReplay(string path)
        {
            Console.WriteLine("  mode      : replay " + path);
            OpenTraceFile(path);

            uint rc = EtwNative.ProcessTrace(new ulong[] { _traceHandle }, 1, IntPtr.Zero, IntPtr.Zero);
            EtwNative.CloseTrace(_traceHandle);
            _traceHandle = 0;

            if (rc != ErrorSuccess)
            {
                Console.Error.WriteLine("error: ProcessTrace failed: 0x" + rc.ToString("X8", CultureInfo.InvariantCulture));
                return 1;
            }
            return 0;
        }

        // --------------------------------------------------------------- live

        private int RunLive()
        {
            Console.WriteLine("  mode      : live session '" + _opt.SessionName + "'");
            if (!StartSession()) return 7;
            OpenTraceRealtime(_opt.SessionName);

            _pumpThread = new Thread(Pump);
            _pumpThread.IsBackground = true;
            _pumpThread.Start();

            Console.CancelKeyPress += OnCancel;

            if (_opt.DurationSeconds > 0)
            {
                Console.WriteLine("  duration  : {0} s", _opt.DurationSeconds);
                if (!_finished.Wait(TimeSpan.FromSeconds(_opt.DurationSeconds)))
                {
                    Console.WriteLine("duration reached, stopping...");
                }
            }
            else
            {
                Console.WriteLine("  Ctrl+C to stop.");
                _finished.Wait();
            }

            StopProcessing();
            if (_pumpThread != null) _pumpThread.Join(5000);
            return _pumpResult == ErrorSuccess ? 0 : 1;
        }

        private void OnCancel(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("stopping...");
            StopProcessing();
        }

        private void Pump()
        {
            try
            {
                uint rc = EtwNative.ProcessTrace(new ulong[] { _traceHandle }, 1, IntPtr.Zero, IntPtr.Zero);
                _pumpResult = rc;
                if (rc != ErrorSuccess && !_closing)
                    Console.Error.WriteLine("error: ProcessTrace failed: 0x" + rc.ToString("X8", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                if (!_closing) Console.Error.WriteLine("error: event pump: " + ex.Message);
            }
            finally
            {
                _finished.Set();
            }
        }

        private void StopProcessing()
        {
            lock (_sync)
            {
                if (_closing) return;
                _closing = true;
                if (_traceHandle != 0)
                {
                    EtwNative.CloseTrace(_traceHandle);
                    _traceHandle = 0;
                }
            }
            if (_sessionStarted) StopSession();
        }

        // -------------------------------------------------------- ETW session

        private bool StartSession()
        {
            uint rc = StartTraceWithProperties(_opt.SessionName, out _sessionHandle, out _sessionProperties);
            if (rc == ErrorAlreadyExists)
            {
                Console.WriteLine("  stale session exists, stopping it...");
                IntPtr stale = BuildSessionProperties(_opt.SessionName, 0);
                try
                {
                    EtwNative.ControlTrace(0, _opt.SessionName, stale, EtwNative.ControlStop);
                }
                finally
                {
                    Marshal.FreeHGlobal(stale);
                }
                Thread.Sleep(500);
                rc = StartTraceWithProperties(_opt.SessionName, out _sessionHandle, out _sessionProperties);
            }

            if (rc != ErrorSuccess)
            {
                Console.Error.WriteLine("error: StartTrace failed: 0x" + rc.ToString("X8", CultureInfo.InvariantCulture));
                if (rc == 5) Console.Error.WriteLine("       access denied - run elevated.");
                return false;
            }
            _sessionStarted = true;

            Guid provider = EtwNative.Win32kProviderId;
            rc = EtwNative.EnableTraceEx2(
                _sessionHandle, ref provider, EtwNative.EnableProviderControl,
                _opt.Level, _opt.MatchAnyKeywords, 0, 0, IntPtr.Zero);
            if (rc != ErrorSuccess)
            {
                Console.Error.WriteLine("error: EnableTraceEx2(Win32k) failed: 0x" + rc.ToString("X8", CultureInfo.InvariantCulture));
                StopSession();
                return false;
            }
            return true;
        }

        private static uint StartTraceWithProperties(string name, out ulong handle, out IntPtr properties)
        {
            properties = BuildSessionProperties(name, EtwNative.EventTraceRealTimeMode);
            uint rc = EtwNative.StartTrace(out handle, name, properties);
            if (rc != ErrorSuccess)
            {
                Marshal.FreeHGlobal(properties);
                properties = IntPtr.Zero;
            }
            return rc;
        }

        private static IntPtr BuildSessionProperties(string name, uint logFileMode)
        {
            int structSize = Marshal.SizeOf(typeof(EtwNative.EVENT_TRACE_PROPERTIES));
            int nameBytes = (name.Length + 1) * 2;
            int total = structSize + nameBytes + 16;

            IntPtr mem = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(mem, i, 0);

            EtwNative.EVENT_TRACE_PROPERTIES props = new EtwNative.EVENT_TRACE_PROPERTIES();
            props.Wnode.BufferSize = (uint)(structSize + nameBytes);
            props.Wnode.Flags = EtwNative.WnodeFlagTracedGuid;
            props.Wnode.ClientContext = 1;               // QPC
            props.BufferSize = 64;                       // KB per buffer
            props.MinimumBuffers = 16;
            props.MaximumBuffers = 64;
            props.FlushTimer = 1;                        // seconds
            props.LogFileMode = logFileMode;
            props.LoggerNameOffset = (uint)structSize;
            Marshal.StructureToPtr(props, mem, false);

            IntPtr namePtr = IntPtr.Add(mem, structSize);
            for (int i = 0; i < name.Length; i++)
                Marshal.WriteInt16(namePtr, i * 2, name[i]);
            Marshal.WriteInt16(namePtr, name.Length * 2, 0);
            return mem;
        }

        private void StopSession()
        {
            if (!_sessionStarted) return;
            _sessionStarted = false;
            if (_sessionProperties != IntPtr.Zero)
            {
                EtwNative.ControlTrace(_sessionHandle, _opt.SessionName, _sessionProperties, EtwNative.ControlStop);
                Marshal.FreeHGlobal(_sessionProperties);
                _sessionProperties = IntPtr.Zero;
            }
            _sessionHandle = 0;
        }

        // ---------------------------------------------------------- open trace

        private void OpenTraceRealtime(string sessionName)
        {
            EtwNative.EVENT_TRACE_LOGFILE logfile = NewLogfile();
            logfile.LoggerName = sessionName;
            logfile.ProcessTraceMode = EtwNative.ProcessTraceModeRealTime | EtwNative.ProcessTraceModeEventRecord;
            OpenTraceCommon(ref logfile);
        }

        private void OpenTraceFile(string path)
        {
            EtwNative.EVENT_TRACE_LOGFILE logfile = NewLogfile();
            logfile.LogFileName = path;
            logfile.ProcessTraceMode = EtwNative.ProcessTraceModeEventRecord;
            OpenTraceCommon(ref logfile);
        }

        private EtwNative.EVENT_TRACE_LOGFILE NewLogfile()
        {
            EtwNative.EVENT_TRACE_LOGFILE logfile = new EtwNative.EVENT_TRACE_LOGFILE();
            logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_eventCallback);
            logfile.Context = ContextMagic;
            return logfile;
        }

        private void OpenTraceCommon(ref EtwNative.EVENT_TRACE_LOGFILE logfile)
        {
            ulong handle = EtwNative.OpenTrace(ref logfile);
            if (handle == EtwNative.InvalidHandleValue)
            {
                int last = Marshal.GetLastWin32Error();
                throw new Win32Exception(last, "OpenTrace failed");
            }
            // Sanity check that our EVENT_TRACE_LOGFILE layout matches the one
            // the OS filled in; a mismatch would corrupt the callback pointer.
            if (logfile.LogfileHeader.PointerSize != 4 && logfile.LogfileHeader.PointerSize != 8)
            {
                EtwNative.CloseTrace(handle);
                throw new InvalidOperationException(
                    "EVENT_TRACE_LOGFILE layout mismatch (PointerSize=" +
                    logfile.LogfileHeader.PointerSize.ToString(CultureInfo.InvariantCulture) + ")");
            }
            _traceHandle = handle;
        }

        // ------------------------------------------------------------- events

        private void OnEventRecord(IntPtr recordPtr)
        {
            try
            {
                HandleEvent(recordPtr);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("warn: event handling failed: " + ex.Message);
            }
        }

        private void HandleEvent(IntPtr recordPtr)
        {
            if (recordPtr == IntPtr.Zero) return;

            EtwNative.EVENT_HEADER header =
                (EtwNative.EVENT_HEADER)Marshal.PtrToStructure(recordPtr, typeof(EtwNative.EVENT_HEADER));

            if (_opt.CountAll)
            {
                string provider = header.ProviderId.ToString();
                long count;
                _providerCounts.TryGetValue(provider, out count);
                _providerCounts[provider] = count + 1;
                if (header.ProviderId.Equals(EtwNative.Win32kProviderId))
                {
                    long idCount;
                    _idCounts.TryGetValue(header.Id, out idCount);
                    _idCounts[header.Id] = idCount + 1;
                    ulong mask;
                    _idKeywords.TryGetValue(header.Id, out mask);
                    _idKeywords[header.Id] = mask | header.Keyword;
                }
                return;
            }

            if (!header.ProviderId.Equals(EtwNative.Win32kProviderId)) return;

            Interlocked.Increment(ref _win32kEvents);

            string key = header.Id.ToString(CultureInfo.InvariantCulture) + "." + header.Version.ToString(CultureInfo.InvariantCulture);
            EventSchema schema;
            if (!_schemas.TryGetValue(key, out schema))
            {
                schema = EventSchema.Probe(recordPtr, header.Id);
                _schemas[key] = schema;
                if (_opt.ProbeSchema) DumpSchema(recordPtr, header, schema);
            }
            if (!schema.IsDelayEvent) return;

            Interlocked.Increment(ref _delayEvents);

            uint delayMs;
            string delaySource;
            if (schema.HasDelayTimeMs && Tdh.TryGetUInt32(recordPtr, "DelayTimeMs", out delayMs))
                delaySource = "DelayTimeMs";
            else if (schema.HasTimeSinceOldestInputMs && Tdh.TryGetUInt32(recordPtr, "TimeSinceOldestInputMs", out delayMs))
                delaySource = "TimeSinceOldestInputMs";
            else
                return;

            if (delayMs <= _opt.MinDelayMs)
            {
                if (_opt.Probe) ProbeLine(recordPtr, header, schema, delayMs, delaySource);
                return;
            }

            DelayRecord rec = BuildRecord(recordPtr, header, schema, delayMs, delaySource);
            _json.Write(rec);
            Interlocked.Increment(ref _recorded);
            if (_opt.Probe || !_opt.Quiet) Console.WriteLine(rec.OneLine());
        }

        private DelayRecord BuildRecord(IntPtr recordPtr, EtwNative.EVENT_HEADER header, EventSchema schema,
                                        uint delayMs, string delaySource)
        {
            DelayRecord rec = new DelayRecord();
            rec.UtcTime = FromFileTimeUtc(header.TimeStamp);
            rec.EventId = header.Id;
            rec.Version = header.Version;
            rec.EventName = EventName(header.Id);
            rec.DelaySource = delaySource;
            rec.DelayMs = delayMs;
            rec.ProcessId = header.ProcessId;
            rec.ThreadId = header.ThreadId;
            rec.ProcessName = ResolveProcessName(header.ProcessId);

            uint value;
            if (schema.HasDelayTimeMs && Tdh.TryGetUInt32(recordPtr, "DelayTimeMs", out value)) rec.DelayTimeMs = value;
            if (schema.HasTimeSinceInputRemoveMs && Tdh.TryGetUInt32(recordPtr, "TimeSinceInputRemoveMs", out value)) rec.TimeSinceInputRemoveMs = value;
            if (schema.HasTimeSinceOldestInputMs && Tdh.TryGetUInt32(recordPtr, "TimeSinceOldestInputMs", out value)) rec.TimeSinceOldestInputMs = value;
            if (schema.HasFlags && Tdh.TryGetUInt32(recordPtr, "Flags", out value)) rec.Flags = value;
            if (schema.HasMessageId && Tdh.TryGetUInt32(recordPtr, "MessageId", out value)) rec.MessageId = value;

            ulong wide;
            if (schema.HasWParam && Tdh.TryGetUInt64(recordPtr, "WParam", out wide)) rec.WParam = wide;

            string text;
            if (schema.HasImagePath && Tdh.TryGetString(recordPtr, "ImagePath", out text)) rec.ImagePath = text;
            if (schema.HasPackageMoniker && Tdh.TryGetString(recordPtr, "PackageMoniker", out text)) rec.PackageMoniker = text;
            if (schema.HasAppUserModelId && Tdh.TryGetString(recordPtr, "AppUserModelId", out text)) rec.AppUserModelId = text;
            if (schema.HasClassName && Tdh.TryGetString(recordPtr, "ClassName", out text)) rec.ClassName = text;
            if (schema.HasTopLevelClassName && Tdh.TryGetString(recordPtr, "TopLevelClassName", out text)) rec.TopLevelClassName = text;
            return rec;
        }

        private void ProbeLine(IntPtr recordPtr, EtwNative.EVENT_HEADER header, EventSchema schema,
                               uint delayMs, string delaySource)
        {
            string className;
            if (!schema.HasClassName || !Tdh.TryGetString(recordPtr, "ClassName", out className)) className = "";
            string imagePath;
            if (!schema.HasImagePath || !Tdh.TryGetString(recordPtr, "ImagePath", out imagePath)) imagePath = "";
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "probe id={0} v{1} {2} delay={3}ms ({4}) pid={5} class='{6}' image='{7}'",
                header.Id, header.Version, EventName(header.Id), delayMs, delaySource,
                header.ProcessId, className, imagePath));
        }

        private void DumpSchema(IntPtr recordPtr, EtwNative.EVENT_HEADER header, EventSchema schema)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "schema id={0} v{1} pid={2} delay={3}", header.Id, header.Version, header.ProcessId, schema.IsDelayEvent);
            uint u;
            ulong w;
            string s;
            if (schema.HasFlags && Tdh.TryGetUInt32(recordPtr, "Flags", out u))
                sb.AppendFormat(CultureInfo.InvariantCulture, " Flags=0x{0:X}", u);
            if (schema.HasDelayTimeMs && Tdh.TryGetUInt32(recordPtr, "DelayTimeMs", out u))
                sb.Append(" DelayTimeMs=").Append(u.ToString(CultureInfo.InvariantCulture));
            if (schema.HasTimeSinceInputRemoveMs && Tdh.TryGetUInt32(recordPtr, "TimeSinceInputRemoveMs", out u))
                sb.Append(" TimeSinceInputRemoveMs=").Append(u.ToString(CultureInfo.InvariantCulture));
            if (schema.HasTimeSinceOldestInputMs && Tdh.TryGetUInt32(recordPtr, "TimeSinceOldestInputMs", out u))
                sb.Append(" TimeSinceOldestInputMs=").Append(u.ToString(CultureInfo.InvariantCulture));
            if (schema.HasMessageId && Tdh.TryGetUInt32(recordPtr, "MessageId", out u))
                sb.Append(" MessageId=").Append(u.ToString(CultureInfo.InvariantCulture));
            if (schema.HasWParam && Tdh.TryGetUInt64(recordPtr, "WParam", out w))
                sb.AppendFormat(CultureInfo.InvariantCulture, " WParam=0x{0:X}", w);
            if (schema.HasClassName && Tdh.TryGetString(recordPtr, "ClassName", out s))
                sb.Append(" ClassName='").Append(s).Append('\'');
            if (schema.HasTopLevelClassName && Tdh.TryGetString(recordPtr, "TopLevelClassName", out s))
                sb.Append(" TopLevelClassName='").Append(s).Append('\'');
            if (schema.HasImagePath && Tdh.TryGetString(recordPtr, "ImagePath", out s))
                sb.Append(" ImagePath='").Append(s).Append('\'');
            if (schema.HasPackageMoniker && Tdh.TryGetString(recordPtr, "PackageMoniker", out s))
                sb.Append(" PackageMoniker='").Append(s).Append('\'');
            if (schema.HasAppUserModelId && Tdh.TryGetString(recordPtr, "AppUserModelId", out s))
                sb.Append(" AppUserModelId='").Append(s).Append('\'');
            Console.WriteLine(sb.ToString());
        }

        private static DateTime FromFileTimeUtc(long fileTime)
        {
            if (fileTime <= 0) return DateTime.UtcNow;
            try
            {
                return DateTime.FromFileTimeUtc(fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.UtcNow;
            }
        }

        private string ResolveProcessName(int pid)
        {
            if (pid <= 0) return null;
            lock (_processNames)
            {
                string name;
                if (_processNames.TryGetValue(pid, out name)) return name;
            }
            string result = null;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    result = p.ProcessName + ".exe";
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            lock (_processNames) { _processNames[pid] = result; }
            return result;
        }

        private static string EventName(int eventId)
        {
            switch (eventId)
            {
                case 37: return "InputProcessDelay";
                case 38: return "MessageCheckDelay";
                case 172: return "ImmersiveInputProcessDelay";
                case 173: return "ImmersiveMessageCheckDelay";
                default: return "Win32kEventId" + eventId.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------ summary

        private void PrintSummary()
        {
            TimeSpan elapsed = DateTime.UtcNow - _startedAt;
            Console.WriteLine();
            Console.WriteLine("summary");
            Console.WriteLine("  elapsed        : {0:hh\\:mm\\:ss}", elapsed);
            Console.WriteLine("  win32k events  : {0}", Interlocked.Read(ref _win32kEvents));
            Console.WriteLine("  delay events   : {0}", Interlocked.Read(ref _delayEvents));
            Console.WriteLine("  recorded (> {0} ms): {1}", _opt.MinDelayMs, Interlocked.Read(ref _recorded));
            Console.WriteLine("  output         : {0}", _json != null ? _json.CurrentPath : "(not opened)");
            if (_opt.CountAll && _providerCounts.Count > 0)
            {
                List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>(_providerCounts);
                list.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                Console.WriteLine("  providers (top 12):");
                for (int i = 0; i < list.Count && i < 12; i++)
                    Console.WriteLine("    {0} : {1}", list[i].Key, list[i].Value);

                if (_idCounts.Count > 0)
                {
                    List<KeyValuePair<int, long>> ids = new List<KeyValuePair<int, long>>(_idCounts);
                    ids.Sort(delegate (KeyValuePair<int, long> a, KeyValuePair<int, long> b)
                    {
                        return b.Value.CompareTo(a.Value);
                    });
                    Console.WriteLine("  win32k event ids (top 20, count / keywords):");
                    for (int i = 0; i < ids.Count && i < 20; i++)
                    {
                        ulong mask;
                        _idKeywords.TryGetValue(ids[i].Key, out mask);
                        Console.WriteLine("    id={0,-5} count={1,-8} keywords=0x{2:X16}",
                            ids[i].Key, ids[i].Value, mask);
                    }
                    foreach (int delayId in new int[] { 37, 38, 172, 173 })
                    {
                        long c;
                        if (_idCounts.TryGetValue(delayId, out c))
                        {
                            ulong mask;
                            _idKeywords.TryGetValue(delayId, out mask);
                            Console.WriteLine("  DELAY id={0} count={1} keywords=0x{2:X16}", delayId, c, mask);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopProcessing();
            if (_pumpThread != null && _pumpThread.IsAlive) _pumpThread.Join(3000);
            StopSession();
            if (_json != null) _json.Dispose();
            _finished.Dispose();
        }
    }
}
