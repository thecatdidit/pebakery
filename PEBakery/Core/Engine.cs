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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using PEBakery.Helper;
using PEBakery.Exceptions;
using PEBakery.Core.Commands;
using PEBakery.WPF;
using System.ComponentModel;
using PEBakery.Lib;
using System.Threading;

namespace PEBakery.Core
{
    public class Engine
    {
        public static Engine WorkingEngine; // Only 1 Instance can run at one time
        public static int WorkingLock;

        public EngineState s;
        private Task<long> task;

        public Engine(EngineState state)
        {
            s = state;
        }

        /// <summary>
        /// Ready to run an plugin
        /// </summary>
        private void ReadyRunPlugin(long buildId, Plugin p = null)
        {
            // Turn off System,ErrorOff
            s.Logger.ErrorOffCount = 0;
            // Turn off System,Log,Off
            s.Logger.SuspendLog = false;

            // Set CurrentPlugin
            // Note: s.CurrentPluginIdx is not touched here
            if (p == null)
                p = s.CurrentPlugin;
            else
                s.CurrentPlugin = p;

            // Init Per-Plugin Log
            s.PluginId = s.Logger.Build_Plugin_Init(buildId, s.CurrentPlugin, s.CurrentPluginIdx + 1);

            // Log Plugin Build Start Message
            string msg;
            if (s.RunOnePlugin && s.EntrySectionName.Equals("Process", StringComparison.OrdinalIgnoreCase) == false)
                msg = $"Processing section [{s.EntrySectionName}] of plugin [{p.ShortPath}] ({s.CurrentPluginIdx + 1}/{s.Plugins.Count})";
            else
                msg = $"Processing plugin [{p.ShortPath}] ({s.CurrentPluginIdx + 1}/{s.Plugins.Count})";
            s.Logger.Build_Write(s, msg);
            s.Logger.Build_Write(s, Logger.LogSeperator);

            // Load Default Per-Plugin Variables
            s.Variables.ResetVariables(VarsType.Local);
            s.Logger.Build_Write(s, s.Variables.LoadDefaultPluginVariables(s.CurrentPlugin));

            // Current Section Parameter - empty
            s.CurSectionParams = new Dictionary<int, string>();

            // Set Interface using MainWindow, MainViewModel
            s.MainViewModel.PluginTitleText = $"({s.CurrentPluginIdx + 1}/{s.Plugins.Count}) {StringEscaper.Unescape(p.Title)}";
            s.MainViewModel.PluginDescriptionText = StringEscaper.Unescape(p.Description);
            s.MainViewModel.PluginVersionText = $"v{p.Version}";
            s.MainViewModel.PluginAuthorText = p.Author;
            s.MainViewModel.BuildEchoMessage = $"Processing Section [{s.EntrySectionName}]...";

            long allLineCount = 0;
            foreach (var kv in s.CurrentPlugin.Sections.Where(x => x.Value.Type == SectionType.Code))
                allLineCount += kv.Value.Lines.Count;

            s.MainViewModel.BuildPluginProgressBarMax = allLineCount;
            s.MainViewModel.BuildPluginProgressBarValue = 0;
            s.MainViewModel.BuildFullProgressBarValue = s.CurrentPluginIdx;

            Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                MainWindow w = Application.Current.MainWindow as MainWindow;
                if (w.CurBuildTree != null)
                    w.CurBuildTree.BuildFocus = false;
                w.CurBuildTree = s.MainViewModel.BuildTree.FindPluginByFullPath(s.CurrentPlugin.FullPath);
                w.CurBuildTree.BuildFocus = true;
            }));
        }

        private void FinishRunPlugin(long pluginId)
        {
            // Finish Per-Plugin Log
            s.Logger.Build_Plugin_Finish(pluginId);
        }

        public Task<long> Run(string runName)
        {
            task = Task.Run(() =>
            {
                s.BuildId = s.Logger.Build_Init(runName, s);

                s.MainViewModel.BuildFullProgressBarMax = s.Plugins.Count;

                while (true)
                {
                    ReadyRunPlugin(s.BuildId);

                    // Run Main Section
                    PluginSection mainSection = s.CurrentPlugin.Sections[s.EntrySectionName];
                    SectionAddress addr = new SectionAddress(s.CurrentPlugin, mainSection);
                    s.Logger.LogStartOfSection(s, addr, 0, true, null);
                    Engine.RunSection(s, new SectionAddress(s.CurrentPlugin, mainSection), new List<string>(), 1, false);
                    s.Logger.LogEndOfSection(s, addr, 0, true, null);

                    // End of Plugin
                    s.Logger.Build_Write(s, $"End of plugin [{s.CurrentPlugin.ShortPath}]");
                    s.Logger.Build_Write(s, Logger.LogSeperator);

                    // OnBuildExit event callback
                    Engine.CheckAndRunCallback(s, ref s.OnPluginExit, "OnPluginExit");

                    if (s.Plugins.Count - 1 <= s.CurrentPluginIdx ||
                        s.RunOnePlugin || s.ErrorHaltFlag || s.UserHaltFlag)
                    { // End of Build
                        if (s.UserHaltFlag)
                        {
                            s.Logger.Build_Write(s, Logger.LogSeperator);
                            s.Logger.Build_Write(s, new LogInfo(LogState.Info, "Build stopped by user"));
                            MessageBox.Show("Build stopped by user", "Build Halt", MessageBoxButton.OK, MessageBoxImage.Information);
                        }

                        Engine.CheckAndRunCallback(s, ref s.OnBuildExit, "OnBuildExit");
                        break;
                    }
                    s.Logger.Build_Write(s, string.Empty);

                    // Run Next Plugin
                    FinishRunPlugin(s.PluginId);
                    s.CurrentPluginIdx += 1;
                    s.CurrentPlugin = s.Plugins[s.CurrentPluginIdx];
                    s.PassCurrentPluginFlag = false;
                }

                s.Logger.Build_Finish(s.BuildId);

                return s.BuildId;
            });

            return task;
        }

        public void ForceStop()
        {
            s.UserHaltFlag = true;
            task.Wait();
        }

        public static void RunSection(EngineState s, SectionAddress addr, List<string> sectionParams, int depth, bool callback)
        {
            Dictionary<int, string> paramDict = new Dictionary<int, string>();
            for (int i = 0; i < sectionParams.Count; i++)
                paramDict[i + 1] = sectionParams[i];
            RunSection(s, addr, paramDict, depth, callback);
        }

        public static void RunSection(EngineState s, SectionAddress addr, Dictionary<int, string> paramDict, int depth, bool callback)
        {
            List<CodeCommand> codes = addr.Section.GetCodes(true);
            s.Logger.Build_Write(s, LogInfo.AddDepth(addr.Section.LogInfos, s.CurDepth + 1));

            RunCommands(s, addr, codes, paramDict, depth, callback);

            s.MainViewModel.BuildPluginProgressBarValue += addr.Section.Lines.Count;
        }
       
        public static void RunCommands(EngineState s, SectionAddress addr, List<CodeCommand> codes, Dictionary<int, string> sectionParams, int depth, bool callback = false)
        {
            if (codes.Count == 0)
            {
                s.Logger.Build_Write(s, new LogInfo(LogState.Error, $"Section [{addr.Section.SectionName}] does not have codes", s.CurDepth));
            }

            CodeCommand curCommand = codes[0];
            for (int idx = 0; idx < codes.Count; idx++)
            {
                try
                {
                    curCommand = codes[idx];
                    s.CurDepth = depth;
                    s.CurSectionParams = sectionParams;
                    ExecuteCommand(s, curCommand);

                    if (s.PassCurrentPluginFlag || s.ErrorHaltFlag || s.UserHaltFlag)
                        break;
                }
                catch (CriticalErrorException)
                { // Critical Error, stop build
                    break;
                }
            }
        }

        private static void CheckAndRunCallback(EngineState s, ref CodeCommand cbCmd, string eventName)
        {
            if (cbCmd != null)
            {
                s.Logger.Build_Write(s, $"Processing callback of event [{eventName}]");

                if (cbCmd.Type == CodeType.Run || cbCmd.Type == CodeType.Exec)
                {
                    s.CurDepth = -1;
                    CommandBranch.RunExec(s, cbCmd, false, false, true);
                }
                else
                {
                    s.CurDepth = 0;
                    ExecuteCommand(s, cbCmd);
                }
                s.Logger.Build_Write(s, new LogInfo(LogState.Info, $"End of callback [{eventName}]", s.CurDepth));
                s.Logger.Build_Write(s, Logger.LogSeperator);
                cbCmd = null;
            }
        }

        private static void ExecuteCommand(EngineState s, CodeCommand cmd)
        {
            List<LogInfo> logs = new List<LogInfo>();
            int curDepth = s.CurDepth;

            if (CodeCommand.DeprecatedCodeType.Contains(cmd.Type))
            {
                logs.Add(new LogInfo(LogState.Warning, $"Command [{cmd.Type}] is deprecated"));
            }

            s.MainViewModel.BuildCommandProgressBarValue = 0;

            try
            {
                switch (cmd.Type)
                {
                    #region 00 Misc
                    // 00 Misc
                    case CodeType.None:
                        logs.Add(new LogInfo(LogState.Ignore, string.Empty));
                        break;
                    case CodeType.Comment:
                        if (s.LogComment)
                            logs.Add(new LogInfo(LogState.Ignore, string.Empty));
                        break;
                    case CodeType.Error:
                        logs.Add(new LogInfo(LogState.Error, string.Empty));
                        break;
                    case CodeType.Unknown:
                        logs.Add(new LogInfo(LogState.Ignore, string.Empty));
                        break;
                    #endregion
                    #region 01 File
                    // 01 File
                    //case CodeType.CopyOrExpand:
                    //    break;
                    //case CodeType.DirCopy:
                    //   break;
                    //case CodeType.DirDelete:
                    //    break;
                    //case CodeType.DirMove:
                    //    break;
                    case CodeType.DirMake:
                        logs.AddRange(CommandFile.DirMake(s, cmd));
                        break;
                    //case CodeType.Expand:
                    //    break;
                    case CodeType.FileCopy:
                        logs.AddRange(CommandFile.FileCopy(s, cmd));
                        break;
                    //case CodeType.FileDelete:
                    //    break;
                    //case CodeType.FileRename:
                    //case CodeType.FileMove:
                    //    break;
                    case CodeType.FileCreateBlank:
                        logs.AddRange(CommandFile.FileCreateBlank(s, cmd));
                        break;
                    //case CodeType.FileByteExtract:
                    //    break;
                    #endregion
                    #region 02 Registry
                    // 02 Registry
                    //case CodeType.RegHiveLoad:
                    //    break;
                    //case CodeType.RegHiveUnload:
                    //    break;
                    //case CodeType.RegImport:
                    //    break;
                    //case CodeType.RegWrite:
                    //    break;
                    //case CodeType.RegRead:
                    //    break;
                    //case CodeType.RegDelete:
                    //    break;
                    //case CodeType.RegWriteBin:
                    //    break;
                    //case CodeType.RegReadBin:
                    //    break;
                    //case CodeType.RegMulti:
                    //   break;
                    #endregion
                    #region 03 Text
                    // 03 Text
                    case CodeType.TXTAddLine:
                        logs.AddRange(CommandText.TXTAddLine(s, cmd));
                        break;
                    case CodeType.TXTAddLineOp:
                        logs.AddRange(CommandText.TXTAddLineOp(s, cmd));
                        break;
                    case CodeType.TXTReplace:
                        logs.AddRange(CommandText.TXTReplace(s, cmd));
                        break;
                    case CodeType.TXTDelLine:
                        logs.AddRange(CommandText.TXTDelLine(s, cmd));
                        break;
                    case CodeType.TXTDelSpaces:
                        logs.AddRange(CommandText.TXTDelSpaces(s, cmd));
                        break;
                    case CodeType.TXTDelEmptyLines:
                        logs.AddRange(CommandText.TXTDelEmptyLines(s, cmd));
                        break;
                    #endregion
                    #region 04 INI
                    // 04 INI
                    case CodeType.INIRead:
                        logs.AddRange(CommandINI.INIRead(s, cmd));
                        break;
                    case CodeType.INIWrite:
                        logs.AddRange(CommandINI.INIWrite(s, cmd));
                        break;
                    case CodeType.INIDelete:
                        logs.AddRange(CommandINI.INIDelete(s, cmd));
                        break;
                    case CodeType.INIAddSection:
                        logs.AddRange(CommandINI.INIAddSection(s, cmd));
                        break;
                    case CodeType.INIDeleteSection:
                        logs.AddRange(CommandINI.INIDeleteSection(s, cmd));
                        break;
                    //case CodeType.INIWriteTextLine:
                    //    break;
                    //case CodeType.INIMerge:
                    //    break;
                    #endregion
                    #region 05 Compress
                    // case CodeType.Compress:
                    //     break;
                    // case CodeType.Decompress:
                    //     break;
                    #endregion
                    #region 06 Network
                    //case CodeType.WebGet:
                    //    break;
                    //case CodeType.WebGetIfNotExist: // Deprecated
                    //    break;
                    #endregion
                    #region 07 Attach
                    // 07 Attach
                    case CodeType.ExtractFile:
                        logs.AddRange(CommandPlugin.ExtractFile(s, cmd));
                        break;
                    //case CodeType.ExtractAndRun:
                    //    break;
                    //case CodeType.ExtractAllFiles:
                    //    break;
                    //case CodeType.Encode:
                    //    break;
                    #endregion
                    #region 08 Interface
                    case CodeType.Visible:
                        logs.AddRange(CommandInterface.Visible(s, cmd));
                        break;
                    case CodeType.VisibleOp:
                        logs.AddRange(CommandInterface.VisibleOp(s, cmd));
                        break;
                    #endregion
                    #region 09 UI
                    case CodeType.Message:
                        logs.AddRange(CommandUI.Message(s, cmd));
                        break;
                    case CodeType.Echo:
                        logs.AddRange(CommandUI.Echo(s, cmd));
                        break;
                    //case CodeType.Retrieve:
                    //   break;
                    #endregion
                    #region 10 StringFormat
                    case CodeType.StrFormat:
                        logs.AddRange(CommandString.StrFormat(s, cmd));
                        break;
                    #endregion
                    #region 11 System
                    // case CodeType.System:
                    //    break;
                    case CodeType.ShellExecute:
                    case CodeType.ShellExecuteEx:
                    case CodeType.ShellExecuteDelete:
                        logs.AddRange(CommandSystem.ShellExecute(s, cmd));
                        break;
                    #endregion
                    #region 12 Branch
                    case CodeType.Run:
                    case CodeType.Exec:
                        CommandBranch.RunExec(s, cmd);
                        break;
                    case CodeType.Loop:
                        CommandBranch.Loop(s, cmd);
                        break;
                    case CodeType.If:
                        CommandBranch.If(s, cmd);
                        break;
                    case CodeType.Else:
                        CommandBranch.Else(s, cmd);
                        break;
                    case CodeType.Begin:
                        throw new InternalParserException("CodeParser Error");
                    case CodeType.End:
                        throw new InternalParserException("CodeParser Error");
                    #endregion
                    #region 13 Control
                    case CodeType.Set:
                        logs.AddRange(CommandControl.Set(s, cmd));
                        break;
                    case CodeType.GetParam:
                        logs.AddRange(CommandControl.GetParam(s, cmd));
                        break;
                    case CodeType.PackParam:
                        logs.AddRange(CommandControl.PackParam(s, cmd));
                        break;
                    //case CodeType.AddVariables:
                    //    break;
                    case CodeType.Exit:
                        logs.AddRange(CommandControl.Exit(s, cmd));
                        break;
                    case CodeType.Halt:
                        logs.AddRange(CommandControl.Halt(s, cmd));
                        break;
                    case CodeType.Wait:
                        logs.AddRange(CommandControl.Wait(s, cmd));
                        break;
                    case CodeType.Beep:
                        logs.AddRange(CommandControl.Beep(s, cmd));
                        break;
                    #endregion
                    #region 14 External Macro
                    case CodeType.Macro:
                        CommandMacro.Macro(s, cmd);
                        break;
                    #endregion
                    #region Error
                    // Error
                    default:
                        throw new ExecuteException($"Cannot execute [{cmd.Type}] command");
                        #endregion
                }
            }
            catch (CriticalErrorException)
            { // Stop Building
                throw new CriticalErrorException();
            }
            catch (InvalidCodeCommandException e)
            {
                logs.Add(new LogInfo(LogState.Error, e, e.Cmd, curDepth));
            }
            catch (Exception e)
            {
                logs.Add(new LogInfo(LogState.Error, e, cmd, curDepth));
            }

            s.Logger.Build_Write(s, LogInfo.AddCommandDepth(logs, cmd, curDepth));

            s.MainViewModel.BuildCommandProgressBarValue = 1000;
        }
    }

    public class EngineState
    {
        // Fields used globally
        public Project Project;
        public List<Plugin> Plugins;
        public Variables Variables { get => Project.Variables; }
        public Macro Macro;
        public Logger Logger;
        public bool RunOnePlugin;
        public MainViewModel MainViewModel;

        // Properties
        public string BaseDir { get => Project.BaseDir; }
        public Plugin MainPlugin { get => Project.MainPlugin; }

        // Fields : Engine's state
        public Plugin CurrentPlugin;
        public int CurrentPluginIdx;
        public Dictionary<int, string> CurSectionParams = new Dictionary<int, string>();
        public int CurDepth;
        public bool ElseFlag = false;
        public bool LoopRunning = false;
        public long LoopCounter;
        public bool PassCurrentPluginFlag = false;
        public bool ErrorHaltFlag = false;
        public bool UserHaltFlag = false;
        public long BuildId; // Used in logging
        public long PluginId; // Used in logging
        public bool LogComment = true; // Used in logging
        public bool LogMacro = true; // Used in logging

        // Fields : System Commands
        public CodeCommand OnBuildExit = null;
        public CodeCommand OnPluginExit = null;

        // Readonly Fields
        public readonly string EntrySectionName;

        public EngineState(Project project, Logger logger, Plugin pluginToRun = null, string entrySectionName = "Process")
        {
            this.Project = project;
            this.Logger = logger;

            Macro = new Macro(Project, Variables, out List<LogInfo> macroLogs);
            logger.Build_Write(BuildId, macroLogs);

            if (pluginToRun == null) // Run just plugin
            {
                // Why List -> Tree -> List? To sort.
                Plugins = new List<Plugin>();
                Tree<Plugin> tree = project.GetActivePlugin();
                foreach (Plugin p in tree)
                    Plugins.Add(p);

                CurrentPlugin = Plugins[0]; // Main Plugin
                CurrentPluginIdx = 0;

                RunOnePlugin = false;
            }
            else
            {
                Plugins = new List<Plugin>() { pluginToRun };

                CurrentPlugin = pluginToRun;
                CurrentPluginIdx = Plugins.IndexOf(pluginToRun);

                RunOnePlugin = true;
            }

            EntrySectionName = entrySectionName;

            Application.Current.Dispatcher.Invoke((Action)(() =>
            {
                MainWindow w = Application.Current.MainWindow as MainWindow;
                MainViewModel = w.Model;
            }));
        }

        public void SetLogOption(SettingViewModel m)
        {
            LogComment = m.Log_Comment;
            LogMacro = m.Log_Macro;
        }

        public void SetLogOption(bool logComment, bool logMacro)
        {
            LogComment = logComment;
            LogMacro = logMacro;
        }
    }
}