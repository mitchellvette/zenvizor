using FluentAssertions;
using Microsoft.Data.Sqlite;
using ZenVizor.Ipc.Contracts.Dto;
using ZenVizor.Storage.Repositories;

namespace ZenVizor.Storage.Tests;

/// <summary>
/// Phase 6.1 repository round-trips for the alerts table. Validates the
/// dedupe+cooldown SQL gate, the in-place detail update, the State-filtered
/// reverse-chronological query, and the idempotent dismiss path. All tests
/// use a fresh temp SQLite file (migrator builds the schema) so the gates
/// are tested against the actual production schema, not a mock.
/// </summary>
public sealed class AlertsRepositoryTests : IDisposable
{
    private const long Hour = 3_600_000L;
    private const long Cooldown24h = 24 * Hour;
    private const long T0 = 1_780_704_000_000L; // 2026-06-02T00:00:00Z, a stable wall clock

    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly AlertsRepository _repo;

    public AlertsRepositoryTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zenvizor-alerts-{Guid.NewGuid():N}.db");
        new Migrator().Migrate(_dbPath);
        _connections = new ConnectionFactory(_dbPath);
        _repo = new AlertsRepository(_connections);
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

    private static NewAlert SampleAlert(string entityRef = "42", string detail = "initial") => new(
        Type:          nameof(AlertType.UnsignedFromUserPath),
        Severity:      nameof(NotableSeverity.Critical),
        SourceMonitor: nameof(SourceMonitor.Capture),
        EntityKind:    nameof(AlertEntityKind.App),
        EntityRef:     entityRef,
        Title:         $"Unsigned program: app-{entityRef}",
        Detail:        detail);

