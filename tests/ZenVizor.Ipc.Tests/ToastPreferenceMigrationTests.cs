// SPDX-License-Identifier: GPL-3.0-or-later

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ZenVizor.Service;
using ZenVizor.Storage;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Ipc.Tests;

/// <summary>
/// Epic B (1.2.0) — legacy-honouring per-severity toast preference
/// migration. Runs at every service start (fast-path when the three
/// per-severity keys already exist). Verifies each acceptance criterion
/// spelled out in the epic's Toggles-phase Acceptance section: fresh
/// install seeds Critical-only, 1.1.x master ON honours "all on", 1.1.x
/// master OFF honours "all off", partial state doesn't get overwritten.
/// </summary>
public sealed class ToastPreferenceMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly SettingsRepository _settings;

    public ToastPreferenceMigrationTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-toast-migration-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _settings = new SettingsRepository(_connections);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>
    /// Fresh install: the migration seed at 001_initial.sql sets
    /// <c>toast.on_alert = '1'</c> — that's the legacy row present on
    /// EVERY install, new and upgraded alike. The migration reads it
    /// and honours it. To test the true "fresh install with no legacy
    /// intent" case we have to clear that row first — which is the
    /// state after a Reset History wipe followed by re-migration, or
    /// on a hypothetical future install where 001 no longer seeds it.
    /// </summary>
    [Fact]
    public void FreshInstall_NoLegacyKey_SeedsCriticalOnly()
    {
        DeleteKey(SettingsRepository.Keys.ToastOnAlert);

        ZenVizorHostedService.MigrateToastPreferencesIfNeeded(_settings, NullLogger.Instance);

        _settings.GetBool(SettingsRepository.Keys.ToastOnCritical, false).Should().BeTrue();
        _settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  true).Should().BeFalse();
        _settings.GetBool(SettingsRepository.Keys.ToastOnInfo,     true).Should().BeFalse();
    }

    [Fact]
    public void LegacyMasterOn_HonoursIntent_AllThreeSeededOn()
    {
        // 1.1.x upgrade path — master is '1'. All three per-severity
        // keys seed to '1' so the user never silently loses toasts
        // they'd come to rely on.
        _settings.SetBool(SettingsRepository.Keys.ToastOnAlert, true);

        ZenVizorHostedService.MigrateToastPreferencesIfNeeded(_settings, NullLogger.Instance);

        _settings.GetBool(SettingsRepository.Keys.ToastOnCritical, false).Should().BeTrue();
        _settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  false).Should().BeTrue();
        _settings.GetBool(SettingsRepository.Keys.ToastOnInfo,     false).Should().BeTrue();
    }

    [Fact]
    public void LegacyMasterOff_HonoursIntent_AllThreeSeededOff()
    {
        _settings.SetBool(SettingsRepository.Keys.ToastOnAlert, false);

        ZenVizorHostedService.MigrateToastPreferencesIfNeeded(_settings, NullLogger.Instance);

        _settings.GetBool(SettingsRepository.Keys.ToastOnCritical, true).Should().BeFalse();
        _settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  true).Should().BeFalse();
        _settings.GetBool(SettingsRepository.Keys.ToastOnInfo,     true).Should().BeFalse();
    }

    [Fact]
    public void PartialState_OnlyMissingKeysSeeded_ExistingValuesPreserved()
    {
        // Simulate a user who set ToastOnCritical via zvctl before the
        // Warning / Info seeds landed. Migration must fill the two
        // missing keys and NOT overwrite the pre-existing Critical.
        _settings.SetBool(SettingsRepository.Keys.ToastOnCritical, false);
        // Legacy master ON — the seed source for the two missing keys.
        _settings.SetBool(SettingsRepository.Keys.ToastOnAlert, true);

        ZenVizorHostedService.MigrateToastPreferencesIfNeeded(_settings, NullLogger.Instance);

        _settings.GetBool(SettingsRepository.Keys.ToastOnCritical, true).Should().BeFalse(
            because: "pre-existing per-severity key must not be overwritten by the migration");
        _settings.GetBool(SettingsRepository.Keys.ToastOnWarning, false).Should().BeTrue();
        _settings.GetBool(SettingsRepository.Keys.ToastOnInfo,    false).Should().BeTrue();
    }

    [Fact]
    public void SecondCall_IsNoOp_DoesNotClobberExistingValues()
    {
        // Idempotence — the migration runs at every service start; a
        // subsequent start must leave user edits alone.
        ZenVizorHostedService.MigrateToastPreferencesIfNeeded(_settings, NullLogger.Instance);
        _settings.SetBool(SettingsRepository.Keys.ToastOnCritical, false);
        _settings.SetBool(SettingsRepository.Keys.ToastOnWarning,  true);

        ZenVizorHostedService.MigrateToastPreferencesIfNeeded(_settings, NullLogger.Instance);

        _settings.GetBool(SettingsRepository.Keys.ToastOnCritical, true).Should().BeFalse();
        _settings.GetBool(SettingsRepository.Keys.ToastOnWarning,  false).Should().BeTrue();
    }

    private void DeleteKey(string key)
    {
        using var connection = _connections.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
