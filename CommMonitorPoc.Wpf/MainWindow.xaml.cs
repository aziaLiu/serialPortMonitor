using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace CommMonitorPoc.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private DriverMonitor? _monitor;
    private PacketRowViewModel? _selectedPacket;
    private bool _saveArtifacts = true;
    private bool _followLatest = true;
    private string _statusMessage = "就绪。";
    private bool _isCapturing;

    private string ProfilePath => Path.Combine(AppContext.BaseDirectory, "monitor-profile.json");

    public MainViewModel()
    {
        CaptureToggleCommand = new AsyncRelayCommand(ToggleCaptureAsync);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);

        LoadProfile();
    }

    public ObservableCollection<PacketRowViewModel> Packets { get; } = [];
    public ObservableCollection<PortOptionViewModel> AvailablePorts { get; } = [];

    public ICommand CaptureToggleCommand { get; }
    public ICommand RefreshPortsCommand { get; }

    public bool SaveArtifacts
    {
        get => _saveArtifacts;
        set => SetField(ref _saveArtifacts, value);
    }

    public bool FollowLatest
    {
        get => _followLatest;
        set => SetField(ref _followLatest, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetField(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CaptureButtonText));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => IsCapturing ? "抓包中" : "空闲";
    public string CaptureButtonText => IsCapturing ? "停止" : "开始";
    public string PacketCountText => $"共 {Packets.Count} 条";

    public PacketRowViewModel? SelectedPacket
    {
        get => _selectedPacket;
        set
        {
            if (SetField(ref _selectedPacket, value))
            {
                OnPropertyChanged(nameof(SelectedSummary));
                OnPropertyChanged(nameof(SelectedHex));
                OnPropertyChanged(nameof(SelectedAscii));
            }
        }
    }

    public string SelectedSummary => SelectedPacket is null
        ? "未选择数据包。"
        : $"{SelectedPacket.Direction} {SelectedPacket.Port}  报文长度={SelectedPacket.Length}  声明长度={SelectedPacket.DeclaredLength}  类型={SelectedPacket.Kind}";

    public string SelectedHex => SelectedPacket?.Hex ?? string.Empty;
    public string SelectedAscii => SelectedPacket?.Ascii ?? string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void LoadProfile()
    {
        var profile = MonitorProfile.Load(ProfilePath);
        SaveArtifacts = true;
        RefreshPorts(profile.PortNames);
        StatusMessage = $"已加载配置：{ProfilePath}";
    }

    private void RefreshPorts()
    {
        var selected = string.Join(",", AvailablePorts.Where(x => x.IsSelected).Select(x => x.Name));
        RefreshPorts(selected);
    }

    private void RefreshPorts(string selectedPorts)
    {
        var selectedSet = selectedPorts
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AvailablePorts.Clear();
        foreach (var port in PortDiscovery.GetPortNames())
        {
            AvailablePorts.Add(new PortOptionViewModel
            {
                Name = port,
                IsSelected = selectedSet.Contains(port)
            });
        }

        if (AvailablePorts.Count == 0)
        {
            StatusMessage = "未检测到串口。";
        }
    }

    private async Task ToggleCaptureAsync()
    {
        if (IsCapturing)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    private async Task StartAsync()
    {
        if (IsCapturing)
        {
            return;
        }

        var selectedPorts = AvailablePorts
            .Where(x => x.IsSelected)
            .Select(x => x.Name)
            .ToArray();

        if (selectedPorts.Length == 0)
        {
            StatusMessage = "请至少勾选一个串口。";
            return;
        }

        try
        {
            Packets.Clear();
            OnPropertyChanged(nameof(PacketCountText));

            var profile = MonitorProfile.Load(ProfilePath);
            profile.PortNames = string.Join(",", selectedPorts);
            profile.Save(ProfilePath);

            StatusMessage = "姝ｅ湪妫€鏌ラ┍鍔?...";
            DriverBootstrapper.EnsureReady(profile, message => StatusMessage = message);

            _monitor = new DriverMonitor(profile);
            _monitor.Open();
            _monitor.Initialize();

            _captureCts = new CancellationTokenSource();
            IsCapturing = true;
            StatusMessage = $"正在抓包：{profile.PortNames}";

            _captureTask = Task.Run(async () =>
            {
                try
                {
                    await _monitor.PollPacketsAsync(
                        _captureCts.Token,
                        record =>
                        {
                            Application.Current.Dispatcher.Invoke(() => AddRow(record));
                            return Task.CompletedTask;
                        },
                        saveArtifacts: SaveArtifacts,
                        logToConsole: false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    File.AppendAllText(
                        Path.Combine(AppContext.BaseDirectory, "capture-error.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex}\r\n\r\n");
                }
                finally
                {
                    _monitor?.Dispose();
                    _monitor = null;
                    _captureCts?.Dispose();
                    _captureCts = null;
                    _captureTask = null;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IsCapturing = false;
                        StatusMessage = "抓包已停止。";
                    });
                }
            });
        }
        catch (Exception ex)
        {
            IsCapturing = false;
            StatusMessage = ex.Message;
            _monitor?.Dispose();
            _monitor = null;
            _captureCts?.Dispose();
            _captureCts = null;
            _captureTask = null;
        }
    }

    private async Task StopAsync()
    {
        if (_captureCts is null)
        {
            return;
        }

        _captureCts.Cancel();
        StatusMessage = "正在停止抓包...";
        if (_captureTask is not null)
        {
            try
            {
                await _captureTask;
            }
            catch
            {
            }
        }
    }

    private void AddRow(CaptureRecord record)
    {
        var packet = record.Packet;
        var shouldAutoSelectLatest = FollowLatest || SelectedPacket is null;
        var row = new PacketRowViewModel
        {
            Time = record.CapturedAt.ToString("HH:mm:ss.fff"),
            Direction = packet.Direction switch
            {
                "tx" => "发送",
                "rx" => "接收",
                "tx?" => "发送?",
                "rx?" => "接收?",
                _ => packet.Direction
            },
            Port = packet.PortName,
            Length = packet.Payload.Length,
            DeclaredLength = packet.DeclaredLength,
            TypeCode = packet.TypeCode,
            HeaderKey = $"0x{packet.HeaderXorKey:X2}",
            PayloadKey = $"0x{packet.PayloadXorKey:X2}",
            Kind = packet.Kind switch
            {
                "serial-frame" => "串口报文",
                "port-open" => "打开串口",
                "port-close" => "关闭串口",
                "driver-data" => "驱动事件",
                "header-only" => "仅头部",
                _ => packet.Kind
            },
            Hex = HexCodec.Format(packet.Payload),
            Ascii = AsciiCodec.Format(packet.Payload)
        };

        Packets.Add(row);
        if (Packets.Count > 2500)
        {
            var removed = Packets[0];
            Packets.RemoveAt(0);
            if (ReferenceEquals(SelectedPacket, removed))
            {
                SelectedPacket = null;
                shouldAutoSelectLatest = true;
            }
        }

        if (shouldAutoSelectLatest)
        {
            SelectedPacket = row;
        }

        OnPropertyChanged(nameof(PacketCountText));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class PortOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PacketRowViewModel
{
    public string Time { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public int Length { get; set; }
    public int DeclaredLength { get; set; }
    public int TypeCode { get; set; }
    public string HeaderKey { get; set; } = string.Empty;
    public string PayloadKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Hex { get; set; } = string.Empty;
    public string Ascii { get; set; } = string.Empty;
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;

    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await _execute();
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
