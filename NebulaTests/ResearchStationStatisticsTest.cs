using System.Runtime.Serialization;
using NebulaWorld.Statistics;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
public class ResearchStationStatisticsTest
{
    [TestMethod]
    public void NoRecentResearchPowerReturnsFalse()
    {
        var factory = CreateFactoryStat(new long[600], 0);

        TestAssert.IsFalse(ResearchStationStatistics.HasRecentPowerUsage([factory], 1));
    }

    [TestMethod]
    public void RecentResearchPowerReturnsTrueAcrossHistoryWraparound()
    {
        var energy = new long[600];
        energy[599] = 1;
        var factory = CreateFactoryStat(energy, 0);

        TestAssert.IsTrue(ResearchStationStatistics.HasRecentPowerUsage([factory], 1));
    }

    [TestMethod]
    public void OldResearchPowerOutsideRecentWindowReturnsFalse()
    {
        var energy = new long[600];
        energy[500] = 1;
        var factory = CreateFactoryStat(energy, 0);

        TestAssert.IsFalse(ResearchStationStatistics.HasRecentPowerUsage([factory], 1));
    }

    [TestMethod]
    public void AnyFactoryWithRecentResearchPowerReturnsTrue()
    {
        var inactive = CreateFactoryStat(new long[600], 0);
        var energy = new long[600];
        energy[10] = 1;
        var active = CreateFactoryStat(energy, 11);

        TestAssert.IsTrue(ResearchStationStatistics.HasRecentPowerUsage([inactive, active], 2));
    }

    private static FactoryProductionStat CreateFactoryStat(long[] energy, int cursor)
    {
        var powerStat = (PowerStat)FormatterServices.GetUninitializedObject(typeof(PowerStat));
        powerStat.energy = energy;
        powerStat.cursor = [cursor];

        var factoryStat = (FactoryProductionStat)FormatterServices.GetUninitializedObject(typeof(FactoryProductionStat));
        factoryStat.powerPool = new PowerStat[5];
        factoryStat.powerPool[4] = powerStat;
        return factoryStat;
    }
}
