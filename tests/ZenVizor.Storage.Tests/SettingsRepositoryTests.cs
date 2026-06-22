using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 6.2 — typed Get/Set round-trip + default-on-missing behaviour.
/// Each test gets a fresh migrated database so seeded defaults from
/// 001_initial.sql + 003_phase6_settings.sql are present.
/// </summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly SettingsRepository _settings;

    public SettingsRepositoryTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-settings-{Guid.NewGuid():N}.db");
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

    [Fact]
    public void GetString_SeededRow_ReturnsValue()
    {
        // autostart.mode is seeded by 003_phase6_settings.sql.
        _settings.GetString(SettingsRepository.Keys.AutostartMode)
            .Should().Be("Automatic");
    }

    [Fact]
    public void GetString_MissingKey_ReturnsNull()
    {
        _settings.GetString("nonexistent.key").Should().BeNull();
    }

    [Fact]
    public void GetInt_SeededRetentionRow_ReturnsParsed()
    {
        _settings.GetInt(SettingsRepository.Keys.DailyDays, defaultValue: 999)
            .Should().Be(365);
    }

    [Fact]
    public void GetInt_MissingKey_ReturnsDefault()
    {
        _settings.GetInt("nonexistent.int", defaultValue: 42)
            .Should().Be(42);
    }

    [Fact]
    public void GetBool_SeededTrueRow_ReturnsTrue()
    {
        // toast.on_alert is seeded to '1'.
        _settings.GetBool(SettingsRepository.Keys.ToastOnAlert, defaultValue: false)
            .Should().BeTrue();
    }

    [Fact]
    public void GetBool_MissingKey_ReturnsDefault()
    {
        _settings.GetBool("nonexistent.bool", defaultValue: true).Should().BeTrue();
        _settings.GetBool("nonexistent.bool", defaultValue: false).Should().BeFalse();
    }

    [Fact]
    public void Set_NewKey_PersistsAndReadsBack()
    {
        _settings.Set("custom.key", "hello");
        _settings.GetString("custom.key").Should().Be("hello");
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValue()
    {
        _settings.Set(SettingsRepository.Keys.DailyDays, "180");
        _settings.GetInt(SettingsRepository.Keys.DailyDays, 0).Should().Be(180);
    }

    [Fact]
    public void SetInt_ThenGetInt_RoundTrips()
    {
        _settings.SetInt(SettingsRepository.Keys.SamplesDays, 7);
        _settings.GetInt(SettingsRepository.Keys.SamplesDays, 99).Should().Be(7);
    }

    [Fact]
    public void SetBool_True_StoresOne()
    {
        _settings.SetBool(SettingsRepository.Keys.ToastOnAlert, true);
        _settings.GetString(SettingsRepository.Keys.ToastOnAlert).Should().Be("1");
        _settings.GetBool(SettingsRepository.Keys.ToastOnAlert, false).Should().BeTrue();
    }

    [Fact]
    public void SetBool_False_StoresZero()
    {
        _settings.SetBool(SettingsRepository.Keys.ToastOnAlert, false);
        _settings.GetString(SettingsRepository.Keys.ToastOnAlert).Should().Be("0");
        _settings.GetBool(SettingsRepository.Keys.ToastOnAlert, true).Should().BeFalse();
    }

    [Fact]
    public void SmoothChartAnimations_AbsentKey_ReadsDefaultFalse()
    {
        // Phase 9.a — the seed migrations don't pre-populate this key,
        // so a fresh DB must report false. GetBool's defaultValue branch
        // is the load-bearing path; assert it directly against the real
        // key name so a typo in either side fails the test.
        _settings.GetBool(SettingsRepository.Keys.SmoothChartAnimations, defaultValue: false)
            .Should().BeFalse();
    }

    [Fact]
    public void SmoothChartAnimations_RoundTrip_PersistsTrue()
    {
        _settings.SetBool(SettingsRepository.Keys.SmoothChartAnimations, true);
        _settings.GetBool(SettingsRepository.Keys.SmoothChartAnimations, defaultValue: false)
            .Should().BeTrue();
        _settings.GetString(SettingsRepository.Keys.SmoothChartAnimations).Should().Be("1");
    }

    [Fact]
    public void Set_NullValue_Throws()
    {
        var act = () => _settings.Set("k", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Set_WhitespaceKey_Throws()
    {
        var act = () => _settings.Set("  ", "v");
        act.Should().Throw<ArgumentException>();
    }
}
