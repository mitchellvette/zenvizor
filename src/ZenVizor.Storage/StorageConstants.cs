// SPDX-License-Identifier: GPL-3.0-or-later

namespace ZenVizor.Storage;

public static class StorageConstants
{
    /// <summary>
    /// The database file name under <c>%ProgramData%\ZenVizor\</c>.
    /// </summary>
    public const string DatabaseFileName = "zenvizor.db";

    /// <summary>
    /// Subdirectory under <c>%ProgramData%</c> that holds the DB and any
    /// service-owned data. ACL'd to SYSTEM + Administrators only.
    /// </summary>
    public const string DataDirectoryName = "ZenVizor";

    /// <summary>
    /// Dev/QA override for the data directory. When set to a non-empty path,
    /// <see cref="DefaultDataDirectory"/> returns it verbatim instead of the
    /// canonical <c>%ProgramData%\ZenVizor\</c> location. Intended only for
    /// pointing the service at a throwaway store (e.g. the marketing
    /// screenshot seed); production installs never set this. Inert unless set.
    /// </summary>
    public const string DataDirectoryEnvVar = "ZENVIZOR_DATA_DIR";

    public static string DefaultDataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DataDirectoryEnvVar);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                DataDirectoryName);
        }
    }

    public static string DefaultDatabasePath =>
        Path.Combine(DefaultDataDirectory, DatabaseFileName);
}
