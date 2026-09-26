#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Factory.RayReceiver;

#endregion

namespace NebulaNetwork.PacketProcessors.Factory.RayReceiver;

[RegisterPacketProcessor]
internal class RayReceiverChangeLensProcessor : PacketProcessor<RayReceiverChangeLensPacket>
{
    protected override void ProcessPacket(RayReceiverChangeLensPacket packet, NebulaConnection conn)
    {
        var pool = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.powerSystem?.genPool;
        if (pool == null || packet.GeneratorId <= 0 || packet.GeneratorId >= pool.Length ||
            pool[packet.GeneratorId].id != packet.GeneratorId || !pool[packet.GeneratorId].gamma ||
            packet.CatalystCount < 0 || packet.CatalystInc < 0 || packet.LensCount < 0 || packet.LensInc < 0)
        {
            return;
        }
        ref var generator = ref pool[packet.GeneratorId];
        generator.catalystId = packet.CatalystId;
        generator.curCatalystId = packet.CurrentCatalystId;
        generator.catalystCount = packet.CatalystCount;
        generator.catalystInc = packet.CatalystInc;
        generator.catalystMask = packet.CatalystMask;
        generator.catalystIncLevel = packet.CatalystIncLevel;
        generator.catalystPoint = packet.LensCount;
        generator.catalystIncPoint = packet.LensInc;
    }
}
