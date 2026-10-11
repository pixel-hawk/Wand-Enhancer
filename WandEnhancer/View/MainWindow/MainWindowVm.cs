using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using WandEnhancer.Core;
using WandEnhancer.Core.Services;
using WandEnhancer.Models;
using WandEnhancer.ReactiveUICore;
using WandEnhancer.Utils;
using WandEnhancer.View.Popups;
using Application = System.Windows.Application;

namespace WandEnhancer.View.MainWindow
{
    public class MainWindowVm : ObservableObject
    {
        private const string LogExportFilter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";

        private readonly IShellView _shell;
        private readonly IFileDialogs _dialogs;
        public ObservableCollection<LogEntry> LogList { get; } = new ObservableCollection<LogEntry>();
        private WeModConfig _weModConfig;

        public WeModConfig WeModInfo
        {
            get => _weModConfig;
            set => SetProperty(ref _weModConfig, value);
        }

        private string _folderPathInput = string.Empty;

        /// <summary>Editable folder path text. Applied on Enter / Browse.</summary>
        public string FolderPathInput
        {
            get => _folderPathInput;
            set => SetProperty(ref _folderPathInput, value);
        }

        private void UseInstall(WeModConfig config)
        {
            WeModInfo = config;
            if (config == null)
            {
                return;
            }

            FolderPathInput = config.RootDirectory;

            Log(LocalizationManager.Format("log_install_found", config, config.ExecutableName), ELogType.Success);
            AlreadyPatched = Enhancer.IsPatched(config.RootDirectory);
            CanRestore = Enhancer.HasBackup(config.RootDirectory);
            IsPatchEnabled = !AlreadyPatched;

            Log(LocalizationManager.Get(AlreadyPatched ? "log_already_patched" : "log_ready"),
                AlreadyPatched ? ELogType.Warn : ELogType.Info);
        }

        private bool _isPatchEnabled;

        public bool IsPatchEnabled
        {
            get => _isPatchEnabled;
            set => SetProperty(ref _isPatchEnabled, value);
        }

        private bool _alreadyPatched;

        public bool AlreadyPatched
        {
            get => _alreadyPatched;
            set => SetProperty(ref _alreadyPatched, value);
        }

        private bool _canRestore;

        public bool CanRestore
        {
            get => _canRestore;
            set => SetProperty(ref _canRestore, value);
        }

        private bool _isBusy;

