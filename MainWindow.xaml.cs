using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MySystemGui.Services;
using Wpf.Ui.Controls;

namespace MySystemGui;

public partial class MainWindow : FluentWindow
{
    private const string DownloadMediaUrl = "https://massgrave.dev/genuine-installation-media";
    private const int MaxActivityLogParagraphs = 500;
    private const int MaxFullLogParagraphs = 10000;
    private static readonly TimeSpan MasScriptTimeout = TimeSpan.FromMinutes(30);
    private static readonly Regex MasVersionRegex = new(
        @"^@set\s+masver\s*=\s*(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
    private static readonly Regex MasFailureLineRegex = new(
        @"(?im)^\s*(?:[>\[\]=*#-]\s*)*(?:error|failed)\b",
        RegexOptions.Compiled);
    private static readonly SolidColorBrush InfoLogBrush = CreateFrozenBrush(192, 132, 252);
    private static readonly SolidColorBrush SuccessLogBrush = CreateFrozenBrush(74, 222, 128);
    private static readonly SolidColorBrush WarningLogBrush = CreateFrozenBrush(251, 191, 36);
    private static readonly SolidColorBrush ErrorLogBrush = CreateFrozenBrush(248, 113, 113);
    private readonly Queue<LogEntry> _logHistory = new();
    private readonly MaintenanceService _maintenanceService = new();
    private readonly OfficeInstallerService _officeInstallerService = new();
    private readonly DispatcherTimer _notificationTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5)
    };
    private bool _isBusy;
    private bool _isClosing;
    private int _notificationGeneration;
    private Window? _fullLogWindow;
    private RichTextBox? _fullLogTextBox;
    private string _activeMenuName = string.Empty;
    private string _officeOperation = string.Empty;
    private string _selectedOfficeCategory = "Office Suites - Retail";
    private string _selectedOfficeTarget = string.Empty;
    private string _savedKmsServer = string.Empty;
    private bool _hasSavedKmsServer;
    private ModalStep _currentModalStep;
    private ModalStep _officeAppsBackStep;

    private enum ModalStep
    {
        None,
        OfficeStep1,
        OfficeStep2,
        OfficeStep3,
        OfficeChannelPicker,
        ExtrasStep1,
        ExtrasStep2
    }

    private enum LogStatus
    {
        Info,
        Success,
        Warning,
        Error
    }

    private sealed record LogEntry(string Message, LogStatus Status);

    public MainWindow()
    {
        InitializeComponent();
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            HideNotificationWithAnimation();
        };
        AppendLog("> Ready.", LogStatus.Info);

        var masVersion = GetMasScriptVersion();
        TxtAppVersion.Text = string.IsNullOrWhiteSpace(masVersion)
            ? GetAssemblyVersion()
            : $"v{masVersion}";

        try
        {
            TxtWindowsEditionInfo.Text = _maintenanceService.GetSystemBuildInfo().ToString();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or UnauthorizedAccessException or SecurityException or IOException)
        {
            TxtWindowsEditionInfo.Text = "Current Edition: [Unavailable]";
            ShowNotification(
                "Could not read Windows build information. Some system details may be unavailable.",
                LogStatus.Warning,
                ex.Message);
        }

        TxtKmsServer.TextChanged += TxtKmsServer_TextChanged;
        TSforgeActivationMethod.SelectionChanged += TSforgeActivationMethod_SelectionChanged;
        ToggleKmsRenewal.Checked += OnlineKmsToggle_Changed;
        ToggleKmsRenewal.Unchecked += OnlineKmsToggle_Changed;
        ToggleKmsEdition.Checked += OnlineKmsToggle_Changed;
        ToggleKmsEdition.Unchecked += OnlineKmsToggle_Changed;

        foreach (var toggle in new[]
        {
            ToggleAccess, ToggleExcel, ToggleOneNote, ToggleOutlookClassic,
            TogglePowerPoint, ToggleProject, TogglePublisher, ToggleVisio,
            ToggleWord, ToggleSkypeForBusiness, ToggleOutlookNew, ToggleOneDrive, ToggleTeams
        })
        {
            toggle.Checked += OfficeAppToggle_Changed;
            toggle.Unchecked += OfficeAppToggle_Changed;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _notificationTimer.Stop();
        var fullLogWindow = _fullLogWindow;
        _fullLogWindow = null;
        _fullLogTextBox = null;
        fullLogWindow?.Close();
        base.OnClosed(e);
    }

    private void SetBusyState(bool isBusy)
    {
        _isBusy = isBusy;
        BtnModule1.IsEnabled = !isBusy;
        BtnModule2.IsEnabled = !isBusy;
        BtnModule3.IsEnabled = !isBusy;
        BtnModule4.IsEnabled = !isBusy;
        BtnModule5.IsEnabled = !isBusy;
        BtnModule6.IsEnabled = !isBusy;
        BtnModule7.IsEnabled = !isBusy;
        BtnModule8.IsEnabled = !isBusy;
        BtnModule9.IsEnabled = !isBusy;
        BtnModule10.IsEnabled = !isBusy;
    }

    private bool ConfirmTask(string title, string message) =>
        System.Windows.MessageBox.Show(
            this,
            message,
            title,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

    private static string GetMasScriptVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name =>
                    name.EndsWith("MAS_AIO.cmd", StringComparison.OrdinalIgnoreCase));

            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        var match = MasVersionRegex.Match(line);
                        if (match.Success)
                        {
                            return match.Groups[1].Value.Trim();
                        }
                    }
                }
            }

            var externalPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MAS_AIO.cmd");
            if (File.Exists(externalPath))
            {
                foreach (var line in File.ReadLines(externalPath))
                {
                    var match = MasVersionRegex.Match(line);
                    if (match.Success)
                    {
                        return match.Groups[1].Value.Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException)
        {
        }

        return string.Empty;
    }

    private static string GetAssemblyVersion()
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion is null
            ? "v3.14"
            : $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }

    private async void BtnModule1_Click(object sender, RoutedEventArgs e) =>
        await RunMasScriptAsync("/HWID");

    private void BtnModule2_Click(object sender, RoutedEventArgs e) => ShowSubMenu("Ohook Activation", ViewOhook);
    private void BtnModule3_Click(object sender, RoutedEventArgs e) => ShowSubMenu("TSforge Activation", ViewTSforge);
    private void BtnModule4_Click(object sender, RoutedEventArgs e) => ShowSubMenu("Online KMS Activation", ViewOnlineKMS);
    private async void BtnModule5_Click(object sender, RoutedEventArgs e) =>
        await RunMaintenanceTaskAsync(MaintenanceTask.LicenseStatusCheck);
    private void BtnModule6_Click(object sender, RoutedEventArgs e) => ShowSubMenu("Change Windows Edition", ViewWinEdition);
    private void BtnModule7_Click(object sender, RoutedEventArgs e)
    {
        ShowSubMenu("Change Office Edition", ViewOfficeEdition);
        ShowOfficeStep(ModalStep.OfficeStep1);
    }
    private void BtnModule8_Click(object sender, RoutedEventArgs e) => ShowSubMenu("Troubleshoot", ViewTroubleshoot);
    private void BtnModule9_Click(object sender, RoutedEventArgs e)
    {
        ShowSubMenu("Extras", ViewExtras);
        ShowExtrasStep(ModalStep.ExtrasStep1);
    }

    private async void BtnModule10_Click(object sender, RoutedEventArgs e) =>
        await DownloadAndRunOfficeInstallerAsync();

    private async Task DownloadAndRunOfficeInstallerAsync()
    {
        if (_isBusy)
        {
            ShowNotification("Another task is already running. Please wait until it finishes.", LogStatus.Warning);
            return;
        }

        OfficeInstallation? installedBefore = null;
        var officeCheckSucceeded = true;
        try
        {
            installedBefore = OfficeInstallerService.DetectInstalledOffice();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            officeCheckSucceeded = false;
            AppendLog("> Could not check the existing Office installation: " + ex.Message, LogStatus.Warning);
        }

        var confirmationMessage = installedBefore is not null
            ? $"Office Click-to-Run is already detected ({installedBefore.ProductReleaseIds}, version {installedBefore.PackageVersion}). " +
              "Running setup may update or modify that installation. Continue?"
            : officeCheckSucceeded
                ? "No Office Click-to-Run installation was detected. Download the latest Microsoft 365 Apps installer from Microsoft's Office service to a temporary folder, then open it. " +
                  "The installer will use the Internet to download Office. You will handle its prompts; the app will not install silently. " +
                  "A valid Microsoft 365 or Office license is required for activation. Continue?"
                : "The app could not determine whether Office is already installed. Download the latest Microsoft 365 Apps installer from Microsoft's Office service to a temporary folder, then open it? " +
                  "The installer will use the Internet and may update or modify an existing Office installation. A valid license is required for activation.";

        if (!ConfirmTask("Microsoft 365 setup", confirmationMessage))
        {
            ShowNotification("Microsoft 365 setup was not started.", LogStatus.Info);
            return;
        }

        SetBusyState(true);
        try
        {
            ShowNotification("Preparing the official Microsoft 365 installer.", LogStatus.Info);
            AppendLog("> Checking for a cached Microsoft Office installer in Temp...", LogStatus.Info);

            var lastLoggedProgress = 0;
            var installer = await _officeInstallerService.GetInstallerAsync(progress =>
            {
                var roundedProgress = progress / 10 * 10;
                if (roundedProgress >= lastLoggedProgress + 10)
                {
                    lastLoggedProgress = roundedProgress;
                    AppendLog($"> Office installer download: {roundedProgress}%", LogStatus.Info);
                }
            });

            if (installer.UsedCachedInstaller)
            {
                AppendLog("> Using the cached Microsoft Office installer from Temp.", LogStatus.Info);
            }
            else
            {
                AppendLog("> Download complete. Opening the Microsoft installer...", LogStatus.Success);
            }

            var installerProcess = Process.Start(new ProcessStartInfo
            {
                FileName = installer.Path,
                UseShellExecute = true
            });

            if (installerProcess is null)
            {
                throw new InvalidOperationException("Windows did not start the Microsoft 365 installer.");
            }

            await installerProcess.WaitForExitAsync();
            if (!_isClosing)
            {
                try
                {
                    var installedAfter = OfficeInstallerService.DetectInstalledOffice();
                    if (installedAfter is not null)
                    {
                        var status = installerProcess.ExitCode != 0
                            ? LogStatus.Warning
                            : installedBefore is null && officeCheckSucceeded
                                ? LogStatus.Success
                                : LogStatus.Info;
                        var message = installedBefore is null
                            ? "Office installation was detected."
                            : "Office remains detected after setup.";
                        AppendLog(
                            $"> {message} {installedAfter.ProductReleaseIds}; version {installedAfter.PackageVersion}. " +
                            $"Setup exit code: {installerProcess.ExitCode}.",
                            status);
                        ShowNotification(message, status, "Open Word or Excel to confirm it starts and run Ohook to activate it.");
                    }
                    else
                    {
                        AppendLog(
                            $"> Office Click-to-Run was not detected after setup. Exit code: {installerProcess.ExitCode}.",
                            LogStatus.Warning);
                        ShowNotification(
                            "Office was not detected after setup closed.",
                            LogStatus.Warning,
                            "The installer exit code alone does not confirm installation. Check the Microsoft installer window for details.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    AppendLog(
                        $"> Could not verify Office after setup. Exit code: {installerProcess.ExitCode}. {ex.Message}",
                        LogStatus.Warning);
                    ShowNotification(
                        "The installer closed, but the app could not verify Office installation.",
                        LogStatus.Warning,
                        ex.Message);
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (!_isClosing)
            {
                ShowNotification("Microsoft 365 setup was cancelled at the Windows permission prompt.", LogStatus.Info);
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or UnauthorizedAccessException or SecurityException or
                InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            if (!_isClosing)
            {
                ShowNotification(
                    "Could not download or start Microsoft 365 setup. Check your Internet connection and try again.",
                    LogStatus.Error,
                    ex.Message);
            }
        }
        finally
        {
            if (!_isClosing)
            {
                SetBusyState(false);
            }
        }
    }

    private void BtnGithub_Click(object sender, RoutedEventArgs e) => OpenWebsite("https://github.com/massgravel/microsoft-activation-scripts");
    private void BtnBlog_Click(object sender, RoutedEventArgs e) => OpenWebsite("https://massgrave.dev/blog");
    private void BtnContactUs_Click(object sender, RoutedEventArgs e) => OpenWebsite("http://massgrave.dev/contactus");

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _logHistory.Clear();
        TxtConsole.Document.Blocks.Clear();
        _fullLogTextBox?.Document.Blocks.Clear();
    }

    private void OpenFullLog_Click(object sender, RoutedEventArgs e)
    {
        if (_fullLogWindow is not null)
        {
            if (_fullLogWindow.WindowState == WindowState.Minimized)
            {
                _fullLogWindow.WindowState = WindowState.Normal;
            }

            _fullLogWindow.Activate();
            return;
        }

        var fullLogTextBox = new RichTextBox
        {
            Background = Brushes.Transparent,
            CaretBrush = new SolidColorBrush(Color.FromRgb(224, 68, 184)),
            FontFamily = TxtConsole.FontFamily,
            FontSize = TxtConsole.FontSize,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4)
        };
        var document = new FlowDocument
        {
            Background = Brushes.Transparent,
            FontFamily = TxtConsole.FontFamily,
            FontSize = TxtConsole.FontSize,
            FontWeight = FontWeights.Medium,
            PagePadding = new Thickness(0)
        };

        foreach (var entry in _logHistory)
        {
            document.Blocks.Add(CreateLogParagraph(entry.Message, GetLogForeground(entry.Status)));
        }

        fullLogTextBox.Document = document;
        var surfaceBrush = new SolidColorBrush(Color.FromRgb(11, 13, 16));
        var fullLogWindow = new Window
        {
            Title = "MAS",
            Width = 1100,
            Height = 760,
            MinWidth = 720,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = surfaceBrush,
            Content = new System.Windows.Controls.Border
            {
                Background = surfaceBrush,
                BorderBrush = new SolidColorBrush(Color.FromRgb(42, 48, 56)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Child = fullLogTextBox
            }
        };

        _fullLogTextBox = fullLogTextBox;
        _fullLogWindow = fullLogWindow;
        fullLogWindow.Closed += (_, _) =>
        {
            _fullLogTextBox = null;
            _fullLogWindow = null;
        };
        fullLogWindow.Show();
    }

    private void ShowSubMenu(string menuName, UIElement selectedView)
    {
        _activeMenuName = menuName;
        TxtModalTitle.Text = menuName;
        TxtModalBreadcrumb.Text = string.Empty;
        TxtModalBreadcrumb.Visibility = Visibility.Collapsed;
        BtnModalBack.Visibility = Visibility.Collapsed;
        _currentModalStep = ModalStep.None;

        ViewOhook.Visibility = Visibility.Collapsed;
        ViewTSforge.Visibility = Visibility.Collapsed;
        ViewOnlineKMS.Visibility = Visibility.Collapsed;
        ViewWinEdition.Visibility = Visibility.Collapsed;
        ViewOfficeEdition.Visibility = Visibility.Collapsed;
        ViewTroubleshoot.Visibility = Visibility.Collapsed;
        ViewExtras.Visibility = Visibility.Collapsed;

        selectedView.Visibility = Visibility.Visible;
        ModalOverlay.Visibility = Visibility.Visible;
    }

    private void CloseModal_Click(object sender, RoutedEventArgs e)
    {
        CloseCurrentModal();
    }

    private void CloseCurrentModal()
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        _currentModalStep = ModalStep.None;
    }

    private async void ModalAction_Click(object sender, RoutedEventArgs e)
    {
        var optionName = GetOptionName(sender);

        if (_activeMenuName == "Troubleshoot" && optionName == "[4] Fix WMI")
        {
            CloseCurrentModal();
            await RunMaintenanceTaskAsync(MaintenanceTask.WmiRepositoryRepair);
            return;
        }

        if (_activeMenuName == "Troubleshoot" && optionName == "[5] Fix Licensing")
        {
            await OpenMasConsoleWorkflowAsync(
                "Fix Licensing",
                "In MAS, choose Main Menu > [8] Troubleshoot > [5] Fix Licensing.",
                "Fix Licensing clears Windows and Office licensing records and can require reactivation. " +
                "MAS will show its own warning and confirmation before applying changes. Open that workflow?",
                optionName);
            return;
        }

        if (_activeMenuName == "Change Windows Edition")
        {
            await OpenMasConsoleWorkflowAsync(
                "Change Windows Edition",
                "In MAS, choose Main Menu > [6] Change Windows Edition, then select an edition supported by this PC.",
                "Open MAS's native Windows edition workflow? The edition choice shown here is not passed to MAS; " +
                "the script will show the editions available on this PC and may require a restart.",
                optionName);
            return;
        }

        if (_activeMenuName == "Change Office Edition" && optionName == "[3] Remove edition")
        {
            await OpenMasConsoleWorkflowAsync(
                "Remove Office edition",
                "In MAS, choose Main Menu > [7] Change Office Edition > [3] Remove edition.",
                "Open MAS's native Office edition removal workflow? Select the installed edition in MAS before confirming.",
                optionName);
            return;
        }

        if (_activeMenuName == "Ohook Activation")
        {
            string? args = optionName switch
            {
                "[1] Install Ohook Office Activation" => "/Ohook",
                "[2] Uninstall Ohook" => "/Ohook-Uninstall",
                _ => null
            };

            if (args is not null)
            {
                CloseCurrentModal();
                await RunMasScriptAsync(args);
                return;
            }
        }

        if (_activeMenuName == "TSforge Activation")
        {
            if (optionName == "[D] Activate - Manually Select Products")
            {
                CloseCurrentModal();
                ShowNotification(
                    "Manual TSforge activation is not available in this app.",
                    LogStatus.Info,
                    "No activation command was run. For genuine activation, use Windows Settings > System > Activation, " +
                    "or Office > File > Account > Activate Product with a valid license or product key.");
                return;
            }

            if (optionName == "[6] Remove TSforge Activation")
            {
                CloseCurrentModal();
                ShowNotification(
                    "TSforge has no separate uninstall action.",
                    LogStatus.Info,
                    "MAS option [6] only displays guidance. To reset activation status, MAS recommends Troubleshoot > Fix Licensing. No changes were made.");
                return;
            }

            string? baseArg = optionName switch
            {
                "[1] Activate - Windows" => "/Z-Windows",
                "[2] Activate - ESU" => "/Z-ESU",
                "[3] Activate - Office [All]" => "/Z-Office",
                "[4] Activate - Office [Project/Visio]" => "/Z-ProjectVisio",
                "[5] Activate - All" => "/Z-WindowsESUOffice",
                "[A] Activate - Windows KMS Host" => "/Z-WinHost",
                "[B] Activate - Office KMS Host" => "/Z-OffHost",
                "[C] Activate - Windows 8/8.1 APPX Sideloading" => "/Z-APPX",
                "[E] Reset - Rearm/Timers/Tamper/Lock" => "/Z-Reset",
                _ => null
            };

            if (baseArg is not null)
            {
                bool appendMethod = optionName is
                    "[1] Activate - Windows" or
                    "[2] Activate - ESU" or
                    "[3] Activate - Office [All]" or
                    "[4] Activate - Office [Project/Visio]" or
                    "[5] Activate - All";

                string fullArgs = appendMethod
                    ? baseArg + GetTsforgeMethodArgument()
                    : baseArg;

                CloseCurrentModal();
                await RunMasScriptAsync(fullArgs);
                return;
            }
        }

        if (_activeMenuName == "Online KMS Activation")
        {
            if (optionName == "[7] Uninstall Online KMS")
            {
                CloseCurrentModal();
                await RunMasScriptAsync("/K-Uninstall");
                return;
            }

            string? baseArg = optionName switch
            {
                "[1] Activate - Windows" => "/K-Windows",
                "[2] Activate - Office [All]" => "/K-Office",
                "[3] Activate - Office [Project/Visio]" => "/K-ProjectVisio",
                "[4] Activate - All" => "/K-WindowsOffice",
                _ => null
            };

            if (baseArg is not null)
            {
                if (HasUnsavedKmsServerInput())
                {
                    ShowNotification(
                        "KMS Server:Port has unsaved changes. Click Apply or clear the field first.",
                        LogStatus.Warning,
                        $"Current input: '{TxtKmsServer.Text.Trim()}'; saved: '{_savedKmsServer}'");
                    return;
                }

                string fullArgs = baseArg + BuildOnlineKmsModifiers();
                CloseCurrentModal();
                await RunMasScriptAsync(fullArgs);
                return;
            }
        }

        if (_activeMenuName == "Extras")
        {
            string? oemArgs = optionName switch
            {
                "[1] HWID [Windows]" => "/HWID",
                "[2] Ohook [Office]" => "/Ohook",
                "[3] TSforge [Windows / ESU / Office]" => "/Z-WindowsESUOffice",
                "[4] Online KMS [Windows / Office]" => "/K-WindowsOffice",
                "[5] HWID [Windows] + Ohook [Office]" => "/HWID /Ohook",
                "[6] HWID [Windows] + Ohook [Office] + TSforge [ESU]" => "/HWID /Ohook /Z-ESU",
                "[7] TSforge [Windows / ESU] + Ohook [Office]" => "/Z-Windows /Z-ESU /Ohook",
                _ => null
            };

            if (oemArgs is not null)
            {
                CloseCurrentModal();
                CreateOemPresetOnDesktop(oemArgs);
                return;
            }
        }

        SimulateAction(_activeMenuName, optionName);
    }

    private string GetTsforgeMethodArgument()
    {
        var selected = (TSforgeActivationMethod.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();
        return selected switch
        {
            "StaticCID" => " /Z-SCID",
            "ZeroCID" => " /Z-ZCID",
            "KMS4k" => " /Z-KMS4k",
            _ => string.Empty
        };
    }

    private string BuildOnlineKmsModifiers()
    {
        var sb = new StringBuilder();

        if (ToggleKmsRenewal.IsChecked != true)
        {
            sb.Append(" /K-NoRenewalTask");
        }

        if (ToggleKmsEdition.IsChecked != true)
        {
            sb.Append(" /K-NoEditionChange");
        }

        if (_hasSavedKmsServer &&
            !string.IsNullOrWhiteSpace(_savedKmsServer) &&
            string.Equals(
                TxtKmsServer.Text.Trim(),
                _savedKmsServer,
                StringComparison.Ordinal))
        {
            var raw = _savedKmsServer.Trim();
            var colonIndex = raw.IndexOf(':');
            string hostPart = colonIndex >= 0 ? raw[..colonIndex].Trim() : raw;
            string portPart = colonIndex >= 0 ? raw[(colonIndex + 1)..].Trim() : string.Empty;

            if (!string.IsNullOrEmpty(hostPart))
            {
                string normalizedHost = hostPart.StartsWith("://", StringComparison.Ordinal)
                    ? hostPart
                    : $"://{hostPart}";
                sb.Append($" /{normalizedHost}");
            }

            if (!string.IsNullOrEmpty(portPart))
            {
                sb.Append($" /K-Port-{portPart}");
            }
        }

        return sb.ToString();
    }

    private bool HasUnsavedKmsServerInput()
    {
        var current = TxtKmsServer.Text.Trim();
        return !string.IsNullOrEmpty(current) &&
               (!_hasSavedKmsServer ||
                !string.Equals(current, _savedKmsServer, StringComparison.Ordinal));
    }

    private async Task RunMasScriptAsync(string arguments)
    {
        if (_isBusy)
        {
            ShowNotification(
                "Another task is already running. Please wait until it finishes.",
                LogStatus.Warning);
            return;
        }

        if (!ConfirmTask(
                "Confirm MAS action",
                $"Run MAS_AIO.cmd with these arguments?\n{arguments}\n\nThis may modify Windows or Office licensing state."))
        {
            ShowNotification("MAS_AIO.cmd run cancelled.", LogStatus.Info);
            return;
        }

        string scriptPath;
        try
        {
            scriptPath = MasScriptProvider.CreateTemporaryScript();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ShowNotification(
                "The embedded MAS script could not be prepared. See the Activity Log for details.",
                LogStatus.Error,
                ex.Message);
            return;
        }

        SetBusyState(true);
        var elapsed = Stopwatch.StartNew();
        bool processMayStillBeRunning = false;
        ShowNotification(
            "Starting MAS_AIO.cmd.",
            LogStatus.Info,
            $"Arguments: {arguments}");

        try
        {
            var result = await Task.Run(() =>
            {
                var capturedOutput = new List<string>();
                var outputLock = new object();
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \"\"{scriptPath}\" {arguments}\"",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        lock (outputLock)
                        {
                            capturedOutput.Add(args.Data);
                        }

                        Dispatcher.Invoke(() => AppendLog(args.Data, LogStatus.Info));
                    }
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        lock (outputLock)
                        {
                            capturedOutput.Add(args.Data);
                        }

                        Dispatcher.Invoke(() => AppendLog($"[ERR] {args.Data}", LogStatus.Error));
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool timedOut = false;
                if (!process.WaitForExit((int)MasScriptTimeout.TotalMilliseconds) &&
                    !process.HasExited)
                {
                    timedOut = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        timedOut = false;
                    }
                    catch (Win32Exception) when (process.HasExited)
                    {
                        timedOut = false;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                        throw new TimeoutException(
                            "MAS_AIO.cmd exceeded its timeout and could not be stopped.",
                            ex);
                    }
                }

                if (!process.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                {
                    throw new TimeoutException(
                        $"MAS_AIO.cmd did not exit after the stop request (PID {process.Id}).");
                }

                process.WaitForExit();

                string[] outputLines;
                lock (outputLock)
                {
                    outputLines = capturedOutput.ToArray();
                }

                return (TimedOut: timedOut, process.ExitCode, OutputLines: outputLines);
            });

            elapsed.Stop();
            if (result.TimedOut)
            {
                ShowNotification(
                    "MAS_AIO.cmd exceeded the 30-minute limit and was stopped.",
                    LogStatus.Warning,
                    $"Elapsed: {FormatElapsed(elapsed.Elapsed)}; arguments: {arguments}");
                return;
            }

            var (status, summary) = ClassifyMasResult(result.OutputLines, result.ExitCode, arguments);
            ShowNotification(
                summary,
                status,
                $"Exit code: {result.ExitCode}; elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        catch (TimeoutException ex)
        {
            elapsed.Stop();
            processMayStillBeRunning = true;
            ShowNotification(
                "MAS_AIO.cmd timed out and may still be running; other actions are disabled.",
                LogStatus.Error,
                $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            elapsed.Stop();
            ShowNotification(
                "MAS_AIO.cmd could not start or complete. See the Activity Log for details.",
                LogStatus.Error,
                $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        finally
        {
            if (!processMayStillBeRunning)
            {
                if (!MasScriptProvider.TryDeleteTemporaryScript(scriptPath))
                {
                    AppendLog("> Warning: Could not remove the temporary MAS script.", LogStatus.Warning);
                }

                SetBusyState(false);
            }
        }
    }

    private async Task OpenMasConsoleWorkflowAsync(
        string workflow,
        string navigationInstructions,
        string confirmationMessage,
        string selectionDetails)
    {
        if (_isBusy)
        {
            ShowNotification(
                "Another task is already running. Please wait until it finishes.",
                LogStatus.Warning);
            return;
        }

        if (!ConfirmTask("Open native MAS workflow", confirmationMessage))
        {
            ShowNotification($"{workflow} was not started.", LogStatus.Info);
            return;
        }

        string scriptPath;
        try
        {
            scriptPath = MasScriptProvider.CreateTemporaryScript();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ShowNotification(
                "The embedded MAS script could not be prepared. See the Activity Log for details.",
                LogStatus.Error,
                ex.Message);
            return;
        }

        CloseCurrentModal();
        SetBusyState(true);
        var elapsed = Stopwatch.StartNew();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /c \"\"{scriptPath}\" -el -qedit re1 re2\"",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };

            ShowNotification(
                "Opening the native MAS console.",
                LogStatus.Info,
                $"Workflow: {workflow}. {navigationInstructions} " +
                $"Select the requested options again in MAS. WPF selection: {selectionDetails}. " +
                "Console output is shown in its own window, not copied into Activity Log.");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Windows did not return a process for the MAS console.");
            }

            await process.WaitForExitAsync();
            elapsed.Stop();

            if (!_isClosing)
            {
                ShowNotification(
                    "The MAS console closed. Check its window for the operation result.",
                    LogStatus.Info,
                    $"Workflow: {workflow}; exit code: {process.ExitCode}; elapsed: {FormatElapsed(elapsed.Elapsed)}");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            elapsed.Stop();
            if (!_isClosing)
            {
                ShowNotification(
                    "The Windows permission prompt was cancelled; MAS did not start.",
                    LogStatus.Info,
                    ex.Message);
            }
        }
        catch (Win32Exception ex)
        {
            elapsed.Stop();
            if (!_isClosing)
            {
                ShowNotification(
                    "Could not start the native MAS console.",
                    LogStatus.Error,
                    $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
            }
        }
        catch (InvalidOperationException ex)
        {
            elapsed.Stop();
            if (!_isClosing)
            {
                ShowNotification(
                    "The native MAS workflow failed to start or complete.",
                    LogStatus.Error,
                    $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
            }
        }
        finally
        {
            if (!MasScriptProvider.TryDeleteTemporaryScript(scriptPath))
            {
                AppendLog("> Warning: Could not remove the temporary MAS script.", LogStatus.Warning);
            }

            if (!_isClosing)
            {
                SetBusyState(false);
            }
        }
    }

    private static (LogStatus Status, string Message) ClassifyMasResult(
        IEnumerable<string> outputLines,
        int exitCode,
        string arguments)
    {
        string output = AnsiEscapeRegex.Replace(
            string.Join(Environment.NewLine, outputLines),
            string.Empty);

        if (ContainsAny(output,
            "you only have the office dashboard app installed",
            "download and install office from the url below, then try again",
            "download and install office from below url and try again",
            "download and install latest office from below url and try again"))
        {
            return (LogStatus.Warning,
                "A full Office installation is required. Check the Activity Log for the download link.");
        }

        if (HasMasFailureIndicator(output) || ContainsAny(output,
            "some errors were detected",
            "activation failed",
            "failed to activate",
            "unable to activate",
            "could not activate",
            "activation succeeded, but remaining period failed to increase",
            "failed to completely clear"))
        {
            return (LogStatus.Error, "MAS output reports an activation error. Check the Activity Log.");
        }

        if (ContainsAny(output, "successfully uninstalled"))
        {
            return exitCode == 0
                ? (LogStatus.Success, "MAS reports the uninstall completed successfully.")
                : (LogStatus.Warning, $"MAS reports the uninstall succeeded, but exited with code {exitCode}.");
        }

        if (ContainsAny(output, "ohook activation is not installed"))
        {
            return (LogStatus.Info, "Ohook activation is not installed; there was nothing to uninstall.");
        }

        if (ContainsAny(output, "renewal task for lifetime activation is successfully installed"))
        {
            return exitCode == 0
                ? (LogStatus.Success, "KMS renewal task was installed successfully.")
                : (LogStatus.Warning, $"KMS renewal task was installed, but MAS exited with code {exitCode}.");
        }

        if (ContainsAny(output,
            "is already permanently activated",
            "is already activated with kms38"))
        {
            string suffix = exitCode == 0 ? string.Empty : $" (process exit code {exitCode})";
            return (exitCode == 0 ? LogStatus.Info : LogStatus.Warning,
                $"MAS output reports that the product is already permanently activated{suffix}.");
        }

        if (arguments.Contains("/K-", StringComparison.OrdinalIgnoreCase))
        {
            string kmsDurationLine = FindMatchingOutputLine(output, "is successfully activated for");
            if (!string.IsNullOrEmpty(kmsDurationLine))
            {
                string suffix = exitCode == 0 ? string.Empty : $" (process exit code {exitCode})";
                return (exitCode == 0 ? LogStatus.Success : LogStatus.Warning,
                    $"Online KMS: {LimitNotificationMessage(kmsDurationLine)}{suffix}");
            }
        }

        string resultLine = FindMatchingOutputLine(
            output,
            "is successfully activated for",
            "activated using kms4k successfully",
            "activated using zerocid successfully",
            "reset process has been successfully done",
            "is permanently activated with",
            "is permanently activated.",
            "activated successfully",
            "successfully installed",
            "successfully removed kms38 protection",
            "successfully cleared kms cache",
            "cleared kms cache successfully");
        if (!string.IsNullOrEmpty(resultLine))
        {
            string suffix = exitCode == 0 ? string.Empty : $" (process exit code {exitCode})";
            return (exitCode == 0 ? LogStatus.Success : LogStatus.Warning,
                $"MAS: {LimitNotificationMessage(resultLine)}{suffix}");
        }

        return exitCode == 0
            ? (LogStatus.Warning, "MAS finished, but no recognized result message was found. Check the Activity Log.")
            : (LogStatus.Error, $"MAS finished with exit code {exitCode} and no recognized result message.");
    }

    private static bool HasMasFailureIndicator(string output)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedLine = line.Trim();
            if (ContainsAny(trimmedLine, "no errors", "0 errors"))
            {
                continue;
            }

            if (trimmedLine.StartsWith("Failed to open", StringComparison.OrdinalIgnoreCase) &&
                !ContainsAny(trimmedLine, "activation", "license", "product key", "office", "windows"))
            {
                continue;
            }

            if (MasFailureLineRegex.IsMatch(trimmedLine))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FindMatchingOutputLine(string output, params string[] phrases)
    {
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ContainsAny(line, phrases))
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }

    private static string LimitNotificationMessage(string? message)
    {
        const int maxLength = 120;
        if (string.IsNullOrWhiteSpace(message))
        {
            return "No notification message.";
        }

        var trimmedMessage = message.Trim();
        return trimmedMessage.Length <= maxLength
            ? trimmedMessage
            : $"{trimmedMessage[..(maxLength - 3)]}...";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{elapsed.Minutes}m {elapsed.Seconds}s";
        }

        return $"{elapsed.TotalSeconds:0.0}s";
    }

    private void CreateOemPresetOnDesktop(string masArguments)
    {
        try
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string scriptsDir = Path.Combine(desktopDir, "$OEM$", "$$", "Setup", "Scripts");
            Directory.CreateDirectory(scriptsDir);

            string sourceScript = MasScriptProvider.CreateTemporaryScript();
            bool masScriptCopied = File.Exists(sourceScript);
            try
            {
                if (masScriptCopied)
                {
                    File.Copy(sourceScript, Path.Combine(scriptsDir, "MAS_AIO.cmd"), overwrite: true);
                }
            }
            finally
            {
                if (!MasScriptProvider.TryDeleteTemporaryScript(sourceScript))
                {
                    AppendLog("> Warning: Could not remove the temporary MAS script.", LogStatus.Warning);
                }
            }

            if (!masScriptCopied)
            {
                AppendLog("> Warning: The embedded MAS script could not be prepared; SetupComplete.cmd was still generated.", LogStatus.Warning);
            }

            string setupCompletePath = Path.Combine(scriptsDir, "SetupComplete.cmd");
            string scriptContent =
                "@echo off\r\n" +
                "fltmc >nul 2>&1 || exit /b\r\n" +
                $"call \"%~dp0MAS_AIO.cmd\" {masArguments}\r\n" +
                "rd /s /q \"%~dp0\" >nul 2>&1\r\n" +
                "exit /b\r\n";

            File.WriteAllText(setupCompletePath, scriptContent, Encoding.ASCII);
            ShowNotification(
                masScriptCopied
                    ? "$OEM$ preset created on the Desktop."
                    : "$OEM$ preset created, but the embedded MAS script could not be extracted.",
                masScriptCopied ? LogStatus.Success : LogStatus.Warning,
                $"SetupComplete.cmd: {setupCompletePath}; arguments: {masArguments}" +
                (masScriptCopied ? string.Empty : $"; missing script: {sourceScript}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowNotification(
                "Could not create the $OEM$ preset. See the Activity Log for details.",
                LogStatus.Error,
                ex.Message);
        }
    }

    private async void RunMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            throw new InvalidOperationException("Maintenance actions must be initiated by a button.");
        }

        var task = button.Tag?.ToString() switch
        {
            "[2] Dism RestoreHealth" => MaintenanceTask.WindowsImageRepair,
            "[3] SFC Scannow" => MaintenanceTask.SystemFileCheck,
            _ => throw new InvalidOperationException("Unknown maintenance task button.")
        };

        await RunMaintenanceTaskAsync(task);
    }

    private async Task RunMaintenanceTaskAsync(MaintenanceTask task)
    {
        if (_isBusy)
        {
            ShowNotification("Another maintenance task is already running.", LogStatus.Warning);
            return;
        }

        var taskName = task switch
        {
            MaintenanceTask.WindowsImageRepair => "DISM RestoreHealth",
            MaintenanceTask.SystemFileCheck => "SFC Scannow",
            MaintenanceTask.LicenseStatusCheck => "MAS activation status check",
            MaintenanceTask.WmiRepositoryRepair => "WMI repository repair",
            _ => "Maintenance task"
        };

        if ((task is MaintenanceTask.WindowsImageRepair or
             MaintenanceTask.SystemFileCheck or
             MaintenanceTask.WmiRepositoryRepair) &&
            !ConfirmTask(
                "Confirm maintenance task",
                $"{taskName} may take a while and modify system components. Continue?"))
        {
            ShowNotification($"{taskName} cancelled.", LogStatus.Info);
            return;
        }

        var elapsed = Stopwatch.StartNew();
        SetBusyState(true);
        ShowNotification($"Starting {taskName}.", LogStatus.Info);

        try
        {
            var capturedOutput = new List<string>();
            var outputLock = new object();
            var exitCode = await _maintenanceService.RunCommandAsync(
                task,
                line =>
                {
                    lock (outputLock)
                    {
                        capturedOutput.Add(line);
                    }

                    Dispatcher.Invoke(() => AppendLog(line, LogStatus.Info));
                });

            elapsed.Stop();
            if (task == MaintenanceTask.LicenseStatusCheck && exitCode == 0)
            {
                string[] statusOutput;
                lock (outputLock)
                {
                    statusOutput = capturedOutput.ToArray();
                }

                var result = BuildActivationStatusNotification(statusOutput);
                ShowNotification(
                    result.Message,
                    result.Status,
                    $"{result.Details} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
                return;
            }

            var status = exitCode == 0 ? LogStatus.Success : LogStatus.Error;
            var summary = exitCode == 0
                ? $"{taskName} completed."
                : $"{taskName} exited with code {exitCode}.";
            ShowNotification(summary, status, $"Elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        catch (Win32Exception ex)
        {
            elapsed.Stop();
            ShowNotification(
                "Could not start the maintenance task. See the Activity Log for details.",
                LogStatus.Error,
                $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            elapsed.Stop();
            ShowNotification(
                task == MaintenanceTask.LicenseStatusCheck
                    ? "MAS_AIO.cmd is missing or unreadable; its status check could not run."
                    : "The maintenance task could not access a required file.",
                LogStatus.Error,
                $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        catch (InvalidOperationException ex)
        {
            elapsed.Stop();
            ShowNotification(
                "The maintenance task failed. See the Activity Log for details.",
                LogStatus.Error,
                $"{ex.Message} Elapsed: {FormatElapsed(elapsed.Elapsed)}");
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private static (string Message, LogStatus Status, string Details) BuildActivationStatusNotification(
        IEnumerable<string> outputLines)
    {
        var windowsStatuses = new List<string>();
        var officeStatuses = new List<string>();
        string? currentProduct = null;

        foreach (var line in outputLines)
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.Contains("Windows Status", StringComparison.OrdinalIgnoreCase))
            {
                currentProduct = "Windows";
                continue;
            }

            if (trimmedLine.Contains("Office Status", StringComparison.OrdinalIgnoreCase))
            {
                currentProduct = "Office";
                continue;
            }

            const string statusPrefix = "License Status:";
            if (!trimmedLine.StartsWith(statusPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var licenseStatus = trimmedLine[statusPrefix.Length..].Trim();
            if (currentProduct == "Windows")
            {
                windowsStatuses.Add(licenseStatus);
            }
            else if (currentProduct == "Office")
            {
                officeStatuses.Add(licenseStatus);
            }
        }

        int windowsLicensed = CountLicensedProducts(windowsStatuses);
        int officeLicensed = CountLicensedProducts(officeStatuses);
        string windowsSummary = FormatLicenseSummary(windowsStatuses, windowsLicensed);
        string officeSummary = FormatLicenseSummary(officeStatuses, officeLicensed);
        bool allLicensed =
            windowsStatuses.Count > 0 &&
            officeStatuses.Count > 0 &&
            windowsLicensed == windowsStatuses.Count &&
            officeLicensed == officeStatuses.Count;
        var notificationStatus = allLicensed ? LogStatus.Success : LogStatus.Warning;
        string details =
            $"Windows license states: {FormatLicenseStates(windowsStatuses)}; " +
            $"Office license states: {FormatLicenseStates(officeStatuses)}. " +
            "No license data means no licensing record was found; it does not confirm whether the app is installed.";

        return (
            $"Windows: {windowsSummary}; Office: {officeSummary}.",
            notificationStatus,
            details);
    }

    private static int CountLicensedProducts(IEnumerable<string> statuses) =>
        statuses.Count(status => string.Equals(status, "Licensed", StringComparison.OrdinalIgnoreCase));

    private static string FormatLicenseSummary(IReadOnlyCollection<string> statuses, int licensedCount)
    {
        if (statuses.Count == 0)
        {
            return "no license data";
        }

        if (licensedCount == statuses.Count)
        {
            return $"Licensed ({licensedCount}/{statuses.Count})";
        }

        return licensedCount == 0
            ? $"Not licensed (0/{statuses.Count})"
            : $"Partly licensed ({licensedCount}/{statuses.Count})";
    }

    private static string FormatLicenseStates(IEnumerable<string> statuses)
    {
        var statusCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses)
        {
            statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
        }

        return statusCounts.Count == 0
            ? "none detected"
            : string.Join(", ", statusCounts.Select(item => $"{item.Key} ({item.Value})"));
    }

    private void ShowNotification(string? message, LogStatus status, string? activityDetails = null)
    {
        var displayMessage = LimitNotificationMessage(message);
        _notificationTimer.Stop();
        _notificationGeneration++;
        NotificationBanner.BeginAnimation(UIElement.OpacityProperty, null);
        var transform = (TranslateTransform)NotificationBanner.RenderTransform;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        NotificationBanner.Opacity = 1;
        transform.X = 0;

        var (background, border, foreground) = status switch
        {
            LogStatus.Success => ("#143322", "#4ADE80", SuccessLogBrush),
            LogStatus.Warning => ("#382B12", "#FBBF24", WarningLogBrush),
            LogStatus.Error => ("#3B161A", "#F87171", ErrorLogBrush),
            _ => ("#251C33", "#C65AE8", InfoLogBrush)
        };

        NotificationBanner.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(background));
        NotificationBanner.BorderBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(border));
        TxtNotificationMessage.Foreground = foreground;
        TxtNotificationMessage.Text = displayMessage;
        NotificationBanner.Visibility = Visibility.Visible;

        AppendLog($"> [NOTIFY] {displayMessage}", status);
        if (!string.IsNullOrWhiteSpace(activityDetails))
        {
            AppendLog($"> [DETAIL] {activityDetails}", status);
        }

        _notificationTimer.Start();
    }

    private void DismissNotification_Click(object sender, RoutedEventArgs e)
    {
        _notificationTimer.Stop();
        HideNotificationWithAnimation();
    }

    private void HideNotificationWithAnimation()
    {
        if (NotificationBanner.Visibility != Visibility.Visible)
        {
            return;
        }

        int generation = _notificationGeneration;
        var duration = TimeSpan.FromMilliseconds(240);
        var fade = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        var slide = new DoubleAnimation(0, 48, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        fade.Completed += (_, _) =>
        {
            if (generation != _notificationGeneration)
            {
                return;
            }

            NotificationBanner.Visibility = Visibility.Collapsed;
            NotificationBanner.BeginAnimation(UIElement.OpacityProperty, null);
            var transform = (TranslateTransform)NotificationBanner.RenderTransform;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            NotificationBanner.Opacity = 1;
            transform.X = 0;
        };

        NotificationBanner.BeginAnimation(UIElement.OpacityProperty, fade);
        ((TranslateTransform)NotificationBanner.RenderTransform)
            .BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void DownloadMedia_Click(object sender, RoutedEventArgs e)
    {
        var optionName = GetOptionName(sender);
        ShowNotification(
            "This opens the download page; no install command is run.",
            LogStatus.Info,
            $"{_activeMenuName} -> {optionName}: {DownloadMediaUrl}");
        OpenWebsite(DownloadMediaUrl);
    }

    private void OpenGuide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string address ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Guide buttons must provide an absolute HTTPS URL in Tag.");
        }

        var guideName = button.Content?.ToString() ?? "Guide";
        ShowNotification(
            "This opens a guide page; no system command is run.",
            LogStatus.Info,
            $"{_activeMenuName} -> {guideName}: {uri.AbsoluteUri}");
        OpenWebsite(uri.AbsoluteUri);
    }

    private void ApplyKmsServer_Click(object sender, RoutedEventArgs e)
    {
        var server = TxtKmsServer.Text.Trim();
        if (string.IsNullOrEmpty(server))
        {
            TxtKmsServerStatus.Text = "Enter a Server:Port value before applying.";
            TxtKmsServerStatus.Visibility = Visibility.Visible;
            ShowNotification("Enter a Server:Port value before applying.", LogStatus.Warning);
            return;
        }

        _savedKmsServer = server;
        _hasSavedKmsServer = true;
        TxtKmsServerStatus.Text = $"Saved for this session: {_savedKmsServer}";
        TxtKmsServerStatus.Visibility = Visibility.Visible;
        ShowNotification(
            "KMS server saved for this session.",
            LogStatus.Success,
            _savedKmsServer);
    }

    private void TxtKmsServer_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_hasSavedKmsServer)
        {
            return;
        }

        var currentValue = TxtKmsServer.Text.Trim();
        TxtKmsServerStatus.Text = string.Equals(currentValue, _savedKmsServer, StringComparison.Ordinal)
            ? $"Saved for this session: {_savedKmsServer}"
            : "Unsaved changes";
        TxtKmsServerStatus.Visibility = Visibility.Visible;
    }

    private void BackModal_Click(object sender, RoutedEventArgs e)
    {
        switch (_currentModalStep)
        {
            case ModalStep.OfficeStep2:
            case ModalStep.OfficeChannelPicker:
                ShowOfficeStep(ModalStep.OfficeStep1);
                break;
            case ModalStep.OfficeStep3:
                ShowOfficeStep(_officeAppsBackStep);
                break;
            case ModalStep.ExtrasStep2:
                ShowExtrasStep(ModalStep.ExtrasStep1);
                break;
        }
    }

    private void OfficeAdvance_Click(object sender, RoutedEventArgs e)
    {
        _officeOperation = GetOptionName(sender);
        AppendSelectionLog(_activeMenuName, GetButtonLabel(sender));
        ShowOfficeStep(ModalStep.OfficeStep2);
    }

    private void OfficeAppsStep_Click(object sender, RoutedEventArgs e)
    {
        _officeOperation = GetOptionName(sender);
        _officeAppsBackStep = ModalStep.OfficeStep1;
        AppendSelectionLog(_activeMenuName, GetButtonLabel(sender));
        ShowOfficeStep(ModalStep.OfficeStep3);
    }

    private void OfficeChannelPicker_Click(object sender, RoutedEventArgs e)
    {
        AppendSelectionLog(_activeMenuName, GetButtonLabel(sender));
        ShowOfficeStep(ModalStep.OfficeChannelPicker);
    }

    private void OfficeCategory_Click(object sender, RoutedEventArgs e)
    {
        _selectedOfficeCategory = GetOptionName(sender);
        TxtOfficeCategory.Text = _selectedOfficeCategory;
        AppendSelectionLog(_activeMenuName, $"Category: {_selectedOfficeCategory}");
    }

    private void OfficeTarget_Click(object sender, RoutedEventArgs e)
    {
        _selectedOfficeTarget = GetOptionName(sender);
        _officeAppsBackStep = ModalStep.OfficeStep2;
        AppendSelectionLog(
            _activeMenuName,
            $"Target: {_selectedOfficeCategory} -> {_selectedOfficeTarget}");
        TxtOfficeStep3Summary.Text =
            $"{_officeOperation}: {_selectedOfficeCategory} > {_selectedOfficeTarget}";
        ShowOfficeStep(ModalStep.OfficeStep3);
    }

    private async void OfficeChannel_Click(object sender, RoutedEventArgs e)
    {
        var channel = GetOptionName(sender);
        await OpenMasConsoleWorkflowAsync(
            "Change Office Update Channel",
            "In MAS, choose Main Menu > [7] Change Office Edition > [5] Change Office Update Channel, then select the channel.",
            "Open MAS's native Office update channel workflow? The channel selected here is not passed to MAS; " +
            "select it again in the console.",
            channel);
    }

    private void ShowExtrasPresets_Click(object sender, RoutedEventArgs e)
    {
        AppendSimulationLog(_activeMenuName, "[1] Extract $OEM$ Folder");
        ShowExtrasStep(ModalStep.ExtrasStep2);
    }

    private async void ApplyOfficeApps_Click(object sender, RoutedEventArgs e)
    {
        var selectedApps = new List<string>();
        AddSelectedApp(selectedApps, ToggleAccess);
        AddSelectedApp(selectedApps, ToggleExcel);
        AddSelectedApp(selectedApps, ToggleOneNote);
        AddSelectedApp(selectedApps, ToggleOutlookClassic);
        AddSelectedApp(selectedApps, TogglePowerPoint);
        AddSelectedApp(selectedApps, ToggleProject);
        AddSelectedApp(selectedApps, TogglePublisher);
        AddSelectedApp(selectedApps, ToggleVisio);
        AddSelectedApp(selectedApps, ToggleWord);
        AddSelectedApp(selectedApps, ToggleSkypeForBusiness);
        AddSelectedApp(selectedApps, ToggleOutlookNew);
        AddSelectedApp(selectedApps, ToggleOneDrive);
        AddSelectedApp(selectedApps, ToggleTeams);

        var selection = selectedApps.Count == 0 ? "None" : string.Join(", ", selectedApps);
        string workflow = _officeOperation == "Add/Remove apps"
            ? "Edit Office apps"
            : $"{_officeOperation} Office edition";
        string navigationInstructions = _officeOperation == "Add/Remove apps"
            ? "In MAS, choose Main Menu > [7] Change Office Edition > [4] Add/Remove apps."
            : "In MAS, choose Main Menu > [7] Change Office Edition, then repeat the operation, category, and edition selections.";
        await OpenMasConsoleWorkflowAsync(
            workflow,
            navigationInstructions,
            $"Open MAS's native Office workflow? Your selections are not passed to MAS. " +
            "Choose them again in its console before confirming any changes.",
            $"Operation: {_officeOperation}; category: {_selectedOfficeCategory}; " +
            $"edition: {_selectedOfficeTarget}; selected apps: {selection}");
    }

    private void OfficeAppToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.ToggleSwitch toggle || toggle.Tag is not string appName)
        {
            throw new InvalidOperationException("Office app switches must provide an app name in Tag.");
        }

        AppendSelectionLog(
            _activeMenuName,
            $"App: {appName} {(toggle.IsChecked == true ? "On" : "Off")}");
    }

    private void OnlineKmsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.ToggleSwitch toggle || toggle.Tag is not string optionName)
        {
            throw new InvalidOperationException("Online KMS switches must provide an option name in Tag.");
        }

        AppendSimulationLog(
            _activeMenuName,
            $"{optionName}: {(toggle.IsChecked == true ? "On" : "Off")}");
    }

    private void TSforgeActivationMethod_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TSforgeActivationMethod.SelectedItem is not System.Windows.Controls.ComboBoxItem selectedItem)
        {
            return;
        }

        AppendSimulationLog(
            _activeMenuName,
            $"Activation Method: {selectedItem.Content}");
    }

    private void ShowOfficeStep(ModalStep step)
    {
        OfficeStep1.Visibility = step == ModalStep.OfficeStep1 ? Visibility.Visible : Visibility.Collapsed;
        OfficeStep2.Visibility = step == ModalStep.OfficeStep2 ? Visibility.Visible : Visibility.Collapsed;
        OfficeStep3.Visibility = step == ModalStep.OfficeStep3 ? Visibility.Visible : Visibility.Collapsed;
        OfficeChannelPicker.Visibility = step == ModalStep.OfficeChannelPicker ? Visibility.Visible : Visibility.Collapsed;
        _currentModalStep = step;

        var breadcrumb = step switch
        {
            ModalStep.OfficeStep1 => string.Empty,
            ModalStep.OfficeStep2 => $"{_officeOperation} > Select Suite / App Category",
            ModalStep.OfficeStep3 => $"{_officeOperation} > Select Apps",
            ModalStep.OfficeChannelPicker => "Change Office Update Channel",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Unsupported Office modal step.")
        };
        SetModalBreadcrumb(breadcrumb, step != ModalStep.OfficeStep1);
    }

    private void ShowExtrasStep(ModalStep step)
    {
        ExtrasStep1.Visibility = step == ModalStep.ExtrasStep1 ? Visibility.Visible : Visibility.Collapsed;
        ExtrasStep2.Visibility = step == ModalStep.ExtrasStep2 ? Visibility.Visible : Visibility.Collapsed;
        _currentModalStep = step;
        SetModalBreadcrumb(
            step == ModalStep.ExtrasStep2 ? "$OEM$ Presets" : string.Empty,
            step == ModalStep.ExtrasStep2);
    }

    private void SetModalBreadcrumb(string breadcrumb, bool showBack)
    {
        TxtModalBreadcrumb.Text = breadcrumb;
        TxtModalBreadcrumb.Visibility = string.IsNullOrEmpty(breadcrumb)
            ? Visibility.Collapsed
            : Visibility.Visible;
        BtnModalBack.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetOptionName(object sender)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string optionName)
        {
            throw new InvalidOperationException("Modal action buttons must provide an option name in Tag.");
        }

        return optionName;
    }

    private static string GetButtonLabel(object sender)
    {
        if (sender is not System.Windows.Controls.Button button || button.Content is not string label)
        {
            throw new InvalidOperationException("Modal navigation buttons must have a text label.");
        }

        return label;
    }

    private static void AddSelectedApp(
        ICollection<string> selectedApps,
        Wpf.Ui.Controls.ToggleSwitch toggle)
    {
        if (toggle.IsChecked == true && toggle.Tag is string appName)
        {
            selectedApps.Add(appName);
        }
    }

    private void SimulateAction(string menuName, string optionName)
    {
        AppendSimulationLog(menuName, $"Selected: {optionName}");
        CloseCurrentModal();
    }

    private void AppendSelectionLog(string menuName, string selection) =>
        AppendLog($"> [SELECTION] {menuName}: {selection}", LogStatus.Info);

    private void AppendSimulationLog(string menuName, string optionName)
    {
        ShowNotification(
            $"{menuName} is UI-only; no command was run.",
            LogStatus.Warning,
            $"[UI Simulation] {menuName} -> {optionName}");
    }

    private void LogAction(string action)
    {
        AppendSimulationLog(action, "Selected");
    }

    private void AppendLog(string message, LogStatus status)
    {
        _logHistory.Enqueue(new LogEntry(message, status));
        while (_logHistory.Count > MaxFullLogParagraphs)
        {
            _logHistory.Dequeue();
        }

        var foreground = GetLogForeground(status);
        TxtConsole.Document.Blocks.Add(CreateLogParagraph(message, foreground));
        while (TxtConsole.Document.Blocks.Count > MaxActivityLogParagraphs)
        {
            TxtConsole.Document.Blocks.Remove(TxtConsole.Document.Blocks.FirstBlock);
        }
        TxtConsole.ScrollToEnd();

        if (_fullLogTextBox is not null)
        {
            _fullLogTextBox.Document.Blocks.Add(CreateLogParagraph(message, foreground));
            while (_fullLogTextBox.Document.Blocks.Count > MaxFullLogParagraphs)
            {
                _fullLogTextBox.Document.Blocks.Remove(_fullLogTextBox.Document.Blocks.FirstBlock);
            }

            _fullLogTextBox.ScrollToEnd();
        }
    }

    private static SolidColorBrush GetLogForeground(LogStatus status) => status switch
    {
        LogStatus.Success => SuccessLogBrush,
        LogStatus.Warning => WarningLogBrush,
        LogStatus.Error => ErrorLogBrush,
        _ => InfoLogBrush
    };

    private static Paragraph CreateLogParagraph(string message, SolidColorBrush foreground) =>
        new(new Run(message) { Foreground = foreground })
        {
            Margin = new Thickness(0)
        };

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static void OpenWebsite(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Unable to open the link: {ex.Message}",
                "MAS",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
