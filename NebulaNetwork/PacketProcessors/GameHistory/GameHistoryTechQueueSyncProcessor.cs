#region

using NebulaAPI.GameState;
using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameHistory;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.GameHistory;

[RegisterPacketProcessor]
internal class GameHistoryTechQueueSyncProcessor : PacketProcessor<GameHistoryTechQueueSyncPacket>
{
    protected override void ProcessPacket(GameHistoryTechQueueSyncPacket packet, NebulaConnection conn)
    {
        if (packet.IsRequest)
        {
            if (!IsHost) return;
            packet.IsRequest = false;
            packet.TechQueue = (int[])GameMain.history.techQueue.Clone();
            conn.SendPacket(packet);
        }
        else
        {
            if (!ValidQueue(packet.TechQueue, GameMain.history.MaxTechQueueCount())) return;
            using (Multiplayer.Session.History.IsIncomingRequest.On())
            {
                var length = GameMain.history.techQueueLength;
                for (var i = 0; i < length; i++)
                {
                    // Clear only occupied slots; the native array can grow to 32.
                    GameMain.history.DequeueTech();
                }
                for (var i = 0; i < packet.TechQueue.Length; i++)
                {
                    if (packet.TechQueue[i] == 0) break;
                    GameMain.history.EnqueueTech(packet.TechQueue[i]);
                }
            }
            if (IsHost)
            {
                // Broadcast to other players
                Multiplayer.Session.Network.SendPacketExclude(packet, conn);
            }
        }
    }

    internal static bool ValidQueue(int[] queue, int maxCount)
    {
        if (queue == null || queue.Length > 32) return false;
        var occupied = 0;
        var ended = false;
        foreach (var tech in queue)
        {
            if (tech == 0) ended = true;
            else if (tech < 0 || ended) return false;
            else occupied++;
        }
        return occupied <= maxCount;
    }
}
