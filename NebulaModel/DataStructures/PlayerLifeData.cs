using NebulaAPI.Interfaces;
using NebulaAPI.Packets;

namespace NebulaModel.DataStructures;

[RegisterNestedType]
public class PlayerLifeData : INetSerializable
{
    private static long lastRevision;
    public static string CurrentTransactionId { get; set; } = "";
    public static bool CurrentRedeployItemsDropped { get; set; }
    public static long NextRevision(long observed = 0)
    {
        lastRevision = System.Math.Max(System.Math.Max(lastRevision, observed) + 1, System.DateTime.UtcNow.Ticks);
        return lastRevision;
    }
    public bool IsAlive { get; set; } = true;
    public int DeathCount { get; set; }
    public int TimeSinceKilled { get; set; }
    public int InvincibleTicks { get; set; }
    public int RespawnMode { get; set; }
    public int RespawnStage { get; set; }
    public int RespawnTick { get; set; }
    public int SelectedOption { get; set; } = -1;
    public long Revision { get; set; }
    public string TransactionId { get; set; } = "";
    public bool RedeployItemsDropped { get; set; }

    public static PlayerLifeData Capture(Player player, long revision = 0, string transactionId = "")
    {
        var action = player.controller.actionDeath;
        return new PlayerLifeData
        {
            IsAlive = player.isAlive,
            DeathCount = player.deathCount,
            TimeSinceKilled = player.timeSinceKilled,
            InvincibleTicks = player.invincibleTicks,
            RespawnMode = action.respawnMode,
            RespawnStage = action.respawnStage,
            RespawnTick = action.respawnTick,
            SelectedOption = action.selectedRespawnOption,
            Revision = revision == 0 ? NextRevision() : revision,
            TransactionId = string.IsNullOrEmpty(transactionId) ? CurrentTransactionId : transactionId,
            RedeployItemsDropped = CurrentRedeployItemsDropped
        };
    }

    public void Serialize(INetDataWriter writer)
    {
        writer.Put(IsAlive); writer.Put(DeathCount); writer.Put(TimeSinceKilled); writer.Put(InvincibleTicks);
        writer.Put(RespawnMode); writer.Put(RespawnStage); writer.Put(RespawnTick); writer.Put(SelectedOption);
        writer.Put(Revision); writer.Put(TransactionId ?? "");
        writer.Put(RedeployItemsDropped);
    }

    public void Deserialize(INetDataReader reader)
    {
        IsAlive = reader.GetBool(); DeathCount = reader.GetInt(); TimeSinceKilled = reader.GetInt();
        InvincibleTicks = reader.GetInt(); RespawnMode = reader.GetInt(); RespawnStage = reader.GetInt();
        RespawnTick = reader.GetInt(); SelectedOption = reader.GetInt(); Revision = reader.GetLong();
        TransactionId = reader.GetString();
        RedeployItemsDropped = reader.GetBool();
    }
}
