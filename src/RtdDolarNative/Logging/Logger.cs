using System;
using System.IO;

namespace RtdDolarNative.Logging
{
    public sealed class Logger
    {
        private readonly object _lock = new object();
        private readonly string _path;

        public Logger(string path)
        {
            _path = path;

            if (!string.IsNullOrWhiteSpace(_path))
            {
                string directory = Path.GetDirectoryName(_path);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        public void Info(string message)
        {
            Write("INFO", message, null);
        }

        public void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        public void Debug(string message)
        {
            Write("DEBUG", message, null);
        }

        private void Write(string level, string message, Exception exception)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;

            if (exception != null)
            {
                line += " | " + exception.GetType().Name + ": " + exception.Message;

                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    line += Environment.NewLine + exception.StackTrace;
                }
            }

            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(_path))
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
        }
    }
}
