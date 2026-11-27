using System;
using System.IO;

namespace XmlValueIntelliSense
{
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "XmlValueIntelliSense.log");

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        File.Delete(LogPath);
                    }

                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        public static void Error(Exception exception, string context)
        {
            if (exception == null)
            {
                return;
            }

            Write(context + "：" + exception.GetType().Name + "：" + exception.Message);
        }
    }
}
