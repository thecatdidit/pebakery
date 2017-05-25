﻿/*
    Copyright (C) 2016-2017 Hajin Jang
    Licensed under GPL 3.0
 
    PEBakery is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using PEBakery.Helper;
using PEBakery.Lib;
using PEBakery.Core;
using MahApps.Metro.IconPacks;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SQLite.Net;
using System.Text;
using PEBakery.Exceptions;
using System.Runtime.CompilerServices;

namespace PEBakery.WPF
{
    #region MainWindow
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Variables
        private ProjectCollection projects;
        public ProjectCollection Projects { get => projects; }

        private string baseDir;
        public string BaseDir { get => baseDir; }

        private BackgroundWorker loadWorker = new BackgroundWorker();
        private BackgroundWorker refreshWorker = new BackgroundWorker();

        private TreeViewModel curMainTree;
        public TreeViewModel CurMainTree { get => curMainTree; }

        private TreeViewModel curBuildTree;
        public TreeViewModel CurBuildTree { get => curBuildTree; set => curBuildTree = value; }

        private Logger logger;
        public Logger Logger { get => logger; }
        private PluginCache pluginCache;

        const int MaxDpiScale = 4;
        private int allPluginCount = 0;
        private readonly string settingFile;
        private SettingViewModel setting;
        public SettingViewModel Setting { get => setting; }

        public MainViewModel Model;
        #endregion

        #region Constructor
        public MainWindow()
        {
            InitializeComponent();
            this.Model = this.DataContext as MainViewModel;

            string[] args = App.Args;
            if (int.TryParse(Properties.Resources.IntegerVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out App.Version) == false)
            {
                Console.WriteLine("Cannot determine version");
                Application.Current.Shutdown(1);
            }  

            string argBaseDir = Environment.CurrentDirectory;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "/basedir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        argBaseDir = System.IO.Path.GetFullPath(args[i + 1]);
                        Environment.CurrentDirectory = argBaseDir;
                    }
                    else
                    {
                        Console.WriteLine("\'/basedir\' must be used with path\r\n");
                    }
                }
                else if (string.Equals(args[i], "/?", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "/help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "/h", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Sorry, help message not implemented\r\n");
                }
            }

            this.baseDir = argBaseDir;

            this.settingFile = System.IO.Path.Combine(argBaseDir, "PEBakery.ini");
            App.Setting = this.setting = new SettingViewModel(settingFile);
            Logger.DebugLevel = setting.Log_DebugLevel;

            string logDBFile = System.IO.Path.Combine(baseDir, "PEBakeryLog.db");
            try
            {
                App.Logger = logger = new Logger(logDBFile);
                logger.System_Write(new LogInfo(LogState.Info, $"PEBakery launched"));
            }
            catch (SQLiteException e)
            { // Update failure
                string msg = $"SQLite Error : {e.Message}\r\n\r\nLog database is corrupted.\r\nPlease delete PEBakeryLog.db and restart.";
                MessageBox.Show(msg, "SQLite Error!", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown(1);
            }
            this.setting.LogDB = logger.DB;

            // If plugin cache is enabled, generate cache after 5 seconds
            if (setting.Plugin_EnableCache)
            {
                string cacheDBFile = System.IO.Path.Combine(baseDir, "PEBakeryCache.db");
                try
                {
                    this.pluginCache = new PluginCache(cacheDBFile);
                    logger.System_Write(new LogInfo(LogState.Info, $"PluginCache enabled, {pluginCache.Table<DB_PluginCache>().Count()} cached plugin found"));
                }
                catch (SQLiteException e)
                { // Update failure
                    string msg = $"SQLite Error : {e.Message}\r\n\r\nCache database is corrupted.\r\nPlease delete PEBakeryCache.db and restart.";
                    MessageBox.Show(msg, "SQLite Error!", MessageBoxButton.OK, MessageBoxImage.Error);
                    Application.Current.Shutdown(1);
                }

                this.setting.CacheDB = pluginCache;
            }
            else
            {
                logger.System_Write(new LogInfo(LogState.Info, $"PluginCache disabled"));
            }

            StartLoadWorker();
        }
        #endregion

        #region Background Workers
        private void StartLoadWorker()
        {
            Stopwatch watch = Stopwatch.StartNew();

            // Set PEBakery Logo
            Image image = new Image()
            {
                UseLayoutRounding = true,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Source = ImageHelper.ToBitmapImage(Properties.Resources.DonutPng),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            PluginLogo.Content = image;

            // Prepare PEBakery Loading Information
            Model.PluginTitleText = "Welcome to PEBakery!";
            Model.PluginDescriptionText = "PEBakery loading...";
            logger.System_Write(new LogInfo(LogState.Info, $@"Loading from [{baseDir}]"));
            MainCanvas.Children.Clear();

            int stage2LinksCount = 0;
            int loadedPluginCount = 0;
            int stage1CachedCount = 0;
            int stage2LoadedCount = 0;
            int stage2CachedCount = 0;

            Model.BottomProgressBarMinimum = 0;
            Model.BottomProgressBarMaximum = 100;
            Model.BottomProgressBarValue = 0;
            Model.ProgressRingActive = true;
            Model.SwitchStatusProgressBar = false; // Show Progress Bar
            loadWorker = new BackgroundWorker();

            loadWorker.DoWork += (object sender, DoWorkEventArgs e) =>
            {
                string baseDir = (string)e.Argument;
                BackgroundWorker worker = sender as BackgroundWorker;

                // Init ProjectCollection
                if (setting.Plugin_EnableCache) // Use PluginCache - Fast speed, more memory
                    projects = new ProjectCollection(baseDir, pluginCache);
                else  // Do not use PluginCache - Slow speed, less memory
                    projects = new ProjectCollection(baseDir, null);

                allPluginCount = projects.PrepareLoad(out stage2LinksCount);
                Dispatcher.Invoke(() => { Model.BottomProgressBarMaximum = allPluginCount + stage2LinksCount; });

                // Let's load plugins parallelly
                projects.Load(worker);
                setting.UpdateProjectList();

                // Populate TreeView
                Dispatcher.Invoke(() =>
                {
                    foreach (Project project in projects.Projects)
                    {
                        List<Node<Plugin>> plugins = project.VisiblePlugins.Root;
                        RecursivePopulateTreeView(plugins, Model.MainTree, Model.MainTree);
                    };
                    int pIdx = setting.Project_DefaultIndex;
                    curMainTree = Model.MainTree.Children[pIdx];
                    curMainTree.IsExpanded = true;
                    if (projects[pIdx] != null)
                        DrawPlugin(projects[pIdx].MainPlugin);
                });
            };
            loadWorker.WorkerReportsProgress = true;
            loadWorker.ProgressChanged += (object sender, ProgressChangedEventArgs e) =>
            {
                Interlocked.Increment(ref loadedPluginCount);
                Model.BottomProgressBarValue = loadedPluginCount;
                string msg = string.Empty;
                switch (e.ProgressPercentage)
                {
                    case 0:  // Stage 1
                        if (e.UserState == null)
                            msg = $"Error";
                        else
                            msg = $"{e.UserState}";
                        break;
                    case 1:  // Stage 1, Cached
                        Interlocked.Increment(ref stage1CachedCount);
                        if (e.UserState == null)
                            msg = $"Cached - Error";
                        else
                            msg = $"Cached - {e.UserState}";
                        break;
                    case 2:  // Stage 2
                        Interlocked.Increment(ref stage2LoadedCount); 
                        if (e.UserState == null)
                            msg = $"Error";
                        else
                            msg = $"{e.UserState}";
                        break;
                    case 3:  // Stage 2, Cached
                        Interlocked.Increment(ref stage2LoadedCount);
                        Interlocked.Increment(ref stage2CachedCount);
                        if (e.UserState == null)
                            msg = $"Cached - Error";
                        else
                            msg = $"Cached - {e.UserState}";
                        break;
                }
                int stage = e.ProgressPercentage / 2 + 1;
                if (stage == 1)
                    msg = $"Stage {stage} ({loadedPluginCount} / {allPluginCount}) \r\n{msg}";
                else
                    msg = $"Stage {stage} ({stage2LoadedCount} / {stage2LinksCount}) \r\n{msg}";

                Model.PluginDescriptionText = $"PEBakery loading...\r\n{msg}";
            };
            loadWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
            {
                StringBuilder b = new StringBuilder();
                b.Append("Projects [");
                List<Project> projList = projects.Projects;
                for (int i = 0; i < projList.Count; i++)
                {
                    b.Append(projList[i].ProjectName);
                    if (i + 1 < projList.Count)
                        b.Append(", ");
                }
                b.Append("] loaded");
                logger.System_Write(new LogInfo(LogState.Info, b.ToString()));

                watch.Stop();
                double t = watch.Elapsed.TotalMilliseconds / 1000.0;
                string msg;
                if (setting.Plugin_EnableCache)
                {
                    double cachePercent = (double)(stage1CachedCount + stage2CachedCount) * 100 / (allPluginCount + stage2LinksCount);
                    msg = $"{allPluginCount} plugins loaded ({cachePercent:0.#}% cached), took {t:0.###}sec";
                    Model.StatusBarText = msg;
                }
                else
                {
                    msg = $"{allPluginCount} plugins loaded, took {t:hh\\:mm\\:ss}";
                    Model.StatusBarText = msg;
                }
                Model.ProgressRingActive = false;
                Model.SwitchStatusProgressBar = true; // Show Status Bar
                
                logger.System_Write(new LogInfo(LogState.Info, msg));
                logger.System_Write(Logger.LogSeperator);

                // If plugin cache is enabled, generate cache.
                if (setting.Plugin_EnableCache)
                    StartCacheWorker();
            };
            loadWorker.RunWorkerAsync(baseDir);
        }

        private void StartCacheWorker()
        {
            // TODO: Prevent DB Corruption due to sudden exit
            if (PluginCache.dbLock == 0)
            {
                Interlocked.Increment(ref PluginCache.dbLock);
                try
                {
                    Stopwatch watch = new Stopwatch();
                    BackgroundWorker cacheWorker = new BackgroundWorker();

                    Model.ProgressRingActive = true;
                    int updatedCount = 0;
                    int cachedCount = 0;
                    cacheWorker.DoWork += (object sender, DoWorkEventArgs e) =>
                    {
                        BackgroundWorker worker = sender as BackgroundWorker;

                        watch = Stopwatch.StartNew();
                        pluginCache.CachePlugins(projects, worker);
                    };

                    cacheWorker.WorkerReportsProgress = true;
                    cacheWorker.ProgressChanged += (object sender, ProgressChangedEventArgs e) =>
                    {
                        Interlocked.Increment(ref cachedCount);
                        if (e.ProgressPercentage == 1)
                            Interlocked.Increment(ref updatedCount);
                    };
                    cacheWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
                    {
                        watch.Stop();

                        double cachePercent = (double)updatedCount * 100 / allPluginCount;

                        double t = watch.Elapsed.TotalMilliseconds / 1000.0;
                        string msg = $"{allPluginCount} plugins cached ({cachePercent:0.#}% updated), took {t:0.###}sec";
                        logger.System_Write(new LogInfo(LogState.Info, msg));
                        logger.System_Write(Logger.LogSeperator);

                        Model.ProgressRingActive = false;
                    };
                    cacheWorker.RunWorkerAsync();
                }
                finally
                {
                    Interlocked.Decrement(ref PluginCache.dbLock);
                }
            }
        }
        
        private void StartReloadPluginWorker()
        {
            if (curMainTree == null)
                return;

            if (refreshWorker.IsBusy)
                return;

            Stopwatch watch = new Stopwatch();

            Model.ProgressRingActive = true;
            refreshWorker = new BackgroundWorker();
            refreshWorker.DoWork += (object sender, DoWorkEventArgs e) =>
            {
                watch.Start();
                Plugin p = curMainTree.Plugin.Project.RefreshPlugin(curMainTree.Plugin);
                if (p != null)
                {
                    curMainTree.Plugin = p;
                    Dispatcher.Invoke(() => 
                    {
                        curMainTree.Plugin = p;
                        DrawPlugin(curMainTree.Plugin);
                    });
                }
            };
            refreshWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
            {
                Model.ProgressRingActive = false;
                watch.Stop();
                double sec = watch.Elapsed.TotalSeconds;
                string msg = $"{Path.GetFileName(curMainTree.Plugin.ShortPath)} reloaded. Took {sec:0.000}sec";
                Model.StatusBarText = msg;
            };
            refreshWorker.RunWorkerAsync();
        }

        private void StartBuildWorker()
        {
            Stopwatch watch = Stopwatch.StartNew();

            int stage2LinksCount = 0;
            int loadedPluginCount = 0;
            int stage1CachedCount = 0;
            int stage2LoadedCount = 0;
            int stage2CachedCount = 0;

            Model.BottomProgressBarMinimum = 0;
            Model.BottomProgressBarMaximum = 100;
            Model.BottomProgressBarValue = 0;
            Model.ProgressRingActive = true;
            Model.SwitchStatusProgressBar = false; // Show Progress Bar
            loadWorker = new BackgroundWorker();

            loadWorker.DoWork += (object sender, DoWorkEventArgs e) =>
            {
                string baseDir = (string)e.Argument;
                BackgroundWorker worker = sender as BackgroundWorker;

                // Init ProjectCollection
                if (setting.Plugin_EnableCache) // Use PluginCache - Fast speed, more memory
                    projects = new ProjectCollection(baseDir, pluginCache);
                else  // Do not use PluginCache - Slow speed, less memory
                    projects = new ProjectCollection(baseDir, null);

                allPluginCount = projects.PrepareLoad(out stage2LinksCount);
                Dispatcher.Invoke(() => { Model.BottomProgressBarMaximum = allPluginCount + stage2LinksCount; });

                // Let's load plugins parallelly
                projects.Load(worker);
                setting.UpdateProjectList();

                // Populate TreeView
                Dispatcher.Invoke(() =>
                {
                    foreach (Project project in projects.Projects)
                    {
                        List<Node<Plugin>> plugins = project.VisiblePlugins.Root;
                        RecursivePopulateTreeView(plugins, Model.MainTree, Model.MainTree);
                    };
                    int pIdx = setting.Project_DefaultIndex;
                    curMainTree = Model.MainTree.Children[pIdx];
                    curMainTree.IsExpanded = true;
                    if (projects[pIdx] != null)
                        DrawPlugin(projects[pIdx].MainPlugin);
                });
            };
            loadWorker.WorkerReportsProgress = true;
            loadWorker.ProgressChanged += (object sender, ProgressChangedEventArgs e) =>
            {
                Interlocked.Increment(ref loadedPluginCount);
                Model.BottomProgressBarValue = loadedPluginCount;
                string msg = string.Empty;
                switch (e.ProgressPercentage)
                {
                    case 0:  // Stage 1
                        if (e.UserState == null)
                            msg = $"Error";
                        else
                            msg = $"{e.UserState}";
                        break;
                    case 1:  // Stage 1, Cached
                        Interlocked.Increment(ref stage1CachedCount);
                        if (e.UserState == null)
                            msg = $"Cached - Error";
                        else
                            msg = $"Cached - {e.UserState}";
                        break;
                    case 2:  // Stage 2
                        Interlocked.Increment(ref stage2LoadedCount);
                        if (e.UserState == null)
                            msg = $"Error";
                        else
                            msg = $"{e.UserState}";
                        break;
                    case 3:  // Stage 2, Cached
                        Interlocked.Increment(ref stage2LoadedCount);
                        Interlocked.Increment(ref stage2CachedCount);
                        if (e.UserState == null)
                            msg = $"Cached - Error";
                        else
                            msg = $"Cached - {e.UserState}";
                        break;
                }
                int stage = e.ProgressPercentage / 2 + 1;
                if (stage == 1)
                    msg = $"Stage {stage} ({loadedPluginCount} / {allPluginCount}) \r\n{msg}";
                else
                    msg = $"Stage {stage} ({stage2LoadedCount} / {stage2LinksCount}) \r\n{msg}";

                Model.PluginDescriptionText = $"PEBakery loading...\r\n{msg}";
            };
            loadWorker.RunWorkerCompleted += (object sender, RunWorkerCompletedEventArgs e) =>
            {
                StringBuilder b = new StringBuilder();
                b.Append("Projects [");
                List<Project> projList = projects.Projects;
                for (int i = 0; i < projList.Count; i++)
                {
                    b.Append(projList[i].ProjectName);
                    if (i + 1 < projList.Count)
                        b.Append(", ");
                }
                b.Append("] loaded");
                logger.System_Write(new LogInfo(LogState.Info, b.ToString()));

                watch.Stop();
                double t = watch.Elapsed.TotalMilliseconds / 1000.0;
                string msg;
                if (setting.Plugin_EnableCache)
                {
                    double cachePercent = (double)(stage1CachedCount + stage2CachedCount) * 100 / (allPluginCount + stage2LinksCount);
                    msg = $"{allPluginCount} plugins loaded ({cachePercent:0.#}% cached), took {t:0.###}sec";
                    Model.StatusBarText = msg;
                }
                else
                {
                    msg = $"{allPluginCount} plugins loaded, took {t:hh\\:mm\\:ss}";
                    Model.StatusBarText = msg;
                }
                Model.ProgressRingActive = false;
                Model.SwitchStatusProgressBar = true; // Show Status Bar

                logger.System_Write(new LogInfo(LogState.Info, msg));
                logger.System_Write(Logger.LogSeperator);

                // If plugin cache is enabled, generate cache.
                if (setting.Plugin_EnableCache)
                    StartCacheWorker();
            };
            loadWorker.RunWorkerAsync(baseDir);
        }
        #endregion

        #region DrawPlugin
        public void DrawPlugin(Plugin p)
        {
            Stopwatch watch = new Stopwatch();
            double size = PluginLogo.ActualWidth * MaxDpiScale;
            if (p.Type == PluginType.Directory)
            {
                PluginLogo.Content = ImageHelper.GetMaterialIcon(PackIconMaterialKind.Folder, 0);
            }
            else
            {
                try
                {
                    MemoryStream mem = EncodedFile.ExtractLogo(p, out ImageType type);
                    if (type == ImageType.Svg)
                    {
                        Image image = new Image()
                        {
                            Source = ImageHelper.SvgToBitmapImage(mem, size, size),
                            Stretch = Stretch.Uniform
                        };
                        PluginLogo.Content = image;
                    }
                    else
                    {
                        Image image = new Image();
                        BitmapImage bitmap = ImageHelper.ImageToBitmapImage(mem);
                        image.StretchDirection = StretchDirection.DownOnly;
                        image.Stretch = Stretch.Uniform;
                        image.UseLayoutRounding = true; // Must to prevent blurry image rendering
                        image.Source = bitmap;

                        Grid grid = new Grid();
                        grid.Children.Add(image);

                        PluginLogo.Content = grid;
                    }
                }
                catch
                { // No logo file - use default
                    if (p.Type == PluginType.Plugin)
                        PluginLogo.Content = ImageHelper.GetMaterialIcon(PackIconMaterialKind.FileDocument, 0);
                    else if (p.Type == PluginType.Link)
                        PluginLogo.Content = ImageHelper.GetMaterialIcon(PackIconMaterialKind.OpenInNew, 0);
                }
            }

            MainCanvas.Children.Clear();
            if (p.Type == PluginType.Directory)
            {
                Model.PluginTitleText = StringEscaper.Unescape(p.Title);
                Model.PluginDescriptionText = string.Empty;
                Model.PluginVersionText = string.Empty;
                Model.PluginAuthorText = string.Empty;
            }
            else
            {
                Model.PluginTitleText = StringEscaper.Unescape(p.Title);
                Model.PluginDescriptionText = StringEscaper.Unescape(p.Description);
                Model.PluginVersionText = $"v{p.Version}";
                Model.PluginAuthorText = p.Author;

                double scaleFactor = setting.Interface_ScaleFactor / 100;
                ScaleTransform scale = new ScaleTransform(scaleFactor, scaleFactor);
                UIRenderer render = new UIRenderer(MainCanvas, this, p, logger, scaleFactor);
                MainCanvas.LayoutTransform = scale;
                render.Render();
            }
        }
        #endregion

        #region Main Buttons
        private async void BuildButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Exact Locking without Race Condition
            if (Engine.WorkingLock == 0)  // Start Build
            {
                Interlocked.Increment(ref Engine.WorkingLock);

                // Determine current project
                Project project = curMainTree.Plugin.Project;

                Tree<Plugin> activeTree = project.GetActivePlugin();
                Model.BuildTree.Children.Clear();
                RecursivePopulateTreeView(activeTree.Root, Model.BuildTree, Model.BuildTree);
                curBuildTree = null;

                EngineState s = new EngineState(project, logger);
                s.SetLogOption(setting);

                Engine.WorkingEngine = new Engine(s);

                // Build Start, Switch to Build View
                Model.SwitchNormalBuildInterface = false;

                // Run
                long buildId = await Engine.WorkingEngine.Run($"Project {project.ProjectName}");

#if DEBUG  // TODO: Remove this later, this line is for Debug
                logger.ExportBuildLog(LogExportType.Text, Path.Combine(s.BaseDir, "LogDebugDump.txt"), buildId);
#endif

                // Build Ended, Switch to Normal View
                Model.SwitchNormalBuildInterface = true;
                DrawPlugin(curMainTree.Plugin);

                Engine.WorkingEngine = null;
                Interlocked.Decrement(ref Engine.WorkingLock);
            }
            else // Stop Build
            {
                Engine.WorkingEngine?.ForceStop();

                Engine.WorkingEngine = null;
                Interlocked.Decrement(ref Engine.WorkingLock);
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (loadWorker.IsBusy == false)
            {
                (MainTreeView.DataContext as TreeViewModel).Children.Clear();

                StartLoadWorker();
            }
        }

        private void SettingButton_Click(object sender, RoutedEventArgs e)
        {
            double old_Interface_ScaleFactor = setting.Interface_ScaleFactor;
            bool old_Plugin_EnableCache = setting.Plugin_EnableCache;

            SettingWindow dialog = new SettingWindow(setting);
            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                // Scale Factor
                double newScaleFactor = setting.Interface_ScaleFactor;
                if (double.Epsilon < Math.Abs(newScaleFactor - old_Interface_ScaleFactor)) // Not Equal
                    DrawPlugin(curMainTree.Plugin);

                // PluginCache
                if (old_Plugin_EnableCache == false && setting.Plugin_EnableCache)
                    StartCacheWorker();

                // DebugLevel
                Logger.DebugLevel = setting.Log_DebugLevel;
            }
        }

        private void UtilityButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Not Implemented", "Sorry", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void LogButton_Click(object sender, RoutedEventArgs e)
        {
            LogWindow dialog = new LogWindow();
            dialog.Show();
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Not Implemented", "Sorry", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Not Implemented", "Sorry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        #endregion

        #region Plugin Buttons
        private async void PluginRunButton_Click(object sender, RoutedEventArgs e)
        {
            Plugin p = curMainTree.Plugin;
            if (p.Sections.ContainsKey("Process"))
            {
                if (Engine.WorkingLock == 0)  // Start Build
                {
                    Interlocked.Increment(ref Engine.WorkingLock);

                    // Populate BuildTree
                    Model.BuildTree.Children.Clear();
                    PopulateOneTreeView(p, Model.BuildTree, Model.BuildTree);
                    curBuildTree = null;

                    EngineState s = new EngineState(p.Project, logger, p);
                    s.SetLogOption(setting);

                    Engine.WorkingEngine = new Engine(s);

                    // Build Start, Switch to Build View
                    Model.SwitchNormalBuildInterface = false;

                    // Run
                    long buildId = await Engine.WorkingEngine.Run($"{p.Title} - Run");

#if DEBUG  // TODO: Remove this later, this line is for Debug
                    logger.ExportBuildLog(LogExportType.Text, Path.Combine(s.BaseDir, "LogDebugDump.txt"), buildId);
#endif

                    // Build Ended, Switch to Normal View
                    Model.SwitchNormalBuildInterface = true;

                    Engine.WorkingEngine = null;
                    Interlocked.Decrement(ref Engine.WorkingLock);
                }
                else // Stop Build
                {
                    Engine.WorkingEngine?.ForceStop();

                    Engine.WorkingEngine = null;
                    Interlocked.Decrement(ref Engine.WorkingLock);
                }
            }
            else
            {
                string msg = $"Cannot run [{p.Title}]!\r\nPlease implement section [Process]";
                Logger.System_Write(new LogInfo(LogState.Error, msg));
                MessageBox.Show(msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PluginEditButton_Click(object sender, RoutedEventArgs e)
        {
            ProcessStartInfo procInfo = new ProcessStartInfo()
            {
                Verb = "open",
                FileName = curMainTree.Plugin.FullPath,
                UseShellExecute = true
            };
            Process.Start(procInfo);
        }

        private void PluginRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            StartReloadPluginWorker();
        }
        #endregion

        #region TreeView Methods
        private void RecursivePopulateTreeView(List<Node<Plugin>> plugins, TreeViewModel treeRoot, TreeViewModel treeParent)
        {
            foreach (Node<Plugin> node in plugins)
            {
                TreeViewModel item = PopulateOneTreeView(node.Data, treeRoot, treeParent);

                if (0 < node.Child.Count)
                    RecursivePopulateTreeView(node.Child, treeRoot, item);
            }
        }

        public TreeViewModel PopulateOneTreeView(Plugin p, TreeViewModel treeRoot, TreeViewModel treeParent)
        {
            TreeViewModel item = new TreeViewModel(treeRoot, treeParent);
            treeParent.Children.Add(item);
            item.Plugin = p;

            if (p.Type == PluginType.Directory)
            {
                item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.Folder, 0));
            }
            else if (p.Type == PluginType.Plugin)
            {
                if (p.Level == Project.MainLevel)
                    item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.Settings, 0));
                else if (p.Mandatory)
                    item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.LockOutline, 0));
                else
                    item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.File, 0));
            }
            else if (p.Type == PluginType.Link)
            {
                item.SetIcon(ImageHelper.GetMaterialIcon(PackIconMaterialKind.OpenInNew, 0));
            }

            return item;
        }

        private void MainTreeView_Loaded(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            window.KeyDown += MainTreeView_KeyDown;
        }

        /// <summary>
        /// Used to ensure pressing 'Space' to toggle TreeView's checkbox.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainTreeView_KeyDown(object sender, KeyEventArgs e)
        {
            // Window window = sender as Window;
            base.OnKeyDown(e);

            if (e.Key == Key.Space)
            {
                if (Keyboard.FocusedElement is FrameworkElement focusedElement)
                {
                    if (focusedElement.DataContext is TreeViewModel node)
                    {
                        if (node.Checked == true)
                            node.Checked = false;
                        else if (node.Checked == false)
                            node.Checked = true;
                        e.Handled = true;
                    }
                }
            }
        }

        private void MainTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var tree = sender as TreeView;

            if (tree.SelectedItem is TreeViewModel)
            {
                TreeViewModel item = curMainTree = tree.SelectedItem as TreeViewModel;

                Dispatcher.Invoke(() =>
                {
                    Stopwatch watch = new Stopwatch();
                    watch.Start();
                    DrawPlugin(item.Plugin);
                    watch.Stop();
                    double sec = watch.Elapsed.TotalSeconds;
                    Model.StatusBarText = $"{Path.GetFileName(curMainTree.Plugin.ShortPath)} rendered. Took {sec:0.000}sec";
                });
            }
        }
        #endregion

        #region Window Event Handler
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            Engine.WorkingEngine?.ForceStop();

            logger.DB.Close();
            if (pluginCache != null)
                pluginCache.WaitClose();
        }
        #endregion
    }
    #endregion

    #region MainViewModel
    public class MainViewModel : INotifyPropertyChanged
    {
        public MainViewModel()
        {
            MainTree = new TreeViewModel(null, null);
            BuildTree = new TreeViewModel(null, null);
        }

        #region Normal Interface
        private bool progressRingActive = true;
        public bool ProgressRingActive
        {
            get => progressRingActive;
            set
            {
                progressRingActive = value;
                OnPropertyUpdate("ProgressRingActive");
            }
        }

        private string pluginTitleText = "Welcome to PEBakery!";
        public string PluginTitleText
        {
            get => pluginTitleText;
            set
            {
                pluginTitleText = value;
                OnPropertyUpdate("PluginTitleText");
            }
        }

        private string pluginAuthorText = "Hajin Jang";
        public string PluginAuthorText
        {
            get => pluginAuthorText;
            set
            {
                pluginAuthorText = value;
                OnPropertyUpdate("PluginAuthorText");
            }
        }

        private string pluginVersionText = "v0.1";
        public string PluginVersionText
        {
            get => pluginVersionText;
            set
            {
                pluginVersionText = value;
                OnPropertyUpdate("PluginVersionText");
            }
        }

        private string pluginDescriptionText = "PEBakery is now loading, please wait...";
        public string PluginDescriptionText
        {
            get => pluginDescriptionText;
            set
            {
                pluginDescriptionText = value;
                OnPropertyUpdate("PluginDescriptionText");
            }
        }

        private string statusBarText = string.Empty;
        public string StatusBarText
        {
            get => statusBarText;
            set
            {
                statusBarText = value;
                OnPropertyUpdate("StatusBarText");
            }
        }

        // True - StatusBar, False - ProgressBar
        private bool switchStatusProgressBar = false;
        public bool SwitchStatusProgressBar
        {
            get => switchStatusProgressBar;
            set
            {
                switchStatusProgressBar = value;
                if (value)
                {
                    BottomStatusBarVisibility = Visibility.Visible;
                    BottomProgressBarVisibility = Visibility.Collapsed;
                }
                else
                {
                    BottomStatusBarVisibility = Visibility.Collapsed;
                    BottomProgressBarVisibility = Visibility.Visible;
                }
            }
        }

        private Visibility bottomStatusBarVisibility = Visibility.Collapsed;
        public Visibility BottomStatusBarVisibility
        {
            get => bottomStatusBarVisibility;
            set
            {
                bottomStatusBarVisibility = value;
                OnPropertyUpdate("BottomStatusBarVisibility");
            }
        }

        private double bottomProgressBarMinimum = 0;
        public double BottomProgressBarMinimum
        {
            get => bottomProgressBarMinimum;
            set
            {
                bottomProgressBarMinimum = value;
                OnPropertyUpdate("BottomProgressBarMinimum");
            }
        }

        private double bottomProgressBarMaximum = 100;
        public double BottomProgressBarMaximum
        {
            get => bottomProgressBarMaximum;
            set
            {
                bottomProgressBarMaximum = value;
                OnPropertyUpdate("BottomProgressBarMaximum");
            }
        }

        private double bottomProgressBarValue = 0;
        public double BottomProgressBarValue
        {
            get => bottomProgressBarValue;
            set
            {
                bottomProgressBarValue = value;
                OnPropertyUpdate("BottomProgressBarValue");
            }
        }

        private Visibility bottomProgressBarVisibility = Visibility.Visible;
        public Visibility BottomProgressBarVisibility
        {
            get => bottomProgressBarVisibility;
            set
            {
                bottomProgressBarVisibility = value;
                OnPropertyUpdate("BottomProgressBarVisibility");
            }
        }

        // True - Normal, False - Build
        private bool switchNormalBuildInterface = true;
        public bool SwitchNormalBuildInterface
        {
            get => switchNormalBuildInterface;
            set
            {
                switchNormalBuildInterface = value;
                if (value)
                {
                    NormalInterfaceVisibility = Visibility.Visible;
                    BuildInterfaceVisibility = Visibility.Collapsed;
                }
                else
                {
                    NormalInterfaceVisibility = Visibility.Collapsed;
                    BuildInterfaceVisibility = Visibility.Visible;
                }
            }
        }

        private Visibility normalInterfaceVisibility = Visibility.Visible;
        public Visibility NormalInterfaceVisibility
        {
            get => normalInterfaceVisibility;
            set
            {
                normalInterfaceVisibility = value;
                OnPropertyUpdate("NormalInterfaceVisibility");
            }
        }

        private Visibility buildInterfaceVisibility = Visibility.Collapsed;
        public Visibility BuildInterfaceVisibility
        {
            get => buildInterfaceVisibility;
            set
            {
                buildInterfaceVisibility = value;
                OnPropertyUpdate("BuildInterfaceVisibility");
            }
        }

        private TreeViewModel mainTree;
        public TreeViewModel MainTree
        {
            get => mainTree;
            set
            {
                mainTree = value;
                OnPropertyUpdate("MainTree");
            }
        }
        #endregion

        #region Build Interface
        private TreeViewModel buildTree;
        public TreeViewModel BuildTree
        {
            get => buildTree;
            set
            {
                buildTree = value;
                OnPropertyUpdate("BuildTree");
            }
        }

        private string buildPosition = string.Empty;
        public string BuildPosition
        {
            get => buildPosition;
            set
            {
                buildPosition = value;
                OnPropertyUpdate("BuildPosition");
            }
        }

        private string buildEchoMessage = string.Empty;
        public string BuildEchoMessage
        {
            get => buildEchoMessage;
            set
            {
                buildEchoMessage = value;
                OnPropertyUpdate("BuildEchoMessage");
            }
        }

        public const double BuildCommandProgressBarMax = 1000;

        private double buildCommandProgressBarValue = 0;
        public double BuildCommandProgressBarValue
        {
            get => buildCommandProgressBarValue;
            set
            {
                buildCommandProgressBarValue = value;
                OnPropertyUpdate("BuildCommandProgressBarValue");
            }
        }

        private double buildPluginProgressBarMax = 100;
        public double BuildPluginProgressBarMax
        {
            get => buildPluginProgressBarMax;
            set
            {
                buildPluginProgressBarMax = value;
                OnPropertyUpdate("BuildPluginProgressBarMax");
            }
        }

        private double buildPluginProgressBarValue = 0;
        public double BuildPluginProgressBarValue
        {
            get => buildPluginProgressBarValue;
            set
            {
                buildPluginProgressBarValue = value;
                OnPropertyUpdate("BuildPluginProgressBarValue");
            }
        }

        private Visibility buildFullProgressBarVisibility = Visibility.Visible;
        public Visibility BuildFullProgressBarVisibility
        {
            get => buildFullProgressBarVisibility;
            set
            {
                buildFullProgressBarVisibility = value;
                OnPropertyUpdate("BuildFullProgressBarVisibility");
            }
        }

        private double buildFullProgressBarMax = 100;
        public double BuildFullProgressBarMax
        {
            get => buildFullProgressBarMax;
            set
            {
                buildFullProgressBarMax = value;
                OnPropertyUpdate("BuildFullProgressBarMax");
            }
        }

        private double buildFullProgressBarValue = 0;
        public double BuildFullProgressBarValue
        {
            get => buildFullProgressBarValue;
            set
            {
                buildFullProgressBarValue = value;
                OnPropertyUpdate("BuildFullProgressBarValue");
            }
        }
        #endregion

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyUpdate(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    #endregion

    #region TreeViewModel
    public class TreeViewModel : INotifyPropertyChanged
    {
        private TreeViewModel root;
        public TreeViewModel Root { get => root; }

        private TreeViewModel parent;
        public TreeViewModel Parent { get => parent; }

        public TreeViewModel(TreeViewModel root, TreeViewModel parent)
        {
            if (root == null)
                this.root = this;
            else
                this.root = root;
            this.parent = parent;
        }

        private bool isExpanded = false;
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                isExpanded = value;
                OnPropertyUpdate("IsExpanded");
            }
        }

        #region Build Mode Property
        private bool buildFocus = false;
        public bool BuildFocus
        {
            get => buildFocus;
            set
            {
                buildFocus = value;
                icon.Foreground = BuildBrush;
                OnPropertyUpdate("BuildFontWeight");
                OnPropertyUpdate("BuildBrush");
                OnPropertyUpdate("BuildIcon");
            }
        }

        public FontWeight BuildFontWeight
        {
            get
            {
                if (buildFocus)
                    return FontWeights.Bold;
                else
                    return FontWeights.Normal;
            }
        }

        public Brush BuildBrush
        {
            get
            {
                if (buildFocus)
                    return Brushes.Red;
                else
                    return Brushes.Black;
            }
        }
        #endregion

        public bool Checked
        {
            get
            {
                switch (plugin.Selected)
                {
                    case SelectedState.True:
                        return true;
                    default:
                        return false;
                }
            }
            set
            {
                MainWindow w = (Application.Current.MainWindow as MainWindow);
                w.Dispatcher.Invoke(() =>
                {
                    w.Model.ProgressRingActive = true;
                    if (plugin.Mandatory == false && plugin.Selected != SelectedState.None)
                    {
                        if (value)
                        {
                            plugin.Selected = SelectedState.True;

                            try
                            {
                                // Run 'Disable' directive
                                DisablePlugins(root, plugin);
                            }
                            catch (Exception e)
                            {
                                w.Logger.System_Write(new LogInfo(LogState.Error, e));
                            }
                        }
                        else
                        {
                            plugin.Selected = SelectedState.False;
                        }


                        if (plugin.Level != Project.MainLevel)
                        {
                            if (0 < this.Children.Count)
                            { // Set child plugins, too -> Top-down propagation
                                foreach (TreeViewModel childModel in this.Children)
                                {
                                    if (value)
                                        childModel.Checked = true;
                                    else
                                        childModel.Checked = false;
                                }
                            }

                            ParentCheckedPropagation();
                        }

                        OnPropertyUpdate("Checked");
                    }
                    w.Model.ProgressRingActive = false;
                });
            }
        }

        public void ParentCheckedPropagation()
        { // Bottom-up propagation of Checked property
            if (parent == null)
                return;

            bool setParentChecked = false;

            foreach (TreeViewModel sibling in parent.Children)
            { // Siblings
                if (sibling.Checked)
                    setParentChecked = true;
            }

            parent.SetParentChecked(setParentChecked);
        }

        public void SetParentChecked(bool value)
        {
            if (parent == null)
                return;

            if (plugin.Mandatory == false && plugin.Selected != SelectedState.None)
            {
                if (value)
                    plugin.Selected = SelectedState.True;
                else
                    plugin.Selected = SelectedState.False;
            }

            OnPropertyUpdate("Checked");
            ParentCheckedPropagation();
        }

        public Visibility CheckBoxVisible
        {
            get
            {
                if (plugin.Selected == SelectedState.None)
                    return Visibility.Collapsed;
                else
                    return Visibility.Visible;
            }
        }

        public string Text { get => plugin.Title; }

        private Plugin plugin;
        public Plugin Plugin
        {
            get => plugin;
            set
            {
                plugin = value;
                OnPropertyUpdate("Plugin");
            }
        }

        private Control icon;
        public Control Icon
        {
            get => icon;
            set
            {
                icon = value;
                OnPropertyUpdate("Icon");
            }
        }

        private ObservableCollection<TreeViewModel> child = new ObservableCollection<TreeViewModel>();
        public ObservableCollection<TreeViewModel> Children { get => child; }

        public void SetIcon(Control icon)
        {
            this.icon = icon;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyUpdate(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public TreeViewModel FindPluginByFullPath(string fullPath)
        {
            return RecursiveFindPluginByFullPath(root, fullPath); 
        }

        private static TreeViewModel RecursiveFindPluginByFullPath(TreeViewModel cur, string fullPath)
        {
            if (cur.Plugin != null)
            {
                if (fullPath.Equals(cur.Plugin.FullPath, StringComparison.OrdinalIgnoreCase))
                    return cur;
            }

            if (0 < cur.Children.Count)
            {
                foreach (TreeViewModel next in cur.Children)
                {
                    TreeViewModel found = RecursiveFindPluginByFullPath(next, fullPath);
                    if (found != null)
                        return found;
                }
            }

            // Not found in this path
            return null;
        }

        private void DisablePlugins(TreeViewModel root, Plugin p)
        {
            if (root == null || p == null)
                return;

            List<string> paths = Plugin.GetDisablePluginPaths(p);

            if (paths != null)
            {
                foreach (string path in paths)
                {
                    string fullPath = p.Project.Variables.Expand(path);

                    Plugin pToDisable = p.Project.AllPluginList.FirstOrDefault(x => x.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
                    if (pToDisable != null)
                    {
                        Ini.SetKey(fullPath, "Main", "Selected", "False");
                        TreeViewModel found = FindPluginByFullPath(fullPath);
                        if (found != null)
                        {
                            if (p.Type != PluginType.Directory && p.Mandatory == false && p.Selected != SelectedState.None)
                            {
                                found.Checked = false;
                            }
                        }
                    }
                }
            }            
        }
    }
    #endregion
}