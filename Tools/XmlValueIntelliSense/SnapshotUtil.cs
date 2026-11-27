using System;

using Microsoft.VisualStudio.Text;

namespace XmlValueIntelliSense
{
    internal static class SnapshotUtil
    {
        public const int MaxDocumentLength = 5 * 1024 * 1024;

        public const int HeaderLength = 64 * 1024;

        public static bool IsWithinLimit(ITextSnapshot snapshot)
        {
            return snapshot != null && snapshot.Length <= MaxDocumentLength;
        }

        public static string GetHeader(ITextSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Length == 0)
            {
                return "";
            }

            var length = Math.Min(snapshot.Length, HeaderLength);
            return snapshot.GetText(0, length);
        }
    }
}
