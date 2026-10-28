using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AiVisionStudio.Services;

public enum NativeKernelHostState { Stopped, Starting, Ready, Faulted, Disposed }

public sealed class NativeKernelHostOptions
{
    public string ExecutablePath { get; init; } = Environment.GetEnvironmentVariable("AIVISION_NATIVE_EXE");
    public Uri ExplicitEndpoint { get; init; }
    public int? ExplicitKernelProcessId { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public bool HideFrontend { get; init; } = true;
    /// <summary>Opt in to launching the original frontend; default launches its normal kernel command directly.</summary>
    public bool LaunchFrontend { get; init; }
}

/// <summary>
/// Starts the installed kernel with the original normal command in an exclusive Windows job. Only processes
/// in that job are discovered/hidden/terminated. An explicit endpoint is borrowed, never owned.
/// StateChanged callbacks may run on a worker thread.
/// </summary>
public sealed class NativeKernelHost : IDisposable, IAsyncDisposable
{
    private readonly NativeKernelHostOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private NativeHandle _job;
    private NativeHandle _applicationHandle;
    private Timer _windowTimer;
    private int _windowBusy;
    private bool _disposed;
    public NativeKernelHostState State { get; private set; } = NativeKernelHostState.Stopped;
    public string StatusText { get; private set; } = "原版内核尚未启动";
    public Uri Endpoint { get; private set; }
    public int? OwnedProcessId { get; private set; }
    public int? KernelProcessId { get; private set; }
    public bool IsExternal => _options.ExplicitEndpoint != null;
    public event Action<NativeKernelHostState, string> StateChanged;

    public NativeKernelHost(NativeKernelHostOptions options = null) => _options = options ?? new();

