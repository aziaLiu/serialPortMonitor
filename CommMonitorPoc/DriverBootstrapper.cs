using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class DriverBootstrapper
{
    private static readonly string[] KnownServiceNames =
    [
        "ComDrv11x",
        "CommMonitorDrv11"
    ];

    public static void EnsureReady(MonitorProfile profile, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Driver bootstrap is only supported on Windows.");
        }

        if (TryOpenDevice(profile.DevicePath, out _))
        {
            log?.Invoke($"Driver device ready: {profile.DevicePath}");
            return;
        }

        if (!profile.AutoInstallDriver)
        {
            throw new InvalidOperationException(
                $"Driver device {profile.DevicePath} is unavailable and auto-install is disabled.");
        }

        if (!IsAdministrator())
        {
            throw new InvalidOperationException(
                $"Driver device {profile.DevicePath} is unavailable. Automatic driver installation requires running as Administrator.");
        }

        var driverPath = DriverAssetLocator.ResolveDriverFile(profile);
        log?.Invoke($"Driver device unavailable. Using driver file: {driverPath}");

        var serviceName = DriverServiceInstaller.EnsureInstalled(profile, driverPath, log);
        DriverServiceInstaller.EnsureRunning(serviceName, profile.DriverInstallTimeoutMs, log);

        var deadline = DateTime.UtcNow.AddMilliseconds(profile.DriverInstallTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (TryOpenDevice(profile.DevicePath, out _))
            {
                log?.Invoke($"Driver device ready after service start: {profile.DevicePath}");
                return;
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"Driver service '{serviceName}' started, but device {profile.DevicePath} did not become available within {profile.DriverInstallTimeoutMs} ms.");
    }

    private static bool TryOpenDevice(string devicePath, out int win32Error)
    {
        using var handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            0,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        win32Error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return !handle.IsInvalid;
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static class DriverAssetLocator
    {
        public static string ResolveDriverFile(MonitorProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(profile.DriverPath))
            {
                var explicitPath = Path.GetFullPath(profile.DriverPath);
                if (!File.Exists(explicitPath))
                {
                    throw new FileNotFoundException($"Configured driver file was not found: {explicitPath}", explicitPath);
                }

                return explicitPath;
            }

            var folderName = ResolveDriverFolderName();
            var fileName = Environment.Is64BitOperatingSystem ? "ComDrv11x64.sys" : "ComDrv11x86.sys";
            var relativePath = Path.Combine(folderName, fileName);

            foreach (var baseDirectory in EnumerateCandidateRoots())
            {
                var candidate = Path.Combine(baseDirectory, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException(
                $"Unable to find the driver payload '{relativePath}' relative to {AppContext.BaseDirectory}.",
                relativePath);
        }

        private static IEnumerable<string> EnumerateCandidateRoots()
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (visited.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
            }
        }

        private static string ResolveDriverFolderName()
        {
            var osVersion = Environment.OSVersion.Version;
            if (osVersion.Major >= 10)
            {
                return "nt10";
            }

            if (osVersion.Major >= 6)
            {
                return "nt6";
            }

            return "nt5";
        }
    }

    private static class DriverServiceInstaller
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ScManagerCreateService = 0x0002;

        private const uint ServiceQueryConfig = 0x0001;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;

        private const uint ServiceKernelDriver = 0x00000001;
        private const uint ServiceDemandStart = 0x00000003;
        private const uint ServiceErrorNormal = 0x00000001;
        private const uint ServiceNoChange = 0xFFFFFFFF;

        private const uint ServiceStopped = 0x00000001;
        private const uint ServiceStartPending = 0x00000002;
        private const uint ServiceRunning = 0x00000004;

        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceAlreadyRunning = 1056;

        public static string EnsureInstalled(MonitorProfile profile, string driverPath, Action<string>? log)
        {
            using var scm = OpenScManager();
            var desiredServiceName = string.IsNullOrWhiteSpace(profile.DriverServiceName)
                ? KnownServiceNames[0]
                : profile.DriverServiceName;

            var existingServiceName = FindExistingServiceName(scm, desiredServiceName);
            if (existingServiceName is not null)
            {
                using var service = OpenService(scm, existingServiceName);
                ConfigureService(service, existingServiceName, profile, driverPath);
                log?.Invoke($"Driver service found: {existingServiceName}");
                return existingServiceName;
            }

            using var createdService = ServiceNativeMethods.CreateService(
                scm,
                desiredServiceName,
                string.IsNullOrWhiteSpace(profile.DriverDisplayName) ? desiredServiceName : profile.DriverDisplayName,
                ServiceQueryConfig | ServiceChangeConfig | ServiceQueryStatus | ServiceStart,
                ServiceKernelDriver,
                ServiceDemandStart,
                ServiceErrorNormal,
                driverPath,
                null,
                IntPtr.Zero,
                null,
                null,
                null);

            if (createdService.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"CreateService failed for kernel driver service '{desiredServiceName}'.");
            }

            log?.Invoke($"Driver service created: {desiredServiceName}");
            return desiredServiceName;
        }

        public static void EnsureRunning(string serviceName, int timeoutMs, Action<string>? log)
        {
            using var scm = OpenScManager();
            using var service = OpenService(scm, serviceName);

            var status = QueryStatus(service);
            if (status.dwCurrentState == ServiceRunning)
            {
                log?.Invoke($"Driver service already running: {serviceName}");
                return;
            }

            if (!ServiceNativeMethods.StartService(service, 0, null))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceAlreadyRunning)
                {
                    throw new Win32Exception(error, $"StartService failed for kernel driver service '{serviceName}'.");
                }
            }

            WaitForState(service, serviceName, ServiceRunning, timeoutMs);
            log?.Invoke($"Driver service running: {serviceName}");
        }

        private static SafeServiceHandle OpenScManager()
        {
            var scm = ServiceNativeMethods.OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
            if (scm.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");
            }

            return scm;
        }

        private static string? FindExistingServiceName(SafeServiceHandle scm, string desiredServiceName)
        {
            foreach (var serviceName in KnownServiceNames
                         .Append(desiredServiceName)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var service = ServiceNativeMethods.OpenService(
                    scm,
                    serviceName,
                    ServiceQueryConfig | ServiceChangeConfig | ServiceQueryStatus | ServiceStart);

                if (!service.IsInvalid)
                {
                    return serviceName;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceDoesNotExist)
                {
                    throw new Win32Exception(error, $"OpenService failed for '{serviceName}'.");
                }
            }

            return null;
        }

        private static SafeServiceHandle OpenService(SafeServiceHandle scm, string serviceName)
        {
            var service = ServiceNativeMethods.OpenService(
                scm,
                serviceName,
                ServiceQueryConfig | ServiceChangeConfig | ServiceQueryStatus | ServiceStart);

            if (service.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenService failed for '{serviceName}'.");
            }

            return service;
        }

        private static void ConfigureService(
            SafeServiceHandle service,
            string serviceName,
            MonitorProfile profile,
            string driverPath)
        {
            if (!ServiceNativeMethods.ChangeServiceConfig(
                    service,
                    ServiceKernelDriver,
                    ServiceDemandStart,
                    ServiceErrorNormal,
                    driverPath,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null,
                    string.IsNullOrWhiteSpace(profile.DriverDisplayName) ? serviceName : profile.DriverDisplayName))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"ChangeServiceConfig failed for kernel driver service '{serviceName}'.");
            }
        }

        private static ServiceStatus QueryStatus(SafeServiceHandle service)
        {
            if (!ServiceNativeMethods.QueryServiceStatus(service, out var status))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatus failed.");
            }

            return status;
        }

        private static void WaitForState(
            SafeServiceHandle service,
            string serviceName,
            uint desiredState,
            int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var status = QueryStatus(service);
                if (status.dwCurrentState == desiredState)
                {
                    return;
                }

                if (status.dwCurrentState == ServiceStopped && desiredState == ServiceRunning)
                {
                    throw new InvalidOperationException($"Kernel driver service '{serviceName}' stopped during startup.");
                }

                Thread.Sleep(200);
            }

            throw new TimeoutException(
                $"Kernel driver service '{serviceName}' did not reach the expected state within {timeoutMs} ms.");
        }
    }
}

internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeServiceHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle()
    {
        return ServiceNativeMethods.CloseServiceHandle(handle);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatus
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

internal static class ServiceNativeMethods
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeServiceHandle CreateService(
        SafeServiceHandle scm,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeServiceHandle OpenService(
        SafeServiceHandle scm,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig(
        SafeServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartService(
        SafeServiceHandle service,
        int numServiceArgs,
        string[]? serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatus(
        SafeServiceHandle service,
        out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
