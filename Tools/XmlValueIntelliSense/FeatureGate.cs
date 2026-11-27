using System;
using System.IO;

namespace XmlValueIntelliSense
{
    internal static class FeatureGate
    {
        private static readonly bool Disabled = ComputeDisabled();

        private static readonly bool VerboseEnabled = ComputeVerbose();

        public static bool Enabled
        {
            get { return !Disabled; }
        }

        public static bool Verbose
        {
            get { return VerboseEnabled; }
        }

        private static bool ComputeDisabled()
        {
            try
            {
                var flag = Environment.GetEnvironmentVariable("XMLVALUEINTELLISENSE_DISABLE");
                if (string.Equals(flag, "1", StringComparison.Ordinal)
                    || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return File.Exists(Path.Combine(Path.GetTempPath(), "XmlValueIntelliSense.disable"));
            }
            catch
            {
                return false;
            }
        }

        private static bool ComputeVerbose()
        {
            try
            {
                var flag = Environment.GetEnvironmentVariable("XMLVALUEINTELLISENSE_VERBOSE");
                return string.Equals(flag, "1", StringComparison.Ordinal)
                    || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