    // ─────────────────────────────────────────────────────────────────────
    //  TryInsert: dedupe + cooldown
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryInsert_FirstCall_ReturnsNewAlertId()
    {
        var id = _repo.TryInsert(SampleAlert(), nowUnixMs: T0, cooldownMs: Cooldown24h);
        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TryInsert_SecondCallSameKeyWhileActive_ReturnsZeroAndDoesNotDuplicate()
    {
        _repo.TryInsert(SampleAlert(), T0, Cooldown24h)
             .Should().BeGreaterThan(0);

        var second = _repo.TryInsert(SampleAlert(), T0 + 5 * 60_000, Cooldown24h);
        second.Should().Be(0);

        var rows = _repo.Query(AlertState.All, maxRows: 50);
        rows.Should().HaveCount(1);
    }

    [Fact]
    public void TryInsert_AfterDismissWithinCooldown_ReturnsZero()
    {
        var id = _repo.TryInsert(SampleAlert(), T0, Cooldown24h);
        _repo.Dismiss(id, T0 + 10 * 60_000).Should().BeTrue();

        // 12 h later — still inside the 24 h cooldown window.
        var second = _repo.TryInsert(SampleAlert(), T0 + 12 * Hour, Cooldown24h);
        second.Should().Be(0);

        var rows = _repo.Query(AlertState.All, 50);
        rows.Should().HaveCount(1);
    }

    [Fact]
    public void TryInsert_AfterDismissBeyondCooldown_ReturnsNewIdAndCreatesSecondRow()
    {
        var first = _repo.TryInsert(SampleAlert(), T0, Cooldown24h);
        _repo.Dismiss(first, T0 + 1 * Hour);

        // 25 h after dismiss — cooldown expired.
        var second = _repo.TryInsert(SampleAlert(), T0 + 26 * Hour, Cooldown24h);
        second.Should().BeGreaterThan(first);

        var rows = _repo.Query(AlertState.All, 50);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public void TryInsert_DifferentEntityRefs_CreateSeparateRows()
    {
        _repo.TryInsert(SampleAlert(entityRef: "1"), T0, Cooldown24h)
             .Should().BeGreaterThan(0);
        _repo.TryInsert(SampleAlert(entityRef: "2"), T0, Cooldown24h)
             .Should().BeGreaterThan(0);

        _repo.Query(AlertState.All, 50).Should().HaveCount(2);
    }

    [Fact]
    public void IsActiveOrCoolingDown_TracksTryInsertSemantics()
    {
        _repo.IsActiveOrCoolingDown(
            nameof(AlertType.UnsignedFromUserPath), nameof(AlertEntityKind.App), "42",
            T0, Cooldown24h).Should().BeFalse();

        var id = _repo.TryInsert(SampleAlert(), T0, Cooldown24h);
        id.Should().BeGreaterThan(0);

        _repo.IsActiveOrCoolingDown(
            nameof(AlertType.UnsignedFromUserPath), nameof(AlertEntityKind.App), "42",
            T0 + 5 * 60_000, Cooldown24h).Should().BeTrue();

        _repo.Dismiss(id, T0 + 1 * Hour);

        // Inside cooldown.
        _repo.IsActiveOrCoolingDown(
            nameof(AlertType.UnsignedFromUserPath), nameof(AlertEntityKind.App), "42",
            T0 + 12 * Hour, Cooldown24h).Should().BeTrue();

        // Outside cooldown.
        _repo.IsActiveOrCoolingDown(
            nameof(AlertType.UnsignedFromUserPath), nameof(AlertEntityKind.App), "42",
            T0 + 26 * Hour, Cooldown24h).Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  UpdateDetail
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateDetail_OnActiveRow_FlipsDetail_ReturnsOne()
    {
        _repo.TryInsert(SampleAlert(detail: "Connections so far: 1."), T0, Cooldown24h);

        var n = _repo.UpdateDetail(
            nameof(AlertType.UnsignedFromUserPath),
            nameof(AlertEntityKind.App),
            "42",
            "Connections so far: 5.");

        n.Should().Be(1);
        var rows = _repo.Query(AlertState.Active, 50);
        rows.Should().ContainSingle().Which.Detail.Should().Be("Connections so far: 5.");
    }

    [Fact]
    public void UpdateDetail_OnDismissedRow_DoesNotMutate_ReturnsZero()
    {
        var id = _repo.TryInsert(SampleAlert(detail: "Connections so far: 1."), T0, Cooldown24h);
        _repo.Dismiss(id, T0 + 1 * Hour);

        var n = _repo.UpdateDetail(
            nameof(AlertType.UnsignedFromUserPath),
            nameof(AlertEntityKind.App),
            "42",
            "Connections so far: 99.");

        n.Should().Be(0);
        var rows = _repo.Query(AlertState.Dismissed, 50);
        rows.Should().ContainSingle().Which.Detail.Should().Be("Connections so far: 1.");
    }

    [Fact]
    public void UpdateDetail_NoMatchingActiveRow_ReturnsZero()
    {
        var n = _repo.UpdateDetail(
            nameof(AlertType.UnsignedFromUserPath),
            nameof(AlertEntityKind.App),
            "9999",
            "anything");
        n.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Query: State filter + reverse-chronological + MaxRows truncation
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Query_FiltersByState_AndReturnsReverseChronological()
    {
        // Seed three rows at different timestamps. Dismiss the middle one.
        var ids = new[]
        {
            _repo.TryInsert(SampleAlert("1"), T0 + 0 * Hour, Cooldown24h),
            _repo.TryInsert(SampleAlert("2"), T0 + 1 * Hour, Cooldown24h),
            _repo.TryInsert(SampleAlert("3"), T0 + 2 * Hour, Cooldown24h),
        };
        _repo.Dismiss(ids[1], T0 + 3 * Hour);

        var active = _repo.Query(AlertState.Active, 50);
        active.Select(r => r.EntityRef).Should().Equal("3", "1");

        var dismissed = _repo.Query(AlertState.Dismissed, 50);
        dismissed.Should().ContainSingle().Which.EntityRef.Should().Be("2");

        var all = _repo.Query(AlertState.All, 50);
        all.Select(r => r.EntityRef).Should().Equal("3", "2", "1");
    }

    [Fact]
    public void Query_ReturnsMaxRowsPlusOne_ForHasMoreDetection()
    {
        for (int i = 0; i < 5; i++)
        {
            _repo.TryInsert(SampleAlert(entityRef: i.ToString()), T0 + i * Hour, Cooldown24h);
        }

        var rows = _repo.Query(AlertState.All, maxRows: 2);
        rows.Should().HaveCount(3);  // MaxRows + 1 — caller computes HasMore = rows.Count > MaxRows
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Dismiss: idempotency
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dismiss_ActiveRow_FlipsAcknowledgedAt_ReturnsTrue()
    {
        var id = _repo.TryInsert(SampleAlert(), T0, Cooldown24h);

        var flipped = _repo.Dismiss(id, T0 + 30 * 60_000);

        flipped.Should().BeTrue();
        var row = _repo.Query(AlertState.Dismissed, 50).Single();
        row.AcknowledgedAtUnixMs.Should().Be(T0 + 30 * 60_000);
    }

    [Fact]
    public void Dismiss_AlreadyDismissedRow_ReturnsFalse_AndPreservesOriginalAcknowledgedAt()
    {
        var id = _repo.TryInsert(SampleAlert(), T0, Cooldown24h);
        _repo.Dismiss(id, T0 + 30 * 60_000).Should().BeTrue();

        var second = _repo.Dismiss(id, T0 + 60 * 60_000);
        second.Should().BeFalse();

        var row = _repo.Query(AlertState.Dismissed, 50).Single();
        row.AcknowledgedAtUnixMs.Should().Be(T0 + 30 * 60_000);
    }

    [Fact]
    public void Dismiss_UnknownId_ReturnsFalse()
    {
        _repo.Dismiss(alertId: 999_999, T0).Should().BeFalse();
    }
}
