using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Exact spent-title migration, replay protection, integrity failures and growth by distinct evidence only.</summary>
public sealed class AcceptanceObservationLedgerTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-acceptance-ledger-" + Guid.NewGuid().ToString("N"));

    public AcceptanceObservationLedgerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* temporary artifact cleanup */ }
        catch (UnauthorizedAccessException) { /* temporary artifact cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MoreThanAThousandIdentitiesRemainExactAndReplayDoesNotGrowTheFile()
    {
        var path = Path.Join(_directory, "ledger.txt");
        var ledger = new AcceptanceObservationLedger(path, File.WriteAllText);
        var legacy = Enumerable.Range(1, 1000).Select(Id).ToArray();
        var next = Enumerable.Range(1001, 1200).Select(Id).ToArray();
        ledger.Spend(next, legacy, required: false);
        var before = File.ReadAllBytes(path);
        ledger.Spend(next, [], required: true);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(2200, ledger.Read([], required: true).Count);
        Assert.Equal(legacy.Concat(next).Order(), ledger.Read([], required: true).Order());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("malformed-id")]
    [InlineData("empty-id")]
    [InlineData("checksum")]
    [InlineData("truncated")]
    public void DamagedLedgersCannotTurnSpentEvidenceIntoFreshEvidence(string damage)
    {
        var path = Path.Join(_directory, "ledger.txt");
        var ledger = new AcceptanceObservationLedger(path, File.WriteAllText);
        ledger.Spend([Id(1)], [], required: false);
        var lines = File.ReadAllLines(path);
        switch (damage)
        {
            case "header": lines[0] = "unknown-version"; break;
            case "malformed-id": lines[2] = "not-a-guid"; break;
            case "empty-id": lines[2] = Guid.Empty.ToString("N"); break;
            case "checksum": lines[2] = Id(2).ToString("N"); break;
            case "truncated": lines = lines.Take(2).ToArray(); break;
        }

        File.WriteAllLines(path, lines);
        Assert.Throws<InvalidDataException>(() => ledger.Read([], required: true));
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ledger.Spend([Id(3)], [], required: true));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingRequiredLedgerFailsClosedWhileLegacyAndMemoryOnlyScopesRemainReadable()
    {
        var ledger = new AcceptanceObservationLedger(Path.Join(_directory, "missing.txt"), File.WriteAllText);
        Assert.Equal(new[] { Id(1) }, ledger.Read([Id(1)], required: false));
        Assert.Throws<IOException>(() => ledger.Read([Id(1)], required: true));
        var memory = new AcceptanceObservationLedger(null, (_, _) => throw new InvalidOperationException());
        Assert.Equal(new[] { Id(1) }, memory.Read([Id(1)], required: true));
        memory.Spend([Id(2)], [], required: true);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void StorageFailureCannotOverwriteACompleteLedger()
    {
        var path = Path.Join(_directory, "ledger.txt");
        var ledger = new AcceptanceObservationLedger(path, File.WriteAllText);
        ledger.Spend([Id(1)], [], required: false);
        var before = File.ReadAllBytes(path);
        var failing = new AcceptanceObservationLedger(path, (_, _) => throw new IOException("injected failure"));
        Assert.Throws<IOException>(() => failing.Spend([Id(2)], [], required: true));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RepeatedAndEmptySpendsValidateIntegrityWithoutRewritingAnUnchangedLedger()
    {
        var path = Path.Join(_directory, "no-op.txt");
        var writes = 0;
        var ledger = new AcceptanceObservationLedger(path, (file, contents) => { writes++; File.WriteAllText(file, contents); });
        ledger.Spend([Id(1)], [Id(2)], required: false);
        ledger.Spend([Id(1)], [Id(2)], required: true);
        ledger.Spend([], [], required: true);
        Assert.Equal(1, writes);
        Assert.Equal(new[] { Id(1), Id(2) }, ledger.Read([], required: true).Order());
        File.WriteAllText(path, "damaged");
        Assert.Throws<InvalidDataException>(() => ledger.Spend([], [], required: true));
        Assert.Equal(1, writes);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
}
