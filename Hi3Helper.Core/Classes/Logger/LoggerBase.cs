﻿using System;
using System.IO;
using System.Text;
#if !APPLYUPDATE
using System.Diagnostics;
using System.Linq;
using Hi3Helper.Shared.Region;
#endif

namespace Hi3Helper
{
    public class LoggerBase
    {
        #region Properties
        public static string CurrentLauncherVersion = "vUnknown";
        private FileStream _logStream { get; set; }
        private StreamWriter _logWriter { get; set; }
        private string _logFolder { get; set; }
#if !APPLYUPDATE
        private string _logPath { get; set; }
#endif
        private StringBuilder _stringBuilder { get; set; }
        #endregion

        #region Statics
        public static string GetCurrentTime(string format) => DateTime.Now.ToLocalTime().ToString(format);
        #endregion

        public LoggerBase(string logFolder, Encoding logEncoding)
        {
            // Initialize the writer and _stringBuilder
            _stringBuilder = new StringBuilder();
            SetFolderPathAndInitialize(logFolder, logEncoding);
        }

        #region Methods
        public void SetFolderPathAndInitialize(string folderPath, Encoding logEncoding)
        {
            // Set the folder path of the stored log
            _logFolder = folderPath;

#if !APPLYUPDATE
            // Check if the directory exist. If not, then create.
            if (!Directory.Exists(_logFolder))
            {
                Directory.CreateDirectory(_logFolder);
            }
#endif

            // Try dispose the _logWriter even though it's not initialized.
            // This will be used if the program need to change the log folder to another location.
            _logWriter?.Dispose();
            _logStream?.Dispose();

#if !APPLYUPDATE
            try
            {
                // Initialize writer and the path of the log file.
                InitializeWriter(false, logEncoding);
            }
            catch
            {
                // If the initialization above fails, then use fallback.
                InitializeWriter(true, logEncoding);
            }
#endif
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public virtual async void LogWriteLine() { }
        // ReSharper disable MethodOverloadWithOptionalParameter
        public virtual async void LogWriteLine(string line = null) { }
        // ReSharper restore MethodOverloadWithOptionalParameter
        public virtual async void LogWriteLine(string line, LogType type) { }
        public virtual async void LogWriteLine(string line, LogType type, bool writeToLog) { }
        public virtual async void LogWrite(string line, LogType type, bool writeToLog, bool fromStart) { }
        public async void WriteLog(string line, LogType type)
        {
            // Always seek to the end of the file.
            _logWriter?.BaseStream.Seek(0, SeekOrigin.End);
            _logWriter?.WriteLine(GetLine(line, type, false, true));
        }
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
#endregion

        #region ProtectedMethods
        /// <summary>
        /// Get the line for displaying or writing into the log based on their type
        /// </summary>
        /// <param name="line">Line for the log you want to return</param>
        /// <param name="type">Type of the log. The type will be added in the return</param>
        /// <param name="coloredType">Whether to colorize the type string, typically used for displaying</param>
        /// <param name="withTimeStamp">Whether to append a timestamp after log type</param>
        /// <returns>Decorated line with colored type or timestamp according to the parameters</returns>
        protected string GetLine(string line, LogType type, bool coloredType, bool withTimeStamp)
        {
            lock (_stringBuilder)
            {
                // Clear the _stringBuilder
                _stringBuilder.Clear();

                // Colorize the log type
                if (coloredType)
                {
                    _stringBuilder.Append(GetColorizedString(type) + GetLabelString(type) + "\u001b[0m");
                }
                else
                {
                    _stringBuilder.Append(GetLabelString(type));
                }

                // Append timestamp
                if (withTimeStamp)
                {
                    if (type != LogType.NoTag)
                    {
                        _stringBuilder.Append($" [{GetCurrentTime("HH:mm:ss.fff")}]");
                    }
                    else
                    {
                        _stringBuilder.Append(new string(' ', 15));
                    }
                }

                // Append spaces between labels and text
                _stringBuilder.Append("  ");
                _stringBuilder.Append(line);

                return _stringBuilder.ToString();
            }
        }

        protected void DisposeBase()
        {
            _logWriter?.Dispose();
            _logStream?.Dispose();
        }
        #endregion

        #region PrivateMethods
#if !APPLYUPDATE
        private void InitializeWriter(bool isFallback, Encoding logEncoding)
        {
            // Initialize _logPath and get fallback string at the end of the filename if true or none if false.
            string fallbackString = isFallback ? ("-f" + Path.GetFileNameWithoutExtension(Path.GetTempFileName())) : string.Empty;
            string dateString = GetCurrentTime("yyyy-MM-dd");
            // Append the build name
            fallbackString += LauncherConfig.IsPreview ? "-pre" : "-sta";
            // Append current app version
            fallbackString += CurrentLauncherVersion;
            // Append the current instance number
            fallbackString += $"-id{GetTotalInstance()}";
            _logPath = Path.Combine(_logFolder, $"log-{dateString + fallbackString}.log");

            // Initialize _logWriter to the given _logPath.
            // The FileShare.ReadWrite is still being used to avoid potential conflict if the launcher needs
            // to warm-restart itself in rare occassion (like update mechanism with Squirrel).
            _logStream = new FileStream(_logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            _logWriter = new StreamWriter(_logStream, logEncoding, -1, false) { AutoFlush = true };
        }

        private int GetTotalInstance()
        {
            // Get this app's process name
            string currentProcName = Path.GetFileNameWithoutExtension(LauncherConfig.AppExecutablePath);

            // Get the process count
            int procCount = Process.GetProcesses()
                .Where(x => x.ProcessName == currentProcName)
                .Select(x => x.ProcessName).Count();

            // If the procCount > 0, then procCount - 1. Else, 0
            return procCount > 0 ? procCount - 1 : 0;
        }
#endif

        private ArgumentException ThrowInvalidType() => new ArgumentException("Type must be Default, Error, Warning, Scheme, Game, NoTag or Empty!");

        /// <summary>
        /// Get the ASCII color in string form.
        /// </summary>
        /// <param name="type">The type of the log</param>
        /// <returns>A string of the ASCII color</returns>
        private string GetColorizedString(LogType type) => type switch
        {
            LogType.Default => "\u001b[32;1m",
            LogType.Error => "\u001b[31;1m",
            LogType.Warning => "\u001b[33;1m",
            LogType.Scheme => "\u001b[34;1m",
            LogType.Game => "\u001b[35;1m",
            LogType.Debug => "\u001b[36;1m",
            LogType.GLC => "\u001b[91;1m",
            _ => string.Empty
        };

        /// <summary>
        /// Get the label string based on log type.
        /// </summary>
        /// <param name="type">The type of the log</param>
        /// <returns>A string of the label based on type</returns>
        /// <exception cref="ArgumentException"></exception>
        private string GetLabelString(LogType type) => type switch
        {
            LogType.Default => "[Info]  ",
            LogType.Error   => "[Erro]  ",
            LogType.Warning => "[Warn]  ",
            LogType.Scheme  => "[Schm]  ",
            LogType.Game    => "[Game]  ",
            LogType.Debug   => "[DBG]   ",
            LogType.GLC     => "[GLC]   ",
            LogType.NoTag   => "      ",
            _ => throw ThrowInvalidType()
        };
#endregion
    }
}
