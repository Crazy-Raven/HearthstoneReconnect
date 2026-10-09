using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace HearthstoneReconnect
{
    internal static class Program
    {
        internal static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearthstoneReconnect");
        [STAThread]
        private static void Main(string[] args)
        {
            // Read-only command modes return BEFORE constructing the interactive form.
            if (args.Length > 0 && args[0] == "--self-test")
            {
                WriteReport("self-test.txt", SelfTests.Run());
                return;
            }
            if (args.Length > 0 && args[0] == "--diagnose")
            {
                WriteReport("diagnose.txt", Detector.Capture().Report());
                return;
            }
            if (args.Length > 0 && args[0] == "--enable-logging")
            {
                try { WriteReport("logging-setup.txt", LoggingConfiguration.Enable()); }
                catch (Exception exception)
                {
                    Environment.ExitCode = 1;
                    WriteReport("logging-setup.txt", "开启日志未完成：" + exception.Message);
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--ui-preview")
            {
                using (ReconnectForm form = new ReconnectForm(true))
                {
                    form.CreateControl();
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawPreview(bitmap);
                        bitmap.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return;
            }
            Application.Run(new ReconnectForm(false));
        }
        private static void WriteReport(string filename, string text)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(Path.Combine(DataDirectory, filename), text, new UTF8Encoding(true));
            }
            catch { Environment.ExitCode = 2; }
        }
        internal static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal sealed class Endpoint
    {
        internal uint Address;
        internal int Port;
        internal DateTime Time;
        internal string Display { get { return new IPAddress(BitConverter.GetBytes(Address)) + ":" + Port; } }
    }
    internal sealed class LogEvent
    {
        internal DateTime Time;
        internal Endpoint Endpoint;
        internal bool End;
    }
    internal sealed class Snapshot
    {
        internal string Status = "等待炉石启动";
        internal string Detail = "只在手动操作时重置对局连接";
        internal int Pid;
        internal DateTime ProcessStart;
        internal string GamePath;
        internal string Session;
        internal Endpoint Endpoint;
        internal Native.TcpOwnerRow Row;
        internal int Matches;
        internal bool Ready;
        internal string Report()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("炉石对局重连工具：只读诊断（不会断线）");
            report.AppendLine("时间：" + DateTime.Now.ToString("O"));
            report.AppendLine("状态：" + Status);
            report.AppendLine("说明：" + Detail);
            report.AppendLine("管理员：" + Program.IsAdministrator());
            report.AppendLine("进程 PID：" + Pid);
            report.AppendLine("进程启动：" + (Pid == 0 ? "无" : ProcessStart.ToString("O")));
            report.AppendLine("游戏路径：" + (GamePath ?? "未识别"));
            report.AppendLine("本次日志目录：" + (Session ?? "未识别"));
            report.AppendLine("有效对局端点：" + (Endpoint == null ? "无" : Endpoint.Display));
            report.AppendLine("唯一已建立连接：" + (Ready ? "是" : "否") + "；匹配数：" + Matches);
            report.AppendLine("只检查当前 PID、本次日志会话和 IPv4 对局 TCP；不读取或输出账号、game/client/spectateKey。");
            return report.ToString();
        }
    }

    internal static class LogParser
    {
        private static readonly Regex EndpointPattern = new Regex(@"Network\.GotoGameServe\(\).*?address=\s*(\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})(?:\D|$)", RegexOptions.Compiled);
        private static readonly Regex TimePattern = new Regex(@"^\s*[A-Z]\s+(\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)", RegexOptions.Compiled);
        internal static List<LogEvent> Parse(string text, DateTime sessionTime)
        {
            return Parse(text, sessionTime, sessionTime);
        }
        internal static List<LogEvent> Parse(string text, DateTime sessionTime, DateTime tailWriteTime)
        {
            List<LogEvent> result = new List<LogEvent>();
            DateTime day = tailWriteTime.Date;
            TimeSpan previous = tailWriteTime.TimeOfDay;
            string[] lines = text.Split('\n');
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                string line = lines[index];
                Match clock = TimePattern.Match(line);
                DateTime parsed;
                if (!clock.Success || !DateTime.TryParseExact(clock.Groups[1].Value,
                    new string[] { "HH:mm:ss", "HH:mm:ss.FFFFFFF" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) continue;
                TimeSpan time = parsed.TimeOfDay;
                if (time - previous > TimeSpan.FromHours(12)) day = day.AddDays(-1);
                previous = time;
                DateTime stamp = day.Add(time);
                // Sparse logs can skip more than 12 hours. An older line cannot be
                // in the future relative to the file's last write; move it back a day.
                if (stamp > tailWriteTime.AddSeconds(5))
                {
                    day = day.AddDays(-1);
                    stamp = day.Add(time);
                }
                Match address = EndpointPattern.Match(line);
                if (address.Success)
                {
                    IPAddress ip;
                    int port;
                    string rawIp = address.Groups[1].Value;
                    if (IPAddress.TryParse(rawIp, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        Int32.TryParse(address.Groups[2].Value, out port) && port > 0 && port <= 65535 && port != 1119 &&
                        ip.ToString() == rawIp && !IPAddress.IsLoopback(ip) && rawIp != "0.0.0.0" && rawIp != "255.255.255.255")
                    {
                        Endpoint endpoint = new Endpoint { Address = BitConverter.ToUInt32(ip.GetAddressBytes(), 0), Port = port, Time = stamp };
                        result.Add(new LogEvent { Time = stamp, Endpoint = endpoint });
                    }
                    else result.Add(new LogEvent { Time = stamp, End = true });
                }
                else if ((line.Contains("Network.GotoGameServe()") && line.Contains("address=")) ||
                    line.Contains("Network.DisconnectFromGameServer()") || line.Contains("Network.OnGameServerDisconnectEvent()") ||
                    line.Contains("EndGameScreen") ||
                    (line.Contains("Entity=GameEntity") && line.Contains("value=COMPLETE") &&
                    (line.Contains("tag=STATE") || line.Contains("tag=GAME_STATE"))))
                    result.Add(new LogEvent { Time = stamp, End = true });
            }
            return result;
        }
        internal static Endpoint LatestActive(List<LogEvent> events)
        {
            LogEvent latest = null;
            foreach (LogEvent current in events)
            {
                // A disconnect wins if two sources share a timestamp.
                if (latest == null || current.Time > latest.Time) latest = current;
                else if (current.Time == latest.Time)
                {
                    if (current.End) latest = current;
                    else if (!latest.End && (current.Endpoint.Address != latest.Endpoint.Address || current.Endpoint.Port != latest.Endpoint.Port))
                        latest = new LogEvent { Time = current.Time, End = true };
                }
            }
            return latest == null || latest.End ? null : latest.Endpoint;
        }
        internal static string ReadTail(string path, int maxBytes)
        {
            if (!File.Exists(path)) return "";
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                int count = (int)Math.Min(length, maxBytes);
                long start = length - count;
                stream.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[count];
                int read = 0;
                while (read < count)
                {
                    int got = stream.Read(buffer, read, count - read);
                    if (got == 0) break;
                    read += got;
                }
                string content = Encoding.UTF8.GetString(buffer, 0, read);
                // Drop the incomplete first line after seeking into a large file.
                if (start > 0) { int newline = content.IndexOf('\n'); content = newline < 0 ? "" : content.Substring(newline + 1); }
                return content.TrimStart('\uFEFF');
            }
        }
        internal static List<LogEvent> ReadEvents(string path, int maxBytes, DateTime sessionTime)
        {
            if (!File.Exists(path)) return new List<LogEvent>();
            string content = ReadTail(path, maxBytes);
            DateTime tailWrite = File.GetLastWriteTime(path);
            if (tailWrite < sessionTime.AddSeconds(-3)) return new List<LogEvent>();
            return Parse(content, sessionTime, tailWrite);
        }
    }

    internal static class Detector
    {
        internal static bool SessionMatches(DateTime started, DateTime session)
        {
            return session >= started.AddSeconds(-3) && session <= started.AddMinutes(2);
        }
        internal static string LatestSession(string logsRoot, DateTime started, out DateTime sessionTime)
        {
            sessionTime = DateTime.MinValue;
            if (!Directory.Exists(logsRoot)) return null;
            string latest = null;
            foreach (string directory in Directory.GetDirectories(logsRoot, "Hearthstone_*"))
            {
                DateTime stamp;
                string name = Path.GetFileName(directory);
                if (!DateTime.TryParseExact(name, "'Hearthstone_'yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out stamp)) continue;
                if (stamp > sessionTime) { latest = directory; sessionTime = stamp; }
            }
            // NEVER fall back to an older directory if the latest is not this process's session.
            if (latest == null || !SessionMatches(started, sessionTime)) return null;
            DateTime created = Directory.GetCreationTime(latest);
            if (!SessionMatches(started, created)) return null;
            return latest;
        }
        internal static Snapshot Capture()
        {
            Snapshot snapshot = new Snapshot();
            Process[] games = null;
            try
            {
                games = Process.GetProcessesByName("Hearthstone");
                if (games.Length == 0) return snapshot;
                if (games.Length != 1) { snapshot.Status = "检测到多个炉石进程"; snapshot.Detail = "无法确认目标，请只保留一个游戏进程"; return snapshot; }
                Process game = games[0];
                snapshot.Pid = game.Id;
                snapshot.ProcessStart = game.StartTime;
                string executable = null;
                try { executable = game.MainModule.FileName; } catch { }
                if (String.IsNullOrEmpty(executable))
                {
                    snapshot.Status = "无法读取炉石位置";
                    snapshot.Detail = Program.IsAdministrator() ? "无法读取当前炉石进程的路径，请稍后重试" : "请点击管理员运行，再识别当前炉石进程的位置";
                    return snapshot;
                }
                snapshot.GamePath = Path.GetDirectoryName(executable);
                DateTime sessionTime;
                snapshot.Session = LatestSession(Path.Combine(snapshot.GamePath, "Logs"), snapshot.ProcessStart, out sessionTime);
                if (snapshot.Session == null)
                {
                    snapshot.Status = "等待本次启动的日志";
                    snapshot.Detail = "尚未找到与当前炉石进程匹配的日志会话";
                    return snapshot;
                }
                List<LogEvent> events = new List<LogEvent>();
                string connectionLog = Path.Combine(snapshot.Session, "GameNetLogger.log");
                if (!File.Exists(connectionLog) || new FileInfo(connectionLog).Length == 0)
                    connectionLog = Path.Combine(snapshot.Session, "Hearthstone.log");
                events.AddRange(LogParser.ReadEvents(connectionLog, 4 * 1024 * 1024, sessionTime));
                events.AddRange(LogParser.ReadEvents(Path.Combine(snapshot.Session, "Power.log"), 512 * 1024, sessionTime));
                snapshot.Endpoint = LogParser.LatestActive(events);
                if (snapshot.Endpoint == null)
                {
                    snapshot.Status = "等待对局连接";
                    snapshot.Detail = "大厅、已结束对局或日志中尚无有效对局地址";
                    return snapshot;
                }
                List<Native.TcpOwnerRow> rows = Native.ReadTcpTable();
                Native.TcpOwnerRow selected;
                snapshot.Matches = SelectTarget(rows, snapshot.Pid, snapshot.Endpoint, out selected);
                if (snapshot.Matches != 1)
                {
                    snapshot.Status = snapshot.Matches == 0 ? "等待对局连接" : "对局连接不唯一，已停用";
                    snapshot.Detail = snapshot.Matches == 0 ? "可能正在连接或重连；只检查本次对局地址" : "无法确认唯一目标，请稍后再试";
                    return snapshot;
                }
                if (game.HasExited || game.StartTime != snapshot.ProcessStart)
                {
                    snapshot.Status = "炉石进程已变化";
                    snapshot.Detail = "等待重新识别当前进程";
                    return snapshot;
                }
                snapshot.Ready = true;
                snapshot.Row = selected;
                snapshot.Status = "对局连接已就绪";
                snapshot.Detail = "当前对局已识别，请在战斗开始后点击";
                return snapshot;
            }
            catch (Exception exception)
            {
                snapshot.Ready = false;
                snapshot.Status = "暂时无法确认连接";
                snapshot.Detail = "只读检查失败（" + exception.GetType().Name + "），稍后自动重试";
                return snapshot;
            }
            finally { if (games != null) foreach (Process process in games) process.Dispose(); }
        }
        internal static int SelectTarget(List<Native.TcpOwnerRow> rows, int pid, Endpoint endpoint, out Native.TcpOwnerRow selected)
        {
            selected = new Native.TcpOwnerRow();
            int count = 0;
            if (pid <= 0 || endpoint == null || endpoint.Port == 1119) return count;
            foreach (Native.TcpOwnerRow row in rows)
                if (row.OwningPid == (uint)pid && row.State == 5 && row.RemoteAddr == endpoint.Address && Native.DecodePort(row.RemotePort) == endpoint.Port)
                { count++; selected = row; }
            return count;
        }
    }

    internal static class LoggingConfiguration
    {
        private static readonly string[] RequiredSections = { "Net", "Power", "GameNetLogger" };
        private static readonly string[] RequiredKeys = { "LogLevel", "FilePrinting", "ConsolePrinting" };
        private static readonly string[] RequiredValues = { "1", "true", "false" };
        private sealed class IniBlock
        {
            internal string Name;
            internal string Header;
            internal List<string> Lines = new List<string>();
        }
        // Only normalize the three logging sections and their required keys.
        // Keep unknown settings and comments, including content from repeated sections.
        internal static string Merge(string original)
        {
            List<IniBlock> blocks = new List<IniBlock>();
            Dictionary<string, IniBlock> logging = new Dictionary<string, IniBlock>(StringComparer.OrdinalIgnoreCase);
            IniBlock current = new IniBlock();
            blocks.Add(current);
            string[] lines = Regex.Split(original ?? "", "\r\n|\n|\r");
            int count = lines.Length;
            if (count > 0 && lines[count - 1].Length == 0) count--;
            for (int index = 0; index < count; index++)
            {
                Match section = Regex.Match(lines[index], @"^\s*\[([^\]]+)\]\s*(?:[;#].*)?$");
                if (!section.Success) { current.Lines.Add(lines[index]); continue; }
                string name = section.Groups[1].Value.Trim();
                bool required = Array.Exists(RequiredSections, delegate(string value) { return String.Equals(value, name, StringComparison.OrdinalIgnoreCase); });
                if (required && logging.TryGetValue(name, out current)) continue;
                current = new IniBlock { Name = name, Header = lines[index] };
                blocks.Add(current);
                if (required) logging.Add(name, current);
            }
            foreach (string name in RequiredSections)
            {
                if (logging.ContainsKey(name)) continue;
                if (blocks[blocks.Count - 1].Lines.Count > 0 && blocks[blocks.Count - 1].Lines[blocks[blocks.Count - 1].Lines.Count - 1].Length > 0)
                    blocks[blocks.Count - 1].Lines.Add("");
                IniBlock missing = new IniBlock { Name = name, Header = "[" + name + "]" };
                blocks.Add(missing);
                logging.Add(name, missing);
            }
            List<string> output = new List<string>();
            foreach (IniBlock block in blocks)
            {
                if (block.Header != null) output.Add(block.Header);
                bool required = block.Name != null && logging.ContainsKey(block.Name);
                bool[] written = new bool[RequiredKeys.Length];
                foreach (string line in block.Lines)
                {
                    int keyIndex = -1;
                    string trimmed = line.Trim();
                    int equals = trimmed.IndexOf('=');
                    if (required && equals > 0 && !trimmed.StartsWith(";") && !trimmed.StartsWith("#"))
                    {
                        string key = trimmed.Substring(0, equals).Trim();
                        keyIndex = Array.FindIndex(RequiredKeys, delegate(string value) { return String.Equals(value, key, StringComparison.OrdinalIgnoreCase); });
                    }
                    if (keyIndex < 0) output.Add(line);
                    else if (!written[keyIndex])
                    {
                        output.Add(RequiredKeys[keyIndex] + "=" + RequiredValues[keyIndex]);
                        written[keyIndex] = true;
                    }
                }
                if (required)
                    for (int index = 0; index < RequiredKeys.Length; index++)
                        if (!written[index]) output.Add(RequiredKeys[index] + "=" + RequiredValues[index]);
            }
            return String.Join("\r\n", output.ToArray()) + "\r\n";
        }
        private static void EnsureGameClosed()
        {
            Process[] games = Process.GetProcessesByName("Hearthstone");
            try
            {
                if (games.Length > 0) throw new InvalidOperationException("请先完全退出炉石，再点击“开启炉石日志”。");
            }
            finally { foreach (Process game in games) game.Dispose(); }
        }
        private static string ReadConfig(string path, out Encoding encoding)
        {
            try
            {
                using (StreamReader reader = new StreamReader(path, new UTF8Encoding(false, true), true))
                {
                    string text = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                    return text;
                }
            }
            catch (DecoderFallbackException)
            {
                using (StreamReader reader = new StreamReader(path, Encoding.Default, true))
                {
                    string text = reader.ReadToEnd();
                    encoding = reader.CurrentEncoding;
                    return text;
                }
            }
        }
        internal static string Enable()
        {
            // No startup, timer, diagnostic or self-test path calls this method.
            EnsureGameClosed();
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blizzard", "Hearthstone");
            string path = Path.Combine(folder, "log.config");
            bool existed = File.Exists(path);
            Encoding encoding = new UTF8Encoding(false);
            string original = existed ? ReadConfig(path, out encoding) : "";
            string merged = Merge(original);
            if (String.Equals(original, merged, StringComparison.Ordinal))
                return "日志已经开启。请重新启动炉石，再进入战棋对局。\n配置：" + path;
            EnsureGameClosed();
            Directory.CreateDirectory(folder);
            string suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string backup = existed ? path + ".backup-" + suffix : null;
            string temporary = Path.Combine(folder, "log.config.tmp-" + suffix);
            try
            {
                File.WriteAllText(temporary, merged, encoding);
                if (existed) File.Replace(temporary, path, backup);
                else File.Move(temporary, path);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            return "日志已开启。请重新启动炉石，再进入战棋对局。\n配置：" + path + (backup == null ? "" : "\n原配置备份：" + backup);
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct TcpOwnerRow
        {
            internal uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRow
        {
            internal uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
        }
        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")]
        private static extern uint SetTcpEntry(ref TcpRow row);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        internal static int DecodePort(uint raw) { return (int)(((raw & 255) << 8) | ((raw >> 8) & 255)); }
        internal static uint EncodePort(int port) { return (uint)(((port & 255) << 8) | ((port >> 8) & 255)); }
        internal static List<TcpOwnerRow> ReadTcpTable()
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
            if (result != 122 && result != 0) throw new InvalidOperationException("TCP table unavailable");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidOperationException("TCP table size invalid");
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int allocated = size;
                    result = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                    if (result == 122) continue;
                    if (result != 0) throw new InvalidOperationException("TCP table unavailable");
                    int count = Marshal.ReadInt32(buffer);
                    int rowSize = Marshal.SizeOf(typeof(TcpOwnerRow));
                    if (count < 0 || 4L + (long)count * rowSize > allocated || rowSize != 24)
                        throw new InvalidOperationException("TCP table layout invalid");
                    List<TcpOwnerRow> rows = new List<TcpOwnerRow>(count);
                    for (int index = 0; index < count; index++)
                        rows.Add((TcpOwnerRow)Marshal.PtrToStructure(IntPtr.Add(buffer, 4 + index * rowSize), typeof(TcpOwnerRow)));
                    return rows;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidOperationException("TCP table changing");
        }
        // The only write-capable API call. Only ReconnectForm.ResetFromUserIntent calls this method.
        internal static uint ResetOneEstablishedConnection(Native.TcpOwnerRow source)
        {
            if (!Program.IsAdministrator() || source.State != 5 || DecodePort(source.RemotePort) == 1119)
                throw new InvalidOperationException("Reset gate rejected");
            TcpRow row = new TcpRow { State = 12, LocalAddr = source.LocalAddr, LocalPort = source.LocalPort,
                RemoteAddr = source.RemoteAddr, RemotePort = source.RemotePort };
            return SetTcpEntry(ref row);
        }
    }

    internal sealed class NonActivatingButton : Button
    {
        internal NonActivatingButton() { SetStyle(ControlStyles.Selectable, false); TabStop = false; }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            base.WndProc(ref message);
        }
    }

    internal sealed class ReconnectForm : Form
    {
        private readonly Button reset = new NonActivatingButton();
        private readonly ToolTip hint = new ToolTip { ShowAlways = true, InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 10000 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem menuStatus = new ToolStripMenuItem { Enabled = false };
        private readonly ToolStripMenuItem menuShortcut = new ToolStripMenuItem("快捷键：Ctrl + Alt + F8") { Enabled = false };
        private readonly Timer timer = new Timer();
        private readonly bool preview;
        private readonly bool administrator;
        private DateTime cooldownUntil = DateTime.MinValue;
        private DateTime feedbackUntil = DateTime.MinValue;
        private string feedback;
        private bool busy;
        private bool registered;
        private bool ready;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
        internal ReconnectForm(bool isPreview)
        {
            preview = isPreview;
            administrator = Program.IsAdministrator();
            Text = "炉石对局重连";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(136, 44);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(28, 33, 42);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.Manual;
            Point defaultPosition = new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 28, Screen.PrimaryScreen.WorkingArea.Top + 100);
            Location = LoadPosition(defaultPosition);

            Label grip = new Label { Text = "⋮", TextAlign = ContentAlignment.MiddleCenter, Location = new Point(0, 2),
                Size = new Size(14, 40), Font = new Font(Font.FontFamily, 12F), ForeColor = Color.FromArgb(130, 145, 166), Cursor = Cursors.SizeAll };
            grip.MouseDown += DragWindow;
            hint.SetToolTip(grip, "拖动移动 · 右键退出");
            reset.Location = new Point(14, 2); reset.Size = new Size(120, 40);
            reset.FlatStyle = FlatStyle.Flat; reset.FlatAppearance.BorderSize = 0; reset.TabStop = false;
            reset.Font = new Font(Font, FontStyle.Bold); reset.AccessibleName = "酒馆战棋拔线";
            reset.Click += delegate { if (!administrator) ElevateFromUserIntent(); else if (ready) ResetFromUserIntent(); };
            menu.Items.Add(menuStatus);
            menu.Items.Add(menuShortcut);
            ToolStripMenuItem runAs = new ToolStripMenuItem("以管理员运行") { Visible = !administrator };
            runAs.Click += delegate { ElevateFromUserIntent(); };
            menu.Items.Add(runAs);
            ToolStripMenuItem enableLogging = new ToolStripMenuItem("开启炉石日志…");
            enableLogging.Click += delegate { EnableLoggingFromUserIntent(); };
            menu.Items.Add(enableLogging);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem close = new ToolStripMenuItem("退出");
            close.Click += delegate { Close(); };
            menu.Items.Add(close);
            ContextMenuStrip = menu; grip.ContextMenuStrip = menu; reset.ContextMenuStrip = menu;
            Controls.Add(grip); Controls.Add(reset);
            MouseDown += DragWindow;
            timer.Interval = 1000;
            timer.Tick += delegate { UpdateSnapshot(); };
            UpdateSnapshot();
            if (!preview) timer.Start();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (preview || !administrator) return;
            registered = Native.RegisterHotKey(Handle, 1, 0x0001 | 0x0002 | 0x4000, 0x77);
            if (!registered) menuShortcut.Text = "快捷键被占用 · 请点击按钮";
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == 1 && !preview) ResetFromUserIntent();
            base.WndProc(ref message);
        }
        internal void DrawPreview(Bitmap bitmap)
        {
            IntPtr formHandle = Handle;
            DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            foreach (Control control in Controls)
            {
                IntPtr childHandle = control.Handle;
                control.DrawToBitmap(bitmap, control.Bounds);
            }
        }
        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !preview)
            { Native.ReleaseCapture(); Native.SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); }
        }
        private void UpdateSnapshot()
        {
            if (busy) return;
            Snapshot snapshot = Detector.Capture();
            bool cooling = DateTime.UtcNow < cooldownUntil;
            bool showingFeedback = DateTime.UtcNow < feedbackUntil;
            ready = administrator && snapshot.Ready && !cooling && !preview;
            // Keep the button enabled so its right-click menu and tooltip work while waiting.
            // The click handler and ResetFromUserIntent both check whether a reset is allowed.
            reset.Enabled = true;
            reset.ForeColor = Color.White;
            reset.Cursor = (!administrator || ready) ? Cursors.Hand : Cursors.Default;
            string fullStatus;
            if (!administrator)
            {
                reset.Text = "管理员运行";
                reset.BackColor = Color.FromArgb(50, 93, 154);
                fullStatus = showingFeedback ? feedback : "点击启用管理员权限";
            }
            else if (cooling)
            {
                reset.Text = "重连 " + Math.Ceiling((cooldownUntil - DateTime.UtcNow).TotalSeconds) + "s";
                reset.BackColor = Color.FromArgb(67, 77, 96);
                fullStatus = "连接已重置，等待游戏重连";
            }
            else if (showingFeedback)
            {
                reset.Text = "操作失败";
                reset.BackColor = Color.FromArgb(136, 60, 65);
                fullStatus = feedback;
            }
            else if (ready)
            {
                reset.Text = "拔线";
                reset.BackColor = Color.FromArgb(50, 93, 154);
                fullStatus = "请在战斗开始后拔线";
            }
            else
            {
                reset.Text = snapshot.Pid == 0 ? "等待炉石" : "等待对局";
                reset.BackColor = Color.FromArgb(55, 63, 77);
                reset.ForeColor = Color.FromArgb(180, 191, 208);
                fullStatus = snapshot.Status;
            }
            menuStatus.Text = fullStatus;
            hint.SetToolTip(reset, fullStatus + "\n" + snapshot.Detail + "\n" + menuShortcut.Text + "\n拖动左侧移动 · 右键退出");
        }
        private void ResetFromUserIntent()
        {
            // No timer, startup or diagnostic path invokes this method.
            if (preview || busy || !administrator || !Program.IsAdministrator() || DateTime.UtcNow < cooldownUntil) return;
            busy = true;
            reset.Enabled = false;
            try
            {
                // Read everything again immediately before changing the single matched TCP row.
                Snapshot snapshot = Detector.Capture();
                if (!snapshot.Ready)
                { feedback = "当前没有可确认的对局连接"; feedbackUntil = DateTime.UtcNow.AddSeconds(5); return; }
                uint code = Native.ResetOneEstablishedConnection(snapshot.Row);
                if (code == 0)
                {
                    cooldownUntil = DateTime.UtcNow.AddSeconds(15);
                    feedback = "连接已重置，等待游戏重连";
                    feedbackUntil = cooldownUntil;
                }
                else
                {
                    feedback = "重置失败（Windows 错误 " + code + "）";
                    feedbackUntil = DateTime.UtcNow.AddSeconds(7);
                }
            }
            catch (Exception exception)
            { feedback = "重置未完成（" + exception.GetType().Name + "）"; feedbackUntil = DateTime.UtcNow.AddSeconds(7); }
            finally { busy = false; UpdateSnapshot(); }
        }
        private void ElevateFromUserIntent()
        {
            if (preview || administrator) return;
            try
            {
                ProcessStartInfo request = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Program.BaseDirectory };
                using (Process launched = Process.Start(request)) { if (launched != null) Close(); }
            }
            catch (System.ComponentModel.Win32Exception)
            { feedback = "未获取管理员权限"; feedbackUntil = DateTime.UtcNow.AddSeconds(7); UpdateSnapshot(); }
            catch
            { feedback = "管理员启动失败，请右键以管理员运行"; feedbackUntil = DateTime.UtcNow.AddSeconds(7); UpdateSnapshot(); }
        }
        private Point LoadPosition(Point fallback)
        {
            try
            {
                string positionPath = Path.Combine(Program.DataDirectory, "position.txt");
                string legacyPath = Path.Combine(Program.BaseDirectory, "position.txt");
                if (!preview && !File.Exists(positionPath) && File.Exists(legacyPath))
                {
                    Directory.CreateDirectory(Program.DataDirectory);
                    File.Copy(legacyPath, positionPath, false);
                }
                string[] values = File.ReadAllText(positionPath).Trim().Split(',');
                Point point = new Point(Int32.Parse(values[0]), Int32.Parse(values[1]));
                Rectangle bounds = new Rectangle(point, Size);
                foreach (Screen screen in Screen.AllScreens)
                {
                    if (Rectangle.Intersect(screen.WorkingArea, bounds).Width > 0 && Rectangle.Intersect(screen.WorkingArea, bounds).Height > 0)
                        return new Point(Math.Max(screen.WorkingArea.Left, Math.Min(point.X, screen.WorkingArea.Right - Width)),
                            Math.Max(screen.WorkingArea.Top, Math.Min(point.Y, screen.WorkingArea.Bottom - Height)));
                }
            }
            catch { }
            return fallback;
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (registered) Native.UnregisterHotKey(Handle, 1);
            if (!preview) try
            {
                Directory.CreateDirectory(Program.DataDirectory);
                File.WriteAllText(Path.Combine(Program.DataDirectory, "position.txt"), Left + "," + Top);
            }
            catch { }
            timer.Dispose();
            hint.Dispose();
            menu.Dispose();
            base.OnFormClosed(e);
        }
        private void EnableLoggingFromUserIntent()
        {
            if (preview) return;
            try { MessageBox.Show(this, LoggingConfiguration.Enable(), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception exception)
            {
                MessageBox.Show(this, "开启日志未完成：" + exception.Message + "\n请退出炉石并确认配置目录可写后重试。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    internal static class SelfTests
    {
        private static int passed;
        private static StringBuilder report;
        private static void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed++; report.AppendLine("PASS " + name); }
        internal static string Run()
        {
            report = new StringBuilder("无损自测：仅构造输入，不调用 SetTcpEntry，不读取实际游戏日志。\r\n");
            passed = 0;
            try
            {
                DateTime started = new DateTime(2026, 10, 8, 21, 21, 14);
                string gotoLine = "I 21:22:00.1234567 Network.GotoGameServe() - address= 203.0.113.105:3724, game=REDACTED, client=REDACTED";
                Endpoint endpoint = LogParser.LatestActive(LogParser.Parse(gotoLine, started));
                Check(endpoint != null && endpoint.Display == "203.0.113.105:3724", "解析 GotoGameServe 对局地址");
                Check(LogParser.LatestActive(LogParser.Parse("I 21:22:00.000 Network.GotoGameServe() - address= 1.2.3.4:1119", started)) == null, "拒绝 1119 登录连接");
                Check(LogParser.LatestActive(LogParser.Parse("I 21:22:00.000 Network.GotoGameServe() - address= 999.2.3.4:3724", started)) == null, "拒绝非法 IPv4");
                Check(LogParser.LatestActive(LogParser.Parse(gotoLine + "\nI 21:22:01.000 Network.DisconnectFromGameServer() - Reason: EndGameScreen", started)) == null, "结束界面清除旧端点");
                Check(LogParser.LatestActive(LogParser.Parse(gotoLine + "\nI 21:22:01.000 Network.OnGameServerDisconnectEvent() - Disconnected", started)) == null, "异常断开清除旧端点");
                Check(LogParser.LatestActive(LogParser.Parse(gotoLine + "\nD 21:22:01.000 TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", started)) == null, "Power STATE COMPLETE 清除端点");
                Check(LogParser.LatestActive(LogParser.Parse("I 21:21:50.000 Network.OnGameServerDisconnectEvent()\n" + gotoLine, started)) != null, "新 Goto 可恢复有效端点");
                Check(LogParser.LatestActive(LogParser.Parse(gotoLine + "\nI 21:22:01.000 Network.GotoGameServe() - address= [2001:db8::1]:3724", started)) == null, "新 IPv6 Goto 不回退旧 IPv4 地址");
                Check(LogParser.LatestActive(LogParser.Parse(gotoLine + "\nI 21:22:01.000 Network.GotoGameServe() - address= 203.0.", started)) == null, "新损坏 Goto 清除旧地址");
                Check(LogParser.LatestActive(LogParser.Parse(gotoLine + "\nI 21:22:00.1234567 Network.GotoGameServe() - address= 1.2.3.4:3724", started)) == null, "同时间不同 Goto 视为歧义");
                Check(Detector.SessionMatches(started, started.AddSeconds(1)), "日志目录匹配本次启动");
                Check(!Detector.SessionMatches(started, started.AddHours(-2)), "拒绝上一次会话");
                Check(!Detector.SessionMatches(started, started.AddMinutes(3)), "拒绝不匹配的新会话");
                Native.TcpOwnerRow row = new Native.TcpOwnerRow { State = 5, LocalAddr = 1, LocalPort = Native.EncodePort(53427), RemoteAddr = endpoint.Address, RemotePort = Native.EncodePort(3724), OwningPid = 1234 };
                Native.TcpOwnerRow chosen;
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { row }, 1234, endpoint, out chosen) == 1, "只选唯一同 PID 已建立连接");
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { row }, 1235, endpoint, out chosen) == 0, "拒绝错误 PID");
                Native.TcpOwnerRow altered = row; altered.RemotePort = Native.EncodePort(1119);
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { altered }, 1234, endpoint, out chosen) == 0, "拒绝错误端口");
                altered = row; altered.State = 2;
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { altered }, 1234, endpoint, out chosen) == 0, "拒绝非 ESTABLISHED 状态");
                altered = row; altered.RemoteAddr++;
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { altered }, 1234, endpoint, out chosen) == 0, "拒绝错误远端地址");
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { row, row }, 1234, endpoint, out chosen) == 2, "多匹配停用");
                Check(Detector.SelectTarget(new List<Native.TcpOwnerRow> { row }, 1234, null, out chosen) == 0, "无本次会话端点停用");
                Check(Native.DecodePort(Native.EncodePort(53427)) == 53427 && Native.DecodePort(Native.EncodePort(3724)) == 3724, "TCP 端口字节序");
                Check(Marshal.SizeOf(typeof(Native.TcpOwnerRow)) == 24, "TCP owner row 为 24 字节");
                Check(LogParser.LatestActive(LogParser.Parse("I 23:59:59.000 Network.GotoGameServe() - address= 1.2.3.4:3724\nI 00:00:01.000 Network.OnGameServerDisconnectEvent()", started)) == null, "跨午夜结束事件");
                DateTime morning = new DateTime(2026, 10, 8, 10, 0, 0);
                DateTime midnightTail = new DateTime(2026, 10, 9, 0, 1, 5);
                List<LogEvent> acrossFiles = LogParser.Parse("I 23:59:00.000 Network.GotoGameServe() - address= 1.2.3.4:3724", morning, new DateTime(2026, 10, 8, 23, 59, 1));
                acrossFiles.AddRange(LogParser.Parse("D 00:01:00.000 TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE", morning, midnightTail));
                Check(LogParser.LatestActive(acrossFiles) == null, "上午启动后的次日 Power COMPLETE 晚于 Goto");
                Endpoint afterMidnight = LogParser.LatestActive(LogParser.Parse("I 00:01:00.000 Network.GotoGameServe() - address= 1.2.3.4:3724", morning, midnightTail));
                Check(afterMidnight != null && afterMidnight.Time.Date == midnightTail.Date, "凌晨尾部按文件日期锚定");
                List<LogEvent> reverseRollover = LogParser.Parse("I 23:59:59.000 Network.GotoGameServe() - address= 1.2.3.4:3724\nI 00:00:01.000 Network.OnGameServerDisconnectEvent()", morning, midnightTail);
                Check(LogParser.LatestActive(reverseRollover) == null && reverseRollover[0].Time.Date == midnightTail.Date && reverseRollover[1].Time.Date == midnightTail.Date.AddDays(-1), "倒序正确恢复跨午夜日期");
                Check(LogParser.LatestActive(LogParser.Parse("I 10:00:00.000 Network.GotoGameServe() - address= 1.2.3.4:3724\nI 00:01:00.000 Network.OnGameServerDisconnectEvent()", morning, midnightTail)) == null, "跨日稀疏日志不把旧 Goto 推到未来");
                string config = "; global comment\n[Other]\nKey=keep\n[Net]\nLogLevel=0\nKeep = untouched\n[net]\nLogLevel=3\nfileprinting=false\nFilePrinting=false\n; network comment\n[Power]\nVerbose=true\n[GameNetLogger]\nConsolePrinting=true\n";
                string merged = LoggingConfiguration.Merge(config);
                Check(merged.Contains("; global comment") && merged.Contains("[Other]\r\nKey=keep") && merged.Contains("Keep = untouched") && merged.Contains("Verbose=true") && merged.Contains("; network comment"), "日志配置保留其它段、未知键和注释");
                Check(Regex.Matches(merged, @"^\[net\]", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count == 1, "重复日志段合并且保留内容");
                string netSection = Regex.Match(merged, @"^\[net\]\r?\n(.*?)(?=^\[|\z)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
                Check(Regex.Matches(netSection, @"^LogLevel=", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count == 1 && Regex.Matches(netSection, @"^FilePrinting=", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count == 1, "重复日志键只保留一个有效设置");
                Check(Regex.Matches(merged, @"^LogLevel=1\r?$", RegexOptions.Multiline).Count == 3 && Regex.Matches(merged, @"^FilePrinting=true\r?$", RegexOptions.Multiline).Count == 3 && Regex.Matches(merged, @"^ConsolePrinting=false\r?$", RegexOptions.Multiline).Count == 3, "三个日志段均启用文件输出并关闭控制台输出");
                Check(LoggingConfiguration.Merge(merged) == merged, "重复开启日志不会继续改变配置");
                string missingSections = LoggingConfiguration.Merge("[Existing]\r\nCustom=keep\r\n");
                Check(missingSections.Contains("[Existing]\r\nCustom=keep") && missingSections.Contains("[Net]") && missingSections.Contains("[Power]") && missingSections.Contains("[GameNetLogger]"), "缺少日志段时补齐并保留已有段");
                report.AppendLine("全部通过：" + passed + " 项。未执行真实断线。");
            }
            catch (Exception exception) { report.AppendLine("FAIL " + exception.Message); Environment.ExitCode = 1; }
            return report.ToString();
        }
    }
}
