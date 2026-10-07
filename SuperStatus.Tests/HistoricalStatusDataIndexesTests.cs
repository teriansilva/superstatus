using Microsoft.EntityFrameworkCore;
using SuperStatus.Data.DatabaseContext;
using SuperStatus.Data.Entities;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #79: HistoricalStatusDataSet must carry two indexes that the
/// poll + cleanup paths depend on. These tests assert the indexes are
/// declared in the EF model — the actual EXPLAIN ANALYZE plan verification
/// is operational (see PR description).
/// </summary>
[TestClass]
public class HistoricalStatusDataIndexesTests
{
    private SuperStatusDb BuildContext()
    {
        var options = new DbContextOptionsBuilder<SuperStatusDb>()
            .UseInMemoryDatabase($"db-{Guid.NewGuid()}")
            .Options;
        return new SuperStatusDb(options);
    }

    [TestMethod]
    public void Model_HasComposite_StatusCheckId_TimeOfCheckUTC_Index()
    {
        using var db = BuildContext();
        var entity = db.Model.FindEntityType(typeof(HistoricalStatusData));
        Assert.IsNotNull(entity);

        var indexes = entity.GetIndexes().ToList();
        var composite = indexes.FirstOrDefault(i =>
            i.Properties.Count == 2
            && i.Properties[0].Name == nameof(HistoricalStatusData.StatusCheckId)
            && i.Properties[1].Name == nameof(HistoricalStatusData.TimeOfCheckUTC));

        Assert.IsNotNull(composite,
            "Composite index on (StatusCheckId, TimeOfCheckUTC) is required to make GetMostRecentHistoricalStatusData an indexed scan instead of a seq scan.");

        // The DESC direction on TimeOfCheckUTC is asserted via the migration
        // file contents in Migration_DeclaresDescendingOnTimeOfCheckUTC below
        // — the EF InMemory provider does not always surface IsDescending in
        // the runtime model.
    }

    [TestMethod]
    public void Migration_DeclaresDescendingOnTimeOfCheckUTC()
    {
        // Read the migration file from the source tree and assert the
        // composite index declares descending: { false, true } so the most
        // recent row per check sits at the head of the index.
        var migrationsDir = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "SuperStatus.Data", "Migrations", "SuperStatusDbMigration");
        Assert.IsTrue(Directory.Exists(migrationsDir),
            $"Migrations directory not found at expected relative path: {migrationsDir}");

        var migration = Directory.EnumerateFiles(migrationsDir, "*_AddHistoricalStatusDataIndexes.cs").FirstOrDefault();
        Assert.IsNotNull(migration, "AddHistoricalStatusDataIndexes migration file not found.");

        var contents = File.ReadAllText(migration!);
        StringAssert.Contains(contents, "IX_HistoricalStatusDataSet_StatusCheckId_TimeOfCheckUTC");
        StringAssert.Contains(contents, "IX_HistoricalStatusDataSet_TimeOfCheckUTC");
        StringAssert.Contains(contents, "descending: new[] { false, true }",
            "Composite index must declare (StatusCheckId ASC, TimeOfCheckUTC DESC) so most-recent lookups don't need a sort.");
    }

    [TestMethod]
    public void Model_HasSingleColumn_TimeOfCheckUTC_Index()
    {
        using var db = BuildContext();
        var entity = db.Model.FindEntityType(typeof(HistoricalStatusData));
        Assert.IsNotNull(entity);

        var indexes = entity.GetIndexes().ToList();
        var single = indexes.FirstOrDefault(i =>
            i.Properties.Count == 1
            && i.Properties[0].Name == nameof(HistoricalStatusData.TimeOfCheckUTC));

        Assert.IsNotNull(single,
            "Single-column index on TimeOfCheckUTC is required for the cleanup job's WHERE TimeOfCheckUTC < cutoff to be index-scan instead of seq-scan.");
    }
}
