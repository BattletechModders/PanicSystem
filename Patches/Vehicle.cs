using BattleTech;
using Harmony;
using System;
using static PanicSystem.PanicSystem;
using static PanicSystem.Components.Controller;
using static PanicSystem.Logger;
using PanicSystem.Components;

// ReSharper disable UnusedMember.Local
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable InconsistentNaming

namespace PanicSystem.Patches
{
    [HarmonyPatch(typeof(Vehicle))]
    [HarmonyPatch("TakeWeaponDamage")]
    [HarmonyPatch(MethodType.Normal)]
    [HarmonyPatch(new Type[] { typeof(WeaponHitInfo), typeof(int), typeof(Weapon), typeof(float), typeof(float), typeof(int), typeof(DamageType) })]
    public static class Vehicle_TakeWeaponDamage
    {
        public static void Postfix(Vehicle __instance, WeaponHitInfo hitInfo, int hitLocation, Weapon weapon, float damageAmount, float directStructureDamage, int hitIndex, DamageType damageType)
        {
            if (__instance == null)
            {
                LogDebug("No vehicle\n");
                return;
            }
            LogReport($"{new string('?', 46)}\n");
            string wname = (weapon != null) ? (weapon.Name ?? "null") : "null";
            LogReport($"{__instance.DisplayName} :{__instance.GUID } took Damage from {wname} - {damageType.ToString()}\n");
            DamageHandler.ProcessDamage(__instance, damageAmount, directStructureDamage, 0);
        }
    }

}
