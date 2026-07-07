using System;
using System.IO;
using System.Configuration;

namespace miPDFsign.Helpers
{
    /// <summary>
    /// Thread-safe file logger with optional rolling (new file each day).
    ///
    /// App.config Keys:
    ///   LogFile             – Target path (environment variables are expanded).
    ///                         Optional: {date} placeholder is replaced with the current date.
    ///   LogLevel            – Minimum level: DEBUG | INFO | WARN | ERROR  (default: INFO)
    ///   LogRolling          – true | false  – new file each day  (default: false)
    ///   LogFileDatePattern  – Date format in the file name  (default: yyyy-MM-dd)
    ///   LogFileDatePosition – suffix | prefix  (default: suffix)
    ///                         Ignored when {date} is contained in LogFile.
    ///   LogMaxDays          – Delete old files after N days; 0 = never  (default: 0)
    /// </summary>
    public static class AppLogger
    {
        // ── Log level ────────────────────────────────────────────────────
        public enum Level { Debug = 0, Info = 1, Warn = 2, Error = 3 }

        // ── Configuration ────────────────────────────────────────────────
        private static string _basePathRaw  = "";   // Path from App.config (unexpanded)
        private static Level  _minLevel     = Level.Info;
        private static bool   _rolling      = false;
        private static string _datePattern  = "yyyy-MM-dd";
        private static bool   _dateAsPrefix = false;
        private static int    _maxDays      = 0;

        // ── Rolling state ────────────────────────────────────────────────
        private static string _currentFilePath = "";
        private static string _currentDateKey  = "";   // last seen date
        private static readonly object _lock = new();

        // ── Initialization ──────────────────────────────────────────────

        /// <summary>
        /// Reads the configuration from App.config and enables logging.
        /// Must be called once at app startup.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                _basePathRaw = ConfigurationManager.AppSettings["LogFile"]?.Trim() ?? "";

                if (Enum.TryParse<Level>(
                        ConfigurationManager.AppSettings["LogLevel"]?.Trim(),
                        ignoreCase: true, out var lvl))
                    _minLevel = lvl;

                _rolling = string.Equals(
                    ConfigurationManager.AppSettings["LogRolling"]?.Trim(),
                    "true", StringComparison.OrdinalIgnoreCase);

                _datePattern = ConfigurationManager.AppSettings["LogFileDatePattern"]?.Trim()
                               ?? "yyyy-MM-dd";
                if (string.IsNullOrEmpty(_datePattern))
                    _datePattern = "yyyy-MM-dd";

                _dateAsPrefix = string.Equals(
                    ConfigurationManager.AppSettings["LogFileDatePosition"]?.Trim(),
                    "prefix", StringComparison.OrdinalIgnoreCase);

                if (int.TryParse(
                        ConfigurationManager.AppSettings["LogMaxDays"]?.Trim(),
                        out int md) && md > 0)
                    _maxDays = md;
            }
            catch
            {
                // Config error → silent fallback to defaults (no crash)
            }

            Info("AppLogger initialized");
        }

        // ── Public log methods ─────────────────────────────────────

        public static void Debug(string message)  => Log(Level.Debug, message, null);
        public static void Info(string message)   => Log(Level.Info,  message, null);
        public static void Warn(string message)   => Log(Level.Warn,  message, null);
        public static void Error(string message, Exception? ex = null)
            => Log(Level.Error, message, ex);

        // ── Internal implementation ──────────────────────────────────────

        private static void Log(Level level, string message, Exception? ex)
        {
            if (level < _minLevel) return;
            if (string.IsNullOrEmpty(_basePathRaw)) return;

            string line = FormatLine(level, message, ex);

            lock (_lock)
            {
                try
                {
                    string path = ResolvePath();
                    if (string.IsNullOrEmpty(path)) return;

                    File.AppendAllText(path, line + Environment.NewLine,
                        System.Text.Encoding.UTF8);

                    if (_maxDays > 0)
                        PurgeOldFiles(path);
                }
                catch
                {
                    // Logging must never crash the application
                }
            }
        }

        private static string FormatLine(Level level, string message, Exception? ex)
        {
            string ts    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string lvl   = level.ToString().ToUpperInvariant().PadRight(5);
            string text  = $"{ts} [{lvl}] {message}";
            if (ex != null)
                text += Environment.NewLine + "  Exception: " + ex;
            return text;
        }

        private static string ResolvePath()
        {
            string expanded = Environment.ExpandEnvironmentVariables(_basePathRaw);

            if (expanded.Contains("{date}", StringComparison.OrdinalIgnoreCase))
            {
                // Explicit {date} placeholder in the path
                string dated = expanded.Replace("{date}",
                    DateTime.Now.ToString(_datePattern),
                    StringComparison.OrdinalIgnoreCase);
                EnsureDir(dated);
                _currentFilePath = dated;
                return dated;
            }

            if (!_rolling)
            {
                // No rolling – always the same file
                if (string.IsNullOrEmpty(_currentFilePath))
                {
                    EnsureDir(expanded);
                    _currentFilePath = expanded;
                }
                return _currentFilePath;
            }

            // Rolling: a new file each day
            string dateKey = DateTime.Now.ToString(_datePattern);
            if (_currentDateKey == dateKey && !string.IsNullOrEmpty(_currentFilePath))
                return _currentFilePath;

            // Date has changed → compute new path
            _currentDateKey = dateKey;
            string dir  = Path.GetDirectoryName(expanded) ?? ".";
            string name = Path.GetFileNameWithoutExtension(expanded);
            string ext  = Path.GetExtension(expanded);

            string newPath = _dateAsPrefix
                ? Path.Combine(dir, dateKey + "_" + name + ext)
                : Path.Combine(dir, name + "_" + dateKey + ext);

            EnsureDir(newPath);
            _currentFilePath = newPath;
            return newPath;
        }

        private static void EnsureDir(string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        private static void PurgeOldFiles(string currentPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                string pattern = Path.GetFileNameWithoutExtension(
                    Environment.ExpandEnvironmentVariables(_basePathRaw)) + "*"
                    + Path.GetExtension(
                    Environment.ExpandEnvironmentVariables(_basePathRaw));

                var cutoff = DateTime.Now.AddDays(-_maxDays);
                foreach (string f in Directory.GetFiles(dir, pattern))
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
                }
            }
            catch { /* ignore purge errors */ }
        }
    }
}