    public static string FindExecutable(string configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(configuredPath);
            if (!File.Exists(configured)) throw new FileNotFoundException("指定的原版应用不存在", configured);
            return configured;
        }
        foreach (var candidate in new[] { @"E:\Vision-Studi\ES-Vision-Studio 4.5.1.exe", @"E:\Vision-Studi\ES-Vision-Studio4.5.1.exe" })
            if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("未找到原版 AI Vision Studio 4.5.1，请配置 AIVISION_NATIVE_EXE 或 ExecutablePath");
    }

    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == NativeKernelHostState.Ready && await IsHttpReadyAsync(Endpoint, linked.Token).ConfigureAwait(false)) return Endpoint;
            if (_options.StartupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(_options.StartupTimeout));
            CleanupOwned();
            SetState(NativeKernelHostState.Starting, IsExternal ? "正在连接指定原版内核" : "正在启动独享原版内核");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(_options.StartupTimeout);
            try
            {
                if (IsExternal)
                {
                    Endpoint = ValidateEndpoint(_options.ExplicitEndpoint);
                    if (_options.ExplicitKernelProcessId is int expectedPid)
                    {
                        var endpoints = ListeningEndpoints(expectedPid);
                        if (!endpoints.Any(item => item.Port == Endpoint.Port))
                            throw new InvalidOperationException($"指定端口 {Endpoint.Port} 不属于内核进程 {expectedPid}");
                        KernelProcessId = expectedPid;
                    }
                    await WaitForHttpAsync(Endpoint, timeout.Token).ConfigureAwait(false);
                    SetState(NativeKernelHostState.Ready, $"已连接指定原版内核 {Endpoint}（不负责关闭该实例）");
                    return Endpoint;
                }

                var executable = FindExecutable(_options.ExecutablePath);
                var kernelPath = Path.Combine(Path.GetDirectoryName(executable)!, "kernel", "kernel.exe");
                if (!File.Exists(kernelPath)) throw new FileNotFoundException("原版 kernel.exe 不存在", kernelPath);
                if (_options.LaunchFrontend)
                    StartOwnedProcess(executable, Path.GetDirectoryName(executable)!, "");
                else
                {
                    // The observed original command is kernel/kernel.exe <port>, with the install directory as cwd.
                    // The kernel still performs its own normal initialization and licensing checks.
                    var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
                    int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
                    StartOwnedProcess(kernelPath, Path.GetDirectoryName(executable)!, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                SetState(NativeKernelHostState.Starting, $"独享原版进程 PID {OwnedProcessId} 已启动，等待 kernel.exe HTTP 服务");
                if (_options.HideFrontend) _windowTimer = new Timer(_ => HideOwnedWindows(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (!Native.QueryInformationJobObject(_job, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法检查独享原版进程状态");
                    if (accounting.ActiveProcesses == 0)
                    {
                        Native.GetExitCodeProcess(_applicationHandle, out uint exitCode);
                        throw new InvalidOperationException($"新启动的原版应用及其子进程已退出（代码 {exitCode}）。可能存在单实例限制；没有接管任何既有实例。");
                    }
                    var candidates = OwnedKernelProcesses(kernelPath);
                    foreach (var candidate in candidates)
                    {
                        var endpoints = ListeningEndpoints(candidate);
                        var ready = new List<Uri>();
                        foreach (var endpoint in endpoints)
                            if (await IsHttpReadyAsync(endpoint, timeout.Token).ConfigureAwait(false)) ready.Add(endpoint);
                        // The same service can listen on IPv4 and IPv6. A different port is ambiguous.
                        if (ready.Select(uri => uri.Port).Distinct().Count() > 1)
                            throw new InvalidOperationException("独享内核监听多个 HTTP 端口，无法确定服务端口；请显式配置端点。");
                        if (ready.Count == 0) continue;
                        Endpoint = ready.OrderBy(uri => uri.Host.Contains(':') ? 1 : 0).First();
                        KernelProcessId = candidate;
                        SetState(NativeKernelHostState.Ready, $"独享原版内核已就绪：PID {candidate}，{Endpoint}");
                        return Endpoint;
                    }
                    await Task.Delay(150, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                throw new TimeoutException($"原版内核在 {_options.StartupTimeout.TotalSeconds:0.#} 秒内未就绪");
            }
        }
        catch (Exception error)
        {
            CleanupOwned();
            SetState(NativeKernelHostState.Faulted, error is OperationCanceledException ? "原版内核启动已取消" : error.Message);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            CleanupOwned();
            SetState(NativeKernelHostState.Stopped, "原版内核已断开；独享实例已关闭");
        }
        finally { _gate.Release(); }
    }

    private void StartOwnedProcess(string executable, string workingDirectory, string arguments)
    {
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建独享进程 Job");
        var limits = new JobExtendedLimits { BasicLimitInformation = new JobBasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!Native.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置独享进程生命周期");
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), dwFlags = 1, wShowWindow = (short)(_options.HideFrontend ? 0 : 5) };
        if (!Native.CreateProcess(executable, new StringBuilder('"' + executable + '"' + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments)), IntPtr.Zero, IntPtr.Zero,
                false, 0x4 | 0x08000000, IntPtr.Zero, workingDirectory, ref startup, out var created)) // CREATE_SUSPENDED | CREATE_NO_WINDOW
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法正常启动原版应用");
        _applicationHandle = new NativeHandle(created.hProcess);
        using var threadHandle = new NativeHandle(created.hThread);
        if (!Native.AssignProcessToJobObject(_job, _applicationHandle))
        {
            int error = Marshal.GetLastWin32Error();
            Native.TerminateProcess(_applicationHandle, 1); // This suspended process was created by this call only.
            throw new Win32Exception(error, "无法隔离新启动的原版应用，已取消启动");
        }
        OwnedProcessId = (int)created.dwProcessId;
        if (Native.ResumeThread(threadHandle) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复新启动的原版应用");
    }

    private List<int> OwnedKernelProcesses(string expectedPath)
    {
        var result = new List<int>();
        foreach (uint pid in OwnedProcessIds(_job))
        {
            using var handle = Native.OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid) continue; // An owned child can exit between the snapshot and this call.
            var path = new StringBuilder(32768); int length = path.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, path, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对独享内核路径");
            if (string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                result.Add((int)pid);
        }
        return result;
    }

    private HashSet<uint> OwnedProcessIds(NativeHandle job)
    {
        int size = 4096;
        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!Native.QueryInformationJobObject(job, 3, buffer, (uint)size, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 234 && size < 1024 * 1024) { size *= 2; continue; }
                    throw new Win32Exception(error, "无法读取独享进程列表");
                }
                int count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > (size - 8) / IntPtr.Size) throw new InvalidDataException("独享进程列表长度无效");
                return Enumerable.Range(0, count).Select(index => (uint)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64()).ToHashSet();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private void HideOwnedWindows()
    {
        if (Interlocked.Exchange(ref _windowBusy, 1) != 0) return;
        try { HideOwnedWindowsCore(); }
        finally { Volatile.Write(ref _windowBusy, 0); }
    }

    private void HideOwnedWindowsCore()
    {
        var job = _job;
        if (job == null || job.IsClosed || job.IsInvalid) return;
        // Keep the original frontend hidden even if it explicitly shows a window after startup.
        HashSet<uint> processes;
        try { processes = OwnedProcessIds(job); }
        catch (ObjectDisposedException) { return; }
        catch (Win32Exception) when (job.IsClosed) { return; }
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint pid);
            if (processes.Contains(pid)) Native.ShowWindowAsync(window, 0);
            return true;
        }, IntPtr.Zero);
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Host != "localhost" && (!IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))))
            throw new ArgumentException("原版内核端点必须是本机 loopback HTTP 地址");
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static async Task WaitForHttpAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        while (!await IsHttpReadyAsync(endpoint, cancellationToken).ConfigureAwait(false))
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsHttpReadyAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) };
        try
        {
            // GET / establishes HTTP readiness without mutating a project. A 404 is a valid HTTP response.
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    internal static List<Uri> ListeningEndpoints(int ownerPid)
    {
        var result = new List<Uri>();
        foreach (var family in new[] { 2, 23 }) // AF_INET / AF_INET6
        {
            int size = 0;
            uint error = Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (error != 0 && error != 122) throw new Win32Exception((int)error, "无法读取内核监听端口");
            if (size < 4) continue;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                error = Native.GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (error != 0) throw new Win32Exception((int)error, "无法读取内核监听端口");
                int count = Marshal.ReadInt32(buffer);
                int rowSize = family == 2 ? Marshal.SizeOf<Tcp4Row>() : Marshal.SizeOf<Tcp6Row>();
                if (count < 0 || count > (size - 4) / rowSize) throw new InvalidDataException("系统 TCP 端口表长度无效");
                for (int index = 0; index < count; index++)
                {
                    var pointer = IntPtr.Add(buffer, 4 + index * rowSize);
                    uint pid, port; IPAddress address;
                    if (family == 2)
                    {
                        var row = Marshal.PtrToStructure<Tcp4Row>(pointer); pid = row.Pid; port = row.LocalPort;
                        address = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
                        if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
                    }
                    else
                    {
                        var row = Marshal.PtrToStructure<Tcp6Row>(pointer); pid = row.Pid; port = row.LocalPort;
                        address = new IPAddress(row.LocalAddress, row.LocalScope);
                        if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
                    }
                    if (pid != ownerPid || !IPAddress.IsLoopback(address)) continue;
                    int hostPort = (ushort)IPAddress.NetworkToHostOrder((short)port);
                    result.Add(new UriBuilder("http", address.ToString(), hostPort).Uri);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result.Distinct().ToList();
    }

    private void CleanupOwned()
    {
        _windowTimer?.Dispose(); _windowTimer = null;
        _job?.Dispose(); _job = null; // Closing only our job terminates only processes assigned to it.
        _applicationHandle?.Dispose(); _applicationHandle = null;
        OwnedProcessId = null; KernelProcessId = null; Endpoint = null;
    }

    private void SetState(NativeKernelHostState state, string text)
    {
        State = state; StatusText = text;
        StateChanged?.Invoke(state, text);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true; CleanupOwned();
            SetState(NativeKernelHostState.Disposed, "原版内核连接已释放");
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeHandle() : base(true) { }
        public NativeHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    { public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    { public JobBasicLimits BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    { public int cb; public string lpReserved, lpDesktop, lpTitle; public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct Tcp4Row
    { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, Pid; }
    [StructLayout(LayoutKind.Sequential)] private struct Tcp6Row
    { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress; public uint LocalScope, LocalPort; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress; public uint RemoteScope, RemotePort, State, Pid; }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern NativeHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(NativeHandle job, int informationClass, ref JobExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool QueryInformationJobObject(NativeHandle job, int informationClass, out JobAccounting info, uint length, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool QueryInformationJobObject(NativeHandle job, int informationClass, IntPtr info, uint length, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(NativeHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(NativeHandle job, NativeHandle process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(NativeHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(NativeHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern NativeHandle OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(NativeHandle process, uint flags, StringBuilder path, ref int size);
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    }
}
