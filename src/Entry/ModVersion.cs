using System;
using System.Globalization;
using System.Reflection;

namespace FreeWorldMultiplayer
{
    // build.ps1 derives the loaded DLL's attributes from the repository's VERSION file.
    // Display the loaded assembly version so stale DLLs cannot advertise a newer package.
    internal static class ModVersion
    {
        private static readonly Version LoadedVersion = typeof(ModVersion).Assembly.GetName().Version
            ?? throw new InvalidOperationException("联机 MOD 程序集缺少版本信息。");

        internal static string Display => "V" + LoadedVersion.Major.ToString(CultureInfo.InvariantCulture)
            + "." + LoadedVersion.Minor.ToString(CultureInfo.InvariantCulture);
        internal static string Caption => "联机 " + Display;

        // Read the fingerprint embedded in the DLL that Unity actually loaded,
        // not the disk package or a potentially stale runtime BUILD-INFO file.
        // build.ps1 emits "V<MOD version>+<64 hex SourceRevision>" in this attribute.
        internal static string LoadedSourceRevision
        {
            get
            {
                object[] attributes = typeof(ModVersion).Assembly.GetCustomAttributes(
                    typeof(AssemblyInformationalVersionAttribute), false);
                if (attributes.Length != 1) return RuntimeSourceRevision.Unavailable;
                string version = ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
                return RuntimeSourceRevision.FromInformationalVersion(version);
            }
        }
    }
}
