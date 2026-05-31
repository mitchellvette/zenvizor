namespace TitaniRun.Storage;

public static class StorageConstants
{
    /// <summary>
    /// The database file name under <c>%ProgramData%\TitaniRun\</c>.
    /// </summary>
    public const string DatabaseFileName = "titanirun.db";

    /// <summary>
    /// Subdirectory under <c>%ProgramData%</c> that holds the DB and any
    /// service-owned data. ACL'd to SYSTEM + Administrators only.
    /// </summary>
    public const string DataDirectoryName = "TitaniRun";

    public static string DefaultDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DataDirectoryName);

    public static string DefaultDatabasePath =>
        Path.Combine(DefaultDataDirectory, DatabaseFileName);
}
