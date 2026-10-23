using System;
using HarmonyLib;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Makes Miho's guided missiles that rolled a hit explode on their target instead of at the proximity fuse cell.
    /// </summary>
    /// <remarks>
    /// MihoLib.Projectile_Homing_Explosive sets off its proximity fuse (2 cells for the Miho missiles) as soon as its path
    /// comes that close to the target, moves itself to that cell and explodes there, even when its own hit roll succeeded.
    /// The explosion is then up to 2 cells short: the plasma missile (radius 1.5) misses the target entirely on a straight
    /// approach, and the regular missile (radius 2.5) only reaches it at the edge of the blast and not behind walls.
    /// Right before the impact, a missile that still carries the intended target flag is moved onto the target cell.
    /// Missiles that failed the roll or lost track keep exploding where the fuse went off.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class MihoMissileFusePatch
    {
        private static readonly Type missileType = AccessTools.TypeByName("MihoLib.Projectile_Homing_Explosive");

        static MihoMissileFusePatch()
        {
            if (missileType == null)
                return;
            new Harmony("ifchen0.nicehealthtabpatch").Patch(AccessTools.Method(typeof(Projectile), "ImpactSomething"),
                prefix: new HarmonyMethod(typeof(MihoMissileFusePatch), nameof(ImpactSomethingPrefix)));
        }

        private static void ImpactSomethingPrefix(Projectile __instance)
        {
            if (!missileType.IsInstanceOfType(__instance))
                return;
            if ((__instance.HitFlags & ProjectileHitFlags.IntendedTarget) == ProjectileHitFlags.None)
                return;
            LocalTargetInfo target = __instance.intendedTarget;
            if (!target.IsValid || (target.HasThing && (!target.Thing.Spawned || target.Thing.Map != __instance.Map)))
                return;
            if (target.Cell.InBounds(__instance.Map))
                __instance.Position = target.Cell;
        }
    }
}
