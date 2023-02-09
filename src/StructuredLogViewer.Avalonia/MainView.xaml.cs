﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Microsoft.Build.Logging.StructuredLogger;
using StructuredLogViewer.Avalonia.Controls;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Linq;
using Avalonia.Controls.Presenters;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using System.Collections;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Storage.FileIO;
using Avalonia.VisualTree;

namespace StructuredLogViewer.Avalonia
{
    public class MainView : UserControl
    {
        private IStorageFile logFile;
        private string projectFilePath;
        private BuildControl currentBuild;

        private const string ClipboardFileFormat = "FileDrop";
        public const string DefaultTitle = "MSBuild Structured Log Viewer";

        private ContentPresenter mainContent;
        private MenuItem RecentProjectsMenu;
        private MenuItem RecentLogsMenu;
        private MenuItem ReloadMenu;
        private MenuItem SaveAsMenu;
        private Separator RecentItemsSeparator;
        private MenuItem startPage;
        private MenuItem Build;
        private MenuItem Rebuild;
        private MenuItem Open;
        private MenuItem SetMSBuild;
        private MenuItem HelpLink;
        private MenuItem Exit;

        public MainView()
        {
            InitializeComponent();

            Loaded += MainWindow_Loaded;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            this.RegisterControl(out mainContent, nameof(mainContent));
            this.RegisterControl(out startPage, nameof(startPage));
            this.RegisterControl(out RecentProjectsMenu, nameof(RecentProjectsMenu));
            this.RegisterControl(out RecentLogsMenu, nameof(RecentLogsMenu));
            this.RegisterControl(out ReloadMenu, nameof(ReloadMenu));
            this.RegisterControl(out SaveAsMenu, nameof(SaveAsMenu));
            this.RegisterControl(out RecentItemsSeparator, nameof(RecentItemsSeparator));
            this.RegisterControl(out Build, nameof(Build));
            this.RegisterControl(out Rebuild, nameof(Rebuild));
            this.RegisterControl(out Open, nameof(Open));
            this.RegisterControl(out SetMSBuild, nameof(SetMSBuild));
            this.RegisterControl(out HelpLink, nameof(HelpLink));
            this.RegisterControl(out Exit, nameof(Exit));

            this.KeyUp += Window_KeyUp;

            startPage.Click += StartPage_Click;
            Build.Click += Build_Click;
            Rebuild.Click += Rebuild_Click;
            Open.Click += Open_Click;
            ReloadMenu.Click += Reload_Click;
            SaveAsMenu.Click += SaveAs_Click;
            SetMSBuild.Click += SetMSBuild_Click;
            HelpLink.Click += HelpLink_Click;
            Exit.Click += Exit_Click;
        }

        private async Task<bool> TryOpenFromClipboard()
        {
            var text = await Application.Current.Clipboard.GetTextAsync();
            if (string.IsNullOrEmpty(text) || text.Length > 260)
            {
                return false;
            }

            text = text.TrimStart('"').TrimEnd('"');

            // only open a file from clipboard if it's not listed in the recent files
            var recentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            recentFiles.UnionWith(SettingsService.GetRecentLogFiles());
            recentFiles.UnionWith(SettingsService.GetRecentProjects());

            // TODO: potentially dangerous to use BclStorageFile here, as clipboard is available on Browser as well, but file system is not.
            if (!recentFiles.Contains(text))
            {
                var fileFromClipboard = await TopLevel.GetTopLevel(this)!.StorageProvider.TryGetFileFromPath(text);
                if (fileFromClipboard is not null)
                {
                    return OpenFile(fileFromClipboard);
                }
            }

            return false;
        }

        private void DisplayWelcomeScreen(string message = "")
        {
            this.projectFilePath = null;
            this.logFile = null;
            this.currentBuild = null;
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.Title = DefaultTitle;
            }

            var welcomeScreen = new WelcomeScreen();
            welcomeScreen.Message = message;
            SetContent(welcomeScreen);
            welcomeScreen.RecentLogSelected += log => Dispatcher.UIThread.Post(() => OpenLogFile(log));
            welcomeScreen.RecentProjectSelected += project => Dispatcher.UIThread.Post(() => BuildProject(project));
            welcomeScreen.OpenProjectRequested += async () => await OpenProjectOrSolution();
            welcomeScreen.OpenLogFileRequested += async () => await OpenLogFile();
            UpdateRecentItemsMenu();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Exception initException = null;
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && await HandleArguments(args))
                {
                    return;
                }