        /// <summary>True while a patch or restore runs.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsIdle));
                }
            }
        }

        /// <summary>Bound by buttons that must not be clickable mid-run.</summary>
        public bool IsIdle => !_isBusy;

        public RelayCommand SetFolderPathCommand { get; }
        public RelayCommand ApplyFolderPathCommand { get; }
        public RelayCommand ApplyPatchCommand { get; }
        public RelayCommand RestoreBackupCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand CopyLogsCommand { get; }
        public RelayCommand ExportLogsCommand { get; }

        private void OnFolderPathSelection(object obj)
        {
            string selectedPath = _dialogs.PickFolder(
                LocalizationManager.Get("dialog_pick_install"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA"));
            if (selectedPath == null)
            {
                return;
            }

            TryApplyFolderPath(selectedPath);
        }

        private void OnApplyFolderPathFromInput(object obj)
        {
            string rawPath = obj as string ?? FolderPathInput;
            TryApplyFolderPath(rawPath);
        }

        /// <summary>Validates a typed or browsed path and applies it when valid.</summary>
        /// <returns>True when the path was accepted.</returns>
        private bool TryApplyFolderPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                Log(LocalizationManager.Get("log_no_directory"), ELogType.Warn);
                return false;
            }

            string candidate = rawPath.Trim().Trim('"').Trim();
            try
            {
                candidate = Environment.ExpandEnvironmentVariables(candidate);
            }
            catch
            {
                // Keep the trimmed candidate when expansion fails.
            }

            try
            {
                // Allow pasting the exe itself: use its directory.
                if (File.Exists(candidate))
                {
                    candidate = Path.GetDirectoryName(candidate);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                // Fall through to invalid-directory handling below.
            }

            WeModConfig info = null;
            try
            {
                info = WeModInstalls.CheckWeModPath(candidate);
                if (info == null && Directory.Exists(candidate))
                {
                    // Also accept a parent install folder (e.g. %LOCALAPPDATA%/Wand)
                    // by resolving its latest app-* child.
                    info = WeModInstalls.FindLatestWeMod(candidate);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                info = null;
            }

            if (info == null)
            {
                string displayName;
                try
                {
                    displayName = Path.GetFileName(candidate);
                    if (string.IsNullOrEmpty(displayName))
                    {
                        displayName = candidate;
                    }
                }
                catch
                {
                    displayName = candidate;
                }

                Log(LocalizationManager.Format("log_invalid_directory", displayName), ELogType.Error);
                // Revert the input box to the last valid install.
                FolderPathInput = WeModInfo?.RootDirectory ?? string.Empty;
                return false;
            }

            UseInstall(info);
            return true;
        }

        // Runs off the UI thread due to heavy file IO.
        private async void OnBackupRestoring(object param)
        {
            if (WeModInfo == null)
            {
                Log(LocalizationManager.Get("log_no_directory"), ELogType.Warn);
                return;
            }

            IsBusy = true;
            bool restored = await Task.Run(() =>
            {
                try
                {
                    new Enhancer(WeModInfo, Log).Restore();
                    return true;
                }
                catch (Exception e)
                {
                    Log(LocalizationManager.Format("log_restore_failed", e.Message), ELogType.Error);
                    return false;
                }
            });

            IsBusy = false;
            if (restored)
            {
                AlreadyPatched = false;
                CanRestore = false;
                IsPatchEnabled = true;
            }
        }

        private void OnPatching(object param)
        {
            if (WeModInfo == null)
            {
                Log(LocalizationManager.Get("log_no_directory"), ELogType.Warn);
                return;
            }

            _shell.OpenPopup(new PatchVectorsPopup(async config =>
            {
                _shell.ClosePopup();
                IsPatchEnabled = false;
                IsBusy = true;
                await Task.Run(() =>
                {
                    try
                    {
                        new Enhancer(WeModInfo, Log, config).Patch();
                        AlreadyPatched = true;
                        CanRestore = true;
                    }
                    catch (Exception e)
                    {
                        Log(LocalizationManager.Format("log_patch_failed", e.Message), ELogType.Error);
                        IsPatchEnabled = true;
                    }
                });
                IsBusy = false;
            }), LocalizationManager.Get("pv_popup_title"));
        }

        private void Log(string message, ELogType logType)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var entry = new LogEntry
                {
                    LogType = logType,
                    Message = $"[{logType.ToString().ToUpper()}] {message}"
                };
                LogList.Add(entry);
                _shell.ScrollLogIntoView(entry);
                // Force CanExecute re-evaluation when appending log entries.
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            });
        }

        private void OnOpenSettings(object param)
        {
            _shell.OpenPopup(new SettingsPopup(), LocalizationManager.Get("settings_title"));
        }

        private string BuildLogReport()
        {
            var builder = new StringBuilder();
            foreach (var entry in LogList)
            {
                builder.AppendLine(entry.Message);
            }
            return builder.ToString();
        }

        private void OnCopyLogs(object param)
        {
            try
            {
                System.Windows.Clipboard.SetText(BuildLogReport());
                Log(LocalizationManager.Get("log_copied"), ELogType.Success);
            }
            catch (Exception e)
            {
                Log(LocalizationManager.Format("log_copy_failed", e.Message), ELogType.Error);
            }
        }

        private void OnExportLogs(object param)
        {
            string path = _dialogs.PickSaveFile(
                LogExportFilter,
                $"wand-enhancer-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (path == null)
            {
                return;
            }

            try
            {
                File.WriteAllText(path, BuildLogReport());
                Log(LocalizationManager.Format("log_exported", path), ELogType.Success);
            }
            catch (Exception e)
            {
                Log(LocalizationManager.Format("log_export_failed", e.Message), ELogType.Error);
            }
        }

        private bool HasLogs(object param) => LogList.Count > 0;

        /// <summary>Displays the repository URL if the browser fails to open.</summary>
        public void ReportRepositoryLinkFailure(string url)
        {
            Log(LocalizationManager.Format("log_open_link_failed", url), ELogType.Warn);
        }

        public MainWindowVm(IShellView shell, IFileDialogs dialogs)
        {
            _shell = shell;
            _dialogs = dialogs;
            SetFolderPathCommand = new RelayCommand(OnFolderPathSelection);
            ApplyFolderPathCommand = new RelayCommand(OnApplyFolderPathFromInput);
            ApplyPatchCommand = new RelayCommand(OnPatching);
            RestoreBackupCommand = new RelayCommand(OnBackupRestoring);
            OpenSettingsCommand = new RelayCommand(OnOpenSettings);
            CopyLogsCommand = new RelayCommand(OnCopyLogs, HasLogs);
            ExportLogsCommand = new RelayCommand(OnExportLogs, HasLogs);

            UseInstall(WeModInstalls.FindWeMod());
            if (WeModInfo == null)
            {
                Log(LocalizationManager.Get("log_install_not_found"), ELogType.Error);
            }

            foreach (var entry in Program.StartupLog)
            {
                Log(entry.Key, entry.Value);
            }
            Program.StartupLog.Clear();

#if ENABLE_UPDATE_NOTIFICATIONS
            ShowUpdateCommand = new RelayCommand(OnShowUpdate);
            UpdateNotifier.CheckInBackground(OnUpdateFound, OnNotificationClick);
#endif
        }

#if ENABLE_UPDATE_NOTIFICATIONS
        private UpdateNotifier.UpdateRelease _availableUpdate;
        private bool _isUpdateAvailable;

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            private set => SetProperty(ref _isUpdateAvailable, value);
        }

        public RelayCommand ShowUpdateCommand { get; }

        private void OnShowUpdate(object param)
        {
            if (_availableUpdate != null)
                OpenUpdatePopup(_availableUpdate);
        }

        private void OnUpdateFound(UpdateNotifier.UpdateRelease update)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _availableUpdate = update;
                IsUpdateAvailable = true;
            });
        }

        private void OnNotificationClick(UpdateNotifier.UpdateRelease update)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = Application.Current.MainWindow;
                if (window != null)
                {
                    window.Topmost = true;
                    window.Activate();
                    window.Topmost = false;
                }

                OpenUpdatePopup(update);
            });
        }

        private void OpenUpdatePopup(UpdateNotifier.UpdateRelease update)
        {
            _shell.OpenPopup(
                new UpdatePopup(Constants.Version.ToString(), update.Version, update.Notes,
                    update.Url, UpdateNotifier.GetFullChangelogAsync),
                LocalizationManager.Get("up_popup_title"));
        }
#endif
    }
}
