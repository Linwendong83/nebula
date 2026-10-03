using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A10: authority-mode field policy — which balances are protected and which legacy packets go.
/// </summary>
/// <remarks>
/// TASKS.md A10 acceptance needs "旧机甲包不能恢复已消费物品" as structure, not as a race the
/// test wins: in a host authority world the two client-to-host full overwrites are refused, so
/// there is no path by which an old snapshot restores a spent balance. This test pins the
/// decision function processors enforce; the processors themselves stay thin branches (A08
/// pattern) with legacy behaviour unchanged.
/// </remarks>
[TestClass]
public class HostResourcePolicyTest
{
    [TestMethod]
    public void EveryLedgerKindIsProtectedAndMappedToASubdivision()
    {
        foreach (LedgerResourceKind kind in Enum.GetValues(typeof(LedgerResourceKind)))
        {
            if (kind == LedgerResourceKind.Unknown) continue;
            TestAssert.IsTrue(HostResourcePolicy.IsProtected(kind), kind + " must be protected in authority mode.");
            var subdivision = HostResourcePolicy.SubdivisionFor(kind);
            TestAssert.AreNotEqual(HostResourceSubdivision.Unknown, subdivision,
                kind + " must belong to exactly one A10 subdivision.");
            var tag = HostResourcePolicy.SubdivisionTag(subdivision);
            TestAssert.IsTrue(tag == "A10-1" || tag == "A10-2" || tag == "A10-3", "Bad tag " + tag);
        }
        TestAssert.IsFalse(HostResourcePolicy.IsProtected(LedgerResourceKind.Unknown));
        TestAssert.AreEqual(HostResourceSubdivision.Unknown,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.Unknown));
    }

    [TestMethod]
    public void SubdivisionsCoverTheThreeA10Submissions()
    {
        // A10-1: mecha energy and ammo.
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.CoreEnergy));
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.ReactorEnergy));
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.AmmoBullet));
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.AmmoStorageItem));
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.BombStorageItem));
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.ReactorStorageItem));
        TestAssert.AreEqual(HostResourceSubdivision.MechaEnergyAmmo,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.WarpStorageItem));

        // A10-2: inventory, construction material and fleet.
        TestAssert.AreEqual(HostResourceSubdivision.InventoryFleet,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.Sand));
        TestAssert.AreEqual(HostResourceSubdivision.InventoryFleet,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.InventoryItem));
        TestAssert.AreEqual(HostResourceSubdivision.InventoryFleet,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.DeliveryItem));
        TestAssert.AreEqual(HostResourceSubdivision.InventoryFleet,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.ForgeItem));
        TestAssert.AreEqual(HostResourceSubdivision.InventoryFleet,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.FighterStorageItem));

        // A10-3: base energy/stock and the factory boundary.
        TestAssert.AreEqual(HostResourceSubdivision.BaseFactory,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.BaseEnergy));
        TestAssert.AreEqual(HostResourceSubdivision.BaseFactory,
            HostResourcePolicy.SubdivisionFor(LedgerResourceKind.BaseStockItem));
    }

    [TestMethod]
    public void DroneQuotaFollowsItsOwner()
    {
        var epoch = new AuthorityEpoch(0xA10A10A10A10A10A, 0xBEEFBEEFBEEFBEEF);
        var player = LedgerOwner.ForPlayer("p1");
        var baseOwner = LedgerOwner.ForBase(ObjectKey.Create(epoch, PoolKind.Base, 7, 3, 1));
        TestAssert.AreEqual(HostResourceSubdivision.InventoryFleet,
            HostResourcePolicy.SubdivisionFor(new LedgerResourceKey(player, LedgerResourceKind.DroneSlot, 0)));
        TestAssert.AreEqual(HostResourceSubdivision.BaseFactory,
            HostResourcePolicy.SubdivisionFor(new LedgerResourceKey(baseOwner, LedgerResourceKind.DroneSlot, 0)));
        TestAssert.AreEqual(HostResourceSubdivision.Unknown,
            HostResourcePolicy.SubdivisionFor(default(LedgerResourceKey)));
    }

    [TestMethod]
    public void ClientFullOverwritesAreRefusedOnlyInAuthorityMode()
    {
        // Legacy rooms never refuse: the old overwrite path runs exactly as before.
        TestAssert.IsFalse(HostResourcePolicy.ShouldRefuseLegacyOverwrite(
            HostResourcePacketKind.MechaData, isHostAuthority: false));
        TestAssert.IsFalse(HostResourcePolicy.ShouldRefuseLegacyOverwrite(
            HostResourcePacketKind.LifeSnapshot, isHostAuthority: false));

        // Authority hosts refuse both client-to-host full overwrites.
        TestAssert.IsTrue(HostResourcePolicy.ShouldRefuseLegacyOverwrite(
            HostResourcePacketKind.MechaData, isHostAuthority: true));
        TestAssert.IsTrue(HostResourcePolicy.ShouldRefuseLegacyOverwrite(
            HostResourcePacketKind.LifeSnapshot, isHostAuthority: true));

        TestAssert.IsFalse(string.IsNullOrEmpty(
            HostResourcePolicy.RefusalReason(HostResourcePacketKind.MechaData)));
        TestAssert.IsFalse(string.IsNullOrEmpty(
            HostResourcePolicy.RefusalReason(HostResourcePacketKind.LifeSnapshot)));
    }

    [TestMethod]
    public void HostFactsToTheDisplayAreNeverRefused()
    {
        // SandCount and GiveItem travel host -> client (sand share, build refund). They carry host
        // truth to the display, not client claims to the ledger, so the A10 gate leaves them alone
        // in both modes. Factory transfers go via commands in A11/A18, not through this gate.
        foreach (var packet in new[]
        {
            HostResourcePacketKind.SandCount,
            HostResourcePacketKind.GiveItem,
            HostResourcePacketKind.Unknown
        })
        {
            TestAssert.IsFalse(HostResourcePolicy.ShouldRefuseLegacyOverwrite(packet, isHostAuthority: true),
                packet + " must stay allowed in authority mode.");
            TestAssert.IsFalse(HostResourcePolicy.ShouldRefuseLegacyOverwrite(packet, isHostAuthority: false),
                packet + " must stay allowed in legacy mode.");
        }
    }

    [TestMethod]
    public void ThePolicyModelReferencesNoEngineTypes()
    {
        var assembly = typeof(HostResourcePolicy).Assembly;
        var offenders = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace != "NebulaModel.Authority") continue;
            if (type.Name != nameof(HostResourcePolicy) && type.Name != nameof(HostResourceBatch) &&
                type.Name != nameof(HostResourceOp) && type.Name != nameof(HostResourceSubdivision) &&
                type.Name != nameof(HostResourcePacketKind) && type.Name != nameof(HostResourceOpType))
            {
                continue;
            }
            foreach (var member in type.GetMembers())
            {
                string name = string.Empty;
                if (member is System.Reflection.FieldInfo field) name = field.FieldType.FullName ?? string.Empty;
                else if (member is System.Reflection.PropertyInfo property) name = property.PropertyType.FullName ?? string.Empty;
                else if (member is System.Reflection.MethodInfo method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        var full = parameter.ParameterType.FullName ?? "";
                        if (full.Contains("UnityEngine") || full.Contains("GameMain") || full.Contains("NebulaWorld") ||
                            full.Contains("NebulaNetwork"))
                        {
                            offenders.Add(type.Name + "." + method.Name + "(" + full + ")");
                        }
                    }
                    continue;
                }
                else continue;
                if (name.Contains("UnityEngine") || name.Contains("GameMain") ||
                    name.Contains("NebulaWorld") || name.Contains("NebulaNetwork"))
                {
                    offenders.Add(type.Name + "." + member.Name + ": " + name);
                }
            }
        }
        TestAssert.IsEmpty(offenders, "Engine references in the A10 model: " + string.Join("; ", offenders));
    }
}