                if (await TryOpenFromClipboard())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                initException = ex;
            }

            try
            {
                DisplayWelcomeScreen();

                // only check for updates if there were no command-line arguments and debugger not attached
                if (Debugger.IsAttached || SettingsService.DisableUpdates)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                initException = ex;
            }

            if (initException is not null
                && mainContent.Content is WelcomeScreen welcomeScreen)
            {
                var text = initException.ToString();
                if (text.Contains("Update.exe not found"))
                {
                    text = "Update.exe not found; app will not update.";
                }

                welcomeScreen.Message = text;
            }
        }

        private async Task<bool> HandleArguments(string[] args)
        {
            if (args.Length > 2)
            {
                DisplayWelcomeScreen("Structured Log Viewer can only accept a single command-line argument: a full path to an existing log file or MSBuild project/solution.");
                return true;
            }

            var argument = args[1];
            if (argument.StartsWith("--"))
            {
                // we don't do anything about the potential "--squirrel-firstrun" argument
                return false;
            }

            var filePath = args[1];
            if (!File.Exists(filePath))
            {
                DisplayWelcomeScreen($"File {filePath} not found.");
                return true;
            }

            // If file was opened from the arguments, it's safe to assume, we have a file system available.
            var fileFromArgs = await TopLevel.GetTopLevel(this)!.StorageProvider.TryGetFileFromPath(filePath);
            if (fileFromArgs is not null && OpenFile(fileFromArgs))
            {
                return true;
            }

            DisplayWelcomeScreen($"File extension not supported: {filePath}");
            return true;
        }

        public bool OpenFile(IStorageFile file)
        {
            if (file.CanOpenRead)
            {
                return false;
            }

            var filePath = file.Path.ToString(); // might be absolute or relative at this point.

            if (filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".buildlog", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase))
            {
                OpenLogFile(file);
                return true;
            }

            if (file.Path is { IsAbsoluteUri: true, Scheme: "file" }
                && (filePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith("proj", StringComparison.OrdinalIgnoreCase)))
            {
                BuildProject(file.Path.LocalPath);
                return true;
            }

            return false;
        }

        private void UpdateRecentItemsMenu(WelcomeScreen welcomeScreen = null)
        {
            welcomeScreen = welcomeScreen ?? new WelcomeScreen();
            if (welcomeScreen.ShowRecentProjects)
            {
                (RecentProjectsMenu.Items as IList)?.Clear();
                RecentProjectsMenu.IsVisible = true;
                RecentItemsSeparator.IsVisible = true;
                foreach (var recentProjectFile in welcomeScreen.RecentProjects)
                {
                    var menuItem = new MenuItem { Header = recentProjectFile };
                    menuItem.Click += RecentProjectClick;
                    (RecentProjectsMenu.Items as IList)?.Add(menuItem);
                }
            }

            if (welcomeScreen.ShowRecentLogs)
            {
                (RecentLogsMenu.Items as IList)?.Clear();
                RecentLogsMenu.IsVisible = true;
                RecentItemsSeparator.IsVisible = true;
                foreach (var recentLog in welcomeScreen.RecentLogs)
                {
                    var menuItem = new MenuItem { Header = recentLog };
                    menuItem.Click += RecentLogFileClick;
                    (RecentLogsMenu.Items as IList)?.Add(menuItem);
                }
            }
        }

        private BuildControl CurrentBuildControl => mainContent.Content as BuildControl;

        private void SetContent(object content)
        {
            mainContent.Content = content;
            if (content == null)
            {
                logFile = null;
                projectFilePath = null;
                currentBuild = null;
            }

            if (content is BuildControl)
            {
                ReloadMenu.IsVisible = logFile != null;
                SaveAsMenu.IsVisible = true;
            }
            else
            {
                ReloadMenu.IsVisible = false;
                SaveAsMenu.IsVisible = false;
            }
        }

        private void RecentProjectClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            BuildProject(Convert.ToString(menuItem.Header));
        }

        private void RecentLogFileClick(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            OpenLogFile(Convert.ToString(menuItem.Header));
        }

        private async void OpenLogFile(string fileBookmark)
        {
            var file = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFileBookmarkAsync(fileBookmark);
            if (file is not null)
            {
                OpenLogFile(file, fileBookmark);
            }
        }

        private async void OpenLogFile(IStorageFile file, string existingBookmark = null)
        {
            DisplayBuild(null);
            this.logFile = file;

            if (existingBookmark is not null)
            {
                SettingsService.AddRecentLogFile(existingBookmark);
            }
            else
            {
                var bookmark = await file.SaveBookmarkAsync();
                if (bookmark is not null)
                {
                    SettingsService.AddRecentLogFile(bookmark);
                }
            }

            var filePath = file.Path;

            UpdateRecentItemsMenu();
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.Title = filePath + " - " + DefaultTitle;
            }

            var progress = new BuildProgress() { IsIndeterminate = true };
            progress.ProgressText = "Opening " + filePath + "...";
            SetContent(progress);

            var (build, shouldAnalyze) = await ReadBuildFromFilePath(file);
            if (build == null)
            {
                build = GetErrorBuild(filePath?.ToString(), "");
                shouldAnalyze = false;
            }

            if (shouldAnalyze)
            {
                progress.ProgressText = "Analyzing " + filePath + "...";
                await QueueAnalyzeBuild(build);
            }

            progress.ProgressText = "Rendering tree...";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); // let the progress message be rendered before we block the UI again

            DisplayBuild(build);
        }

        private static async Task<(Build build, bool shouldAnalyze)> ReadBuildFromFilePath(IStorageFile file)
        {
            if (file.Path is { IsAbsoluteUri: true, Scheme: "file" } uri)
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        return (Serialization.Read(uri.LocalPath), true);
                    }
                    catch (Exception ex)
                    {
                        ex = ExceptionHandler.Unwrap(ex);
                        return (GetErrorBuild(uri.LocalPath, ex.ToString()), false);
                    }
                });
            }

            if (file.CanOpenRead)
            {
                // Use intermediate memory stream, as some platforms don't support sync access to the file system directly.
                using var memoryStream = new MemoryStream();
                await using (var stream = await file.OpenReadAsync())
                {
                    await stream.CopyToAsync(memoryStream);
                    await stream.FlushAsync();
                }

                memoryStream.Seek(0, SeekOrigin.Begin);
                var build = Serialization.ReadBinLog(memoryStream);
                return (build, build.Children.Count == 1 && build.Children.FirstOrDefault() is Error);
            }

            return (GetErrorBuild(file.Path?.ToString() ?? "(null)", "Unable to open file for read"), false);
        }

        private static Build GetErrorBuild(string filePath, string message)
        {
            var build = new Build() { Succeeded = false };
            build.AddChild(new Error() { Text = "Error when opening file: " + filePath });
            build.AddChild(new Error() { Text = message });
            return build;
        }

        private void BuildProject(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            DisplayBuild(null);
            this.projectFilePath = filePath;
            SettingsService.AddRecentProject(projectFilePath);
            UpdateRecentItemsMenu();
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.Title = projectFilePath + " - " + DefaultTitle;
            }

            string customArguments = SettingsService.GetCustomArguments(filePath);
            var parametersScreen = new BuildParametersScreen();
            parametersScreen.BrowseForMSBuildRequsted += BrowseForMSBuildExe;
            parametersScreen.PrefixArguments = filePath.QuoteIfNeeded();
            parametersScreen.MSBuildArguments = customArguments;
            parametersScreen.PostfixArguments = HostedBuild.GetPostfixArguments();
            parametersScreen.BuildRequested += () =>
            {
                parametersScreen.SaveSelectedMSBuild();
                SettingsService.SaveCustomArguments(filePath, parametersScreen.MSBuildArguments);
                BuildCore(projectFilePath, parametersScreen.MSBuildArguments);
            };
            parametersScreen.CancelRequested += () =>
            {
                parametersScreen.SaveSelectedMSBuild();
                DisplayWelcomeScreen();
            };
            SetContent(parametersScreen);
        }

        private async void BuildCore(string projectFilePath, string customArguments)
        {
            var progress = new BuildProgress() { IsIndeterminate = true };
            progress.ProgressText = $"Building {projectFilePath}...";
            SetContent(progress);
            var buildHost = new HostedBuild(projectFilePath, customArguments);
            Build result = await buildHost.BuildAndGetResult(progress);
            progress.ProgressText = "Analyzing build...";
            await QueueAnalyzeBuild(result);
            DisplayBuild(result);
        }

        private async Task QueueAnalyzeBuild(Build build)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    BuildAnalyzer.AnalyzeBuild(build);
                }
                catch (Exception ex)
                {
                    DialogService.ShowMessageBox(
                    "Error while analyzing build. Sorry about that. Please Ctrl+C to copy this text and file an issue on https://github.com/KirillOsenkov/MSBuildStructuredLog/issues/new \r\n" + ex.ToString());
                }
            });
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            await OpenLogFile();
        }

        private async void Build_Click(object sender, RoutedEventArgs e)
        {
            await OpenProjectOrSolution();
        }

        private async Task OpenLogFile()
        {
            var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Build Log",
                FileTypeFilter = new[] { FilePickerFileTypes.All, FileTypes.Binlog, FileTypes.Xml }
            });
            var firstFile = files.FirstOrDefault();
            if (firstFile is null)
            {
                return;
            }

            OpenLogFile(firstFile);
        }

        private async Task OpenProjectOrSolution()
        {
            var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
            {
                Title = "Open a solution or project",
                FileTypeFilter = new[] { FilePickerFileTypes.All, FileTypes.MsBuildProj, FileTypes.Sln }
            });
            var result = files.FirstOrDefault();

            if (result is not null
                && result.CanOpenRead
                && result.Path is { IsAbsoluteUri: true, Scheme: "file" } filePath)
            {
                BuildProject(filePath.LocalPath);
            }
        }

        private void RebuildProjectOrSolution()
        {
            if (!string.IsNullOrEmpty(projectFilePath))
            {
                var args = SettingsService.GetCustomArguments(projectFilePath);
                BuildCore(projectFilePath, args);
            }
        }

        private void DisplayBuild(Build build)
        {
            currentBuild = build != null ? new BuildControl(build, logFile) : null;
            SetContent(currentBuild);

            GC.Collect();
        }

        private void Reload()
        {
            OpenLogFile(logFile);
        }

        private async Task SaveAs()
        {
            if (currentBuild != null)
            {
                var prevParent = logFile is not null ? await logFile.GetParentAsync() : null;
                var result = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions()
                {
                    Title = "Save log file as",
                    FileTypeChoices = new[] { FileTypes.Binlog, FileTypes.Xml },
                    DefaultExtension = FileTypes.BinlogDefaultExtension,
                    SuggestedStartLocation = prevParent,
                    SuggestedFileName = logFile is not null
                        ? Path.GetFileNameWithoutExtension(logFile.ToString())
                        : null
                });

                if (result == null
                    || !result.CanOpenWrite)
                {
                    return;
                }

                var logFilePath = result.Path;

                logFile = result;
                // Use intermediate memory stream, as some platforms don't support sync access to the file system directly.
                using var memoryStream = new MemoryStream();
                Serialization.Write(currentBuild.Build, memoryStream, logFilePath?.ToString());
                memoryStream.Seek(0, SeekOrigin.Begin);

                await using (var writeStream = await result.OpenWriteAsync())
                {
                    await memoryStream.CopyToAsync(writeStream);
                    await writeStream.FlushAsync();
                }

                currentBuild.UpdateBreadcrumb(new Message { Text = $"Saved {logFilePath}" });

                if (result.CanBookmark)
                {
                    var bookmark = await result.SaveBookmarkAsync();
                    if (bookmark is not null)
                    {
                        SettingsService.AddRecentLogFile(bookmark);
                    }
                }
            }
        }

        private async void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                Reload();
            }
            else if (e.Key == Key.F6 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                RebuildProjectOrSolution();
            }
            else if (e.Key == Key.F6)
            {
                await OpenProjectOrSolution();
            }
            else if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await OpenLogFile();
            }
            else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                FocusSearch();
            }
            else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var content = mainContent.Content as BuildProgress;
                if (content != null)
                {
                    await Application.Current.Clipboard.SetTextAsync(content.MSBuildCommandLine);
                }
            }
            else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var task = SaveAs();
            }
        }

        private void FocusSearch()
        {
            CurrentBuildControl?.FocusSearch();
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        private void Rebuild_Click(object sender, RoutedEventArgs e)
        {
            RebuildProjectOrSolution();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (currentBuild != null)
            {
                currentBuild.CopySubtree();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (currentBuild != null)
            {
                currentBuild.Delete();
            }
        }

        private async void SetMSBuild_Click(object sender, RoutedEventArgs e)
        {
            await BrowseForMSBuildExe();

            var buildParametersScreen = mainContent.Content as BuildParametersScreen;
            if (buildParametersScreen != null)
            {
                buildParametersScreen.UpdateMSBuildLocations();
            }
        }

        private async Task BrowseForMSBuildExe()
        {
            var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
            {
                Title = "Select MSBuild file location", FileTypeFilter = new[] { FilePickerFileTypes.All, FileTypes.Exe }
            });
            var result = files.FirstOrDefault();
            if (result is null || !result.CanOpenRead)
            {
                return;
            }

            if (result.Path is not { IsAbsoluteUri: true, Scheme: "file" } fileNameUri)
            {
                return;
            }

            var fileName = fileNameUri.LocalPath;
            var isMsBuild = fileName.EndsWith("MSBuild.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith("MSBuild.exe", StringComparison.OrdinalIgnoreCase);
            if (!isMsBuild)
            {
                return;
            }

            SettingsService.AddRecentMSBuildLocation(fileName);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var task = SaveAs();
        }

        private void HelpLink_Click(object sender, RoutedEventArgs e)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "https://github.com/KirillOsenkov/MSBuildStructuredLog",
                UseShellExecute = true
            };
            Process.Start(psi);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            ((IClassicDesktopStyleApplicationLifetime)Application.Current.ApplicationLifetime).Shutdown();
        }

        private void StartPage_Click(object sender, RoutedEventArgs e)
        {
            DisplayWelcomeScreen();
        }
    }
}