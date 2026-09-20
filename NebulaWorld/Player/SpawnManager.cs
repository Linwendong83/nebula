using NebulaModel.DataStructures;
using UnityEngine;

namespace NebulaWorld.Player;

public static class SpawnManager
{
    public static void SetBirthPoint(PlayerData data)
    {
        var planet = GameMain.galaxy.PlanetById(GameMain.galaxy.birthPlanetId);
        var point = planet.birthPoint;
        var upos = planet.uPosition + (VectorLF3)(planet.runtimeRotation * point);
        data.LocalPlanetId = planet.id;
        data.LocalStarId = planet.star.id;
        data.LocalPlanetPosition = new NebulaAPI.DataStructures.Float3(point);
        data.UPosition = new NebulaAPI.DataStructures.Double3(upos.x, upos.y, upos.z);
        data.Rotation = new NebulaAPI.DataStructures.Float3(Maths.SphericalRotation(point, 0f).eulerAngles);
    }
}
