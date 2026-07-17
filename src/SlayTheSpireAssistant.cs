using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("杀戮尖塔助手")]
[assembly: AssemblyDescription("Slay the Spire 单机辅助工具")]
[assembly: AssemblyCompany("Local Tools")]
[assembly: AssemblyProduct("杀戮尖塔助手")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]

internal sealed class AssistantForm : Form
{
    private const string AppVersion = "1.0.3";
    private const string PipeName = "STSAssistantBridge_v1_0_3_2";
    private readonly Label status = new Label();
    private readonly Label detail = new Label();
    private readonly Label version = new Label();
    private readonly NumericUpDown energyAmount = new NumericUpDown();
    private readonly Button energyLockButton = new Button();
    private readonly NumericUpDown goldAmount = new NumericUpDown();
    private readonly Button goldLockButton = new Button();
    private readonly NumericUpDown healthAmount = new NumericUpDown();
    private readonly Button healthLockButton = new Button();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private int injectedPid;
    private int checkedPid;
    private string bridgePath;
    private string logPath;
    private DateTime lastInjectionAttempt = DateTime.MinValue;
    private bool energyLocked;
    private bool healthLocked;
    private bool goldLocked;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attrs, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    public AssistantForm()
    {
        Text = "杀戮尖塔助手 v" + AppVersion;
        ClientSize = new Size(450, 310);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);

        logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlayTheSpireAssistant", "assistant.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));
        Log("助手启动，版本 " + AppVersion);

        status.SetBounds(22, 18, 400, 28);
        status.Font = new Font(Font, FontStyle.Bold);
        status.Text = "● 正在查找游戏……";
        Controls.Add(status);

        energyAmount.SetBounds(318, 65, 96, 30);
        energyAmount.Minimum = 1; energyAmount.Maximum = 999; energyAmount.Value = 3;
        Controls.Add(energyAmount);
        energyLockButton.Text = "锁定能量  (F1)";
        energyLockButton.SetBounds(22, 62, 280, 36);
        energyLockButton.Click += (s, e) => ToggleEnergyLock();
        Controls.Add(energyLockButton);
        energyAmount.ValueChanged += (s, e) => {
            if (energyLocked) Send("ENERGY_LOCK " + (int)energyAmount.Value);
        };

        goldAmount.SetBounds(318, 111, 96, 30);
        goldAmount.Minimum = 0; goldAmount.Maximum = 999999; goldAmount.Value = 999;
        Controls.Add(goldAmount);
        goldLockButton.Text = "锁定金币  (F4)";
        goldLockButton.SetBounds(22, 108, 280, 36);
        goldLockButton.Click += (s, e) => ToggleGoldLock();
        Controls.Add(goldLockButton);
        goldAmount.ValueChanged += (s, e) => {
            if (goldLocked) Send("GOLD_LOCK " + (int)goldAmount.Value);
        };

        healthAmount.SetBounds(318, 157, 96, 30);
        healthAmount.Minimum = 1; healthAmount.Maximum = 999; healthAmount.Value = 50;
        Controls.Add(healthAmount);
        healthLockButton.Text = "锁定生命值  (F2)";
        healthLockButton.SetBounds(22, 154, 280, 36);
        healthLockButton.Click += (s, e) => ToggleHealthLock();
        Controls.Add(healthLockButton);
        healthAmount.ValueChanged += (s, e) => {
            if (healthLocked) Send("HEALTH_LOCK " + (int)healthAmount.Value);
        };

        detail.SetBounds(22, 207, 405, 48);
        detail.ForeColor = Color.DimGray;
        detail.Text = "请先启动游戏并进入一局";
        Controls.Add(detail);

        version.SetBounds(22, 274, 405, 22);
        version.ForeColor = Color.Gray;
        version.Text = "正式版 v" + AppVersion + " · 仅支持 Windows 64 位 Steam 版";
        Controls.Add(version);

        try { bridgePath = ExtractBridge(); }
        catch (Exception ex) {
            Log("释放连接组件失败", ex);
            MessageBox.Show("无法准备连接组件：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            throw;
        }
        timer.Interval = 1200;
        timer.Tick += (s, e) => EnsureConnected();
        timer.Start();
        Shown += (s, e) => EnsureConnected();
    }

    private void AddButton(string text, int x, int y, int width, Action action)
    {
        var button = new Button { Text = text, Left = x, Top = y, Width = width, Height = 36 };
        button.Click += (s, e) => action();
        Controls.Add(button);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, 1, 0, (uint)Keys.F1);
        RegisterHotKey(Handle, 2, 0, (uint)Keys.F2);
        RegisterHotKey(Handle, 4, 0, (uint)Keys.F4);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2); UnregisterHotKey(Handle, 4);
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (energyLocked) {
            string ignored;
            TryCommand("ENERGY_UNLOCK", out ignored);
        }
        if (healthLocked) {
            string ignored;
            TryCommand("HEALTH_UNLOCK", out ignored);
        }
        if (goldLocked) {
            string ignored;
            TryCommand("GOLD_UNLOCK", out ignored);
        }
        base.OnFormClosing(e);
    }

    private void ToggleEnergyLock()
    {
        string command = energyLocked ? "ENERGY_UNLOCK" : "ENERGY_LOCK " + (int)energyAmount.Value;
        string response;
        if (!TryCommand(command, out response)) {
            detail.Text = "尚未连接，请确认游戏已启动";
            EnsureConnected();
            return;
        }
        Log("命令 " + command + " -> " + response);
        if (response.StartsWith("OK ")) {
            energyLocked = !energyLocked;
            energyLockButton.Text = energyLocked ? "关闭能量锁定  (F1)" : "锁定能量  (F1)";
            energyLockButton.BackColor = energyLocked ? Color.PaleGreen : SystemColors.Control;
            SetConnected(response);
        } else {
            detail.Text = Friendly(response);
        }
    }

    private void ToggleHealthLock()
    {
        string command = healthLocked ? "HEALTH_UNLOCK" : "HEALTH_LOCK " + (int)healthAmount.Value;
        string response;
        if (!TryCommand(command, out response)) {
            detail.Text = "尚未连接，请确认游戏已启动";
            EnsureConnected();
            return;
        }
        Log("命令 " + command + " -> " + response);
        if (response.StartsWith("OK ")) {
            healthLocked = !healthLocked;
            healthLockButton.Text = healthLocked ? "关闭生命锁定  (F2)" : "锁定生命值  (F2)";
            healthLockButton.BackColor = healthLocked ? Color.PaleGreen : SystemColors.Control;
            SetConnected(response);
        } else {
            detail.Text = Friendly(response);
        }
    }

    private void ToggleGoldLock()
    {
        string command = goldLocked ? "GOLD_UNLOCK" : "GOLD_LOCK " + (int)goldAmount.Value;
        string response;
        if (!TryCommand(command, out response)) {
            detail.Text = "尚未连接，请确认游戏已启动";
            EnsureConnected();
            return;
        }
        Log("命令 " + command + " -> " + response);
        if (response.StartsWith("OK ")) {
            goldLocked = !goldLocked;
            goldLockButton.Text = goldLocked ? "关闭金币锁定  (F4)" : "锁定金币  (F4)";
            goldLockButton.BackColor = goldLocked ? Color.PaleGreen : SystemColors.Control;
            SetConnected(response);
        } else {
            detail.Text = Friendly(response);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312) {
            int id = m.WParam.ToInt32();
            if (id == 1) ToggleEnergyLock();
            else if (id == 2) ToggleHealthLock();
            else if (id == 4) ToggleGoldLock();
        }
        base.WndProc(ref m);
    }

    private string ExtractBridge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "SlayTheSpireAssistant");
        Directory.CreateDirectory(dir);
        byte[] data;
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("AssistantBridge.dll")) {
            if (input == null) throw new InvalidOperationException("内嵌组件不存在");
            using (var memory = new MemoryStream()) { input.CopyTo(memory); data = memory.ToArray(); }
        }
        string tag;
        using (SHA256 sha = SHA256.Create()) tag = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").Substring(0, 12);
        string path = Path.Combine(dir, "AssistantBridge-" + AppVersion + "-" + tag + ".dll");
        if (!File.Exists(path) || new FileInfo(path).Length != data.Length) File.WriteAllBytes(path, data);
        return path;
    }

    private void EnsureConnected()
    {
        string pong;
        if (TryCommand("PING", out pong)) {
            Process connectedGame = FindGame();
            int connectedPid = connectedGame == null ? injectedPid : connectedGame.Id;
            if (checkedPid != connectedPid) {
                string check;
                if (!TryCommand("CHECK", out check) || check != "OK COMPATIBLE") {
                    status.Text = "● 当前游戏版本不兼容";
                    status.ForeColor = Color.Firebrick;
                    detail.Text = "请保留日志并联系制作者更新适配";
                    Log("兼容性检测失败：" + check);
                    return;
                }
                checkedPid = connectedPid;
                Log("兼容性检测通过，PID=" + connectedPid);
                if (energyLocked) {
                    string lockResponse;
                    TryCommand("ENERGY_LOCK " + (int)energyAmount.Value, out lockResponse);
                }
                if (healthLocked) {
                    string lockResponse;
                    TryCommand("HEALTH_LOCK " + (int)healthAmount.Value, out lockResponse);
                }
                if (goldLocked) {
                    string lockResponse;
                    TryCommand("GOLD_LOCK " + (int)goldAmount.Value, out lockResponse);
                }
            }
            SetConnected(pong);
            return;
        }
        Process game = FindGame();
        if (game == null) {
            injectedPid = 0;
            checkedPid = 0;
            status.Text = "● 尚未检测到游戏";
            status.ForeColor = Color.DarkOrange;
            detail.Text = "启动游戏后会自动连接";
            return;
        }
        int pid = game.Id;
        status.Text = "● 正在连接游戏……";
        status.ForeColor = Color.SteelBlue;
        if (injectedPid != pid || (DateTime.Now - lastInjectionAttempt).TotalSeconds > 8) {
            lastInjectionAttempt = DateTime.Now;
            string error;
            if (!Inject(pid, out error)) {
                status.Text = "● 连接失败";
                status.ForeColor = Color.Firebrick;
                detail.Text = error;
                Log("连接失败，PID=" + pid + "：" + error);
                return;
            }
            injectedPid = pid;
            Log("已加载连接组件，PID=" + pid);
        }
    }

    private Process FindGame()
    {
        Process[] direct = Process.GetProcessesByName("SlayTheSpire");
        if (direct.Length > 0) return direct[0];
        foreach (Process p in Process.GetProcessesByName("javaw")) {
            try {
                if (p.MainWindowTitle == "Slay the Spire") return p;
                if (!String.IsNullOrEmpty(p.MainModule.FileName) &&
                    p.MainModule.FileName.IndexOf("SlayTheSpire", StringComparison.OrdinalIgnoreCase) >= 0) return p;
            } catch { }
        }
        return null;
    }

    private void SetConnected(string message)
    {
        status.Text = "● 已连接《杀戮尖塔》";
        status.ForeColor = Color.SeaGreen;
        if (!String.IsNullOrEmpty(message) && message != "OK CONNECTED") detail.Text = Friendly(message);
        else if (detail.Text.StartsWith("请先") || detail.Text.StartsWith("启动游戏") || detail.Text.StartsWith("尚未")) detail.Text = "连接正常，可使用按钮或 F1/F2/F4";
    }

    private void Send(string command)
    {
        string response;
        if (!TryCommand(command, out response)) {
            detail.Text = "尚未连接，请确认游戏已启动";
            EnsureConnected();
            return;
        }
        Log("命令 " + command + " -> " + response);
        if (response.StartsWith("OK ")) { SetConnected(response); }
        else { detail.Text = Friendly(response); }
    }

    private string Friendly(string response)
    {
        if (response.StartsWith("OK ENERGY ")) return "当前能量：" + response.Substring(10);
        if (response.StartsWith("OK ENERGY_LOCK ")) return "能量已锁定为 " + response.Substring(15);
        if (response == "OK ENERGY_UNLOCK") return "能量锁定已关闭";
        if (response.StartsWith("OK GOLD_LOCK ")) return "金币已锁定为 " + response.Substring(13);
        if (response == "OK GOLD_UNLOCK") return "金币锁定已关闭";
        if (response.StartsWith("OK HEALTH_LOCK ")) return "生命值已锁定（最高为角色最大生命）";
        if (response == "OK HEALTH_UNLOCK") return "生命值锁定已关闭";
        if (response == "ERR NO_COMBAT") return "请先进入战斗";
        if (response == "ERR NO_RUN") return "请先开始一局游戏";
        if (response == "ERR NOT_READY") return "游戏仍在加载，请稍候";
        if (response.StartsWith("ERR ")) return "功能暂不可用（" + response.Substring(4) + "）";
        return response;
    }

    private bool TryCommand(string command, out string response)
    {
        response = null;
        try {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut)) {
                pipe.Connect(250);
                byte[] request = Encoding.UTF8.GetBytes(command);
                pipe.Write(request, 0, request.Length);
                pipe.Flush();
                byte[] buffer = new byte[512];
                int count = pipe.Read(buffer, 0, buffer.Length);
                response = Encoding.UTF8.GetString(buffer, 0, count);
                return count > 0;
            }
        } catch { return false; }
    }

    private bool Inject(int pid, out string error)
    {
        error = null;
        IntPtr process = OpenProcess(0x0002 | 0x0008 | 0x0020 | 0x0400 | 0x0010, false, pid);
        if (process == IntPtr.Zero) { error = "无法打开游戏进程，请尝试以管理员身份运行助手"; return false; }
        try {
            bool wow64;
            if (IsWow64Process(process, out wow64) && wow64) { error = "当前游戏是 32 位版本，正式版仅支持 64 位游戏"; return false; }
            byte[] path = Encoding.Unicode.GetBytes(bridgePath + "\0");
            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length, 0x1000 | 0x2000, 0x04);
            UIntPtr written;
            if (remote == IntPtr.Zero || !WriteProcessMemory(process, remote, path, (UIntPtr)path.Length, out written)) { error = "无法写入连接组件"; return false; }
            try {
                IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                uint threadId;
                IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out threadId);
                if (thread == IntPtr.Zero) { error = "游戏拒绝连接；请检查安全软件设置"; return false; }
                try {
                    if (WaitForSingleObject(thread, 5000) != 0) { error = "连接组件加载超时"; return false; }
                    uint exitCode;
                    if (!GetExitCodeThread(thread, out exitCode) || exitCode == 0) { error = "连接组件加载失败，可能与游戏版本不兼容"; return false; }
                    return true;
                } finally { CloseHandle(thread); }
            } finally { VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000); }
        } finally { CloseHandle(process); }
    }

    private void Log(string message, Exception ex = null)
    {
        try {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            if (ex != null) line += Environment.NewLine + ex;
            File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
        } catch { }
    }

    private static Mutex appMutex;
    [STAThread]
    private static void Main()
    {
        bool created;
        appMutex = new Mutex(true, "Local\\SlayTheSpireAssistant_v1", out created);
        if (!created) {
            MessageBox.Show("助手已经在运行。", "杀戮尖塔助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new AssistantForm()); }
        catch (Exception ex) { MessageBox.Show("助手发生错误：" + ex.Message, "杀戮尖塔助手", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { appMutex.ReleaseMutex(); appMutex.Dispose(); }
    }
}
