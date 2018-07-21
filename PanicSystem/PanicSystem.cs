﻿using BattleTech;
using Harmony;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using static PanicSystem.Controller;

// HUGE thanks to RealityMachina and mpstark for their work, outstanding.
namespace PanicSystem
{
    public static class PanicSystem
    {
        internal static ModSettings Settings;
        public static string ActiveJsonPath; //store current tracker here
        public static string StorageJsonPath; //store our meta trackers here
        public static string ModDirectory;

        public static Random RNG = new Random();

        // forces ejection saves every attack
        public static bool LastStraw = false;

        // makes saving throws harder
        public static bool PanicStarted = false;

        public static void Init(string modDir, string modSettings)
        {
            FileLog.Reset();
            FileLog.Log($"{DateTime.Now.ToLongTimeString()} Harmony Init");

            var harmony = HarmonyInstance.Create("com.BattleTech.PanicSystem");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ModDirectory = modDir;
            ActiveJsonPath = Path.Combine(modDir, "PanicSystem.json");
            StorageJsonPath = Path.Combine(modDir, "PanicSystemStorage.json");
            try
            {
                FileLog.Log($"Loading settings");
                Settings = JsonConvert.DeserializeObject<ModSettings>(modSettings);
            }
            catch (Exception)
            {
                Settings = new ModSettings();
            }
        }

        public static class Logger
        {
            public static readonly string FilePath = $"{ModDirectory}/Log.txt";
            public static void Harmony(object line)
            {
                if (!Settings.Debug) return;
                FileLog.Log(line.ToString());
            }

            public static void Debug(object line)
            {
                if (!Settings.Debug) return;
                using (var writer = new StreamWriter(FilePath, true))
                {
                    writer.WriteLine($"{DateTime.Now.ToShortTimeString()} {line}");
                }
            }
        }

        /// <summary>
        /// true implies a panic condition was met
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="attackSequence"></param>
        /// <returns></returns>
        public static bool ShouldPanic(Mech mech, AttackDirector.AttackSequence attackSequence)
        {
            if (!CheckCanPanic(mech, attackSequence))
            {
                return false;
            }

            Pilot pilot = mech.GetPilot();
            var weapons = mech.Weapons;
            var guts = mech.SkillGuts * Settings.GutsEjectionResistPerPoint;
            var tactics = mech.SkillTactics * Settings.TacticsEjectionResistPerPoint;
            var gutAndTacticsSum = guts + tactics;
            int index = -1;
            index = GetTrackedPilotIndex(mech);
            float panicModifiers = 0;

            if (LastStraw)
            {
                return true;
            }

            if (!CheckPanicFromDamage(mech, attackSequence))
            {
                return false;
            }

            Logger.Harmony($"Collecting panic modifiers:");
            if (!CheckTrackedPilots(mech, ref index))
            {
                return false;
            }

            panicModifiers = GetPilotHealthModifier(pilot, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = GetUnsteadyModifier(mech, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = GetHeadModifier(mech, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = GetCTModifier(mech, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = GetLTModifier(mech, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = CheckRT(mech, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = GetLegModifier(mech, panicModifiers);
            Logger.Harmony(panicModifiers);
            panicModifiers = CheckLastStraws(mech, panicModifiers, weapons);
            Logger.Harmony(panicModifiers);

            if (pilot.pilotDef.PilotTags.Contains("pilot_brave"))
            {
                panicModifiers -= Settings.BraveModifier;
                Logger.Harmony($"After bravery {panicModifiers}");
            }

            Logger.Harmony($"Guts and Tactics: {gutAndTacticsSum}");
            panicModifiers -= gutAndTacticsSum;
            Logger.Harmony($"After guts and tactics");

            if (mech.team == mech.Combat.LocalPlayerTeam)
            {
                panicModifiers -= (mech.Combat.LocalPlayerTeam.Morale - Settings.MedianMorale) / 2;
            }

            if ((panicModifiers < Settings.AtLeastOneChanceToPanicPercentage) && Settings.AtLeastOneChanceToPanic)
            {
                panicModifiers = Settings.AtLeastOneChanceToPanicPercentage;
                Logger.Harmony($"Floored saving throw to 25");
            }

            panicModifiers = (float)Math.Round(panicModifiers);
            Logger.Harmony($"Rounded to {panicModifiers} - roll to beat");

            var rng = new Random().Next(1, 101);
            Logger.Harmony($"Rolled: {rng}");

            if (rng <= (int)panicModifiers)
            {
                Logger.Harmony($"Failed panic save.");
                ApplyPanicDebuff(mech, index);
                return true;
            }

            mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, $"MADE {panicModifiers}% PANIC SAVE!", FloatieMessage.MessageNature.Buff, true)));
            Logger.Harmony($"MADE {panicModifiers}% PANIC SAVE!");
            return false;
        }

        /// <summary>
        /// true implies an ejection condition was met and ejections occurs
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="attackSequence"></param>
        /// <param name="panicStarted"></param>
        /// <returns></returns>
        public static bool RollForEjectionResult(Mech mech, AttackDirector.AttackSequence attackSequence, bool panicStarted)
        {
            if (mech == null || mech.IsDead || (mech.IsFlaggedForDeath && !mech.HasHandledDeath))
            {
                return false;
            }

            if (!attackSequence.attackDidDamage)
            {
                return false;
            }

            // knocked down mechs cannot eject
            if (mech.IsProne && Settings.KnockedDownCannotEject)
            {
                return false;
            }

            Pilot pilot = mech.GetPilot();
            if (pilot == null)
            {
                return false;
            }

            var weapons = mech.Weapons;
            var guts = mech.SkillGuts;
            var tactics = mech.SkillTactics;
            var gutsAndTacticsSum = guts + tactics;

            if (!CheckCantEject(mech, guts, pilot, tactics, gutsAndTacticsSum))
            {
                return false;
            }

            // start building ejectModifiers
            float ejectModifiers = 0;
            Logger.Harmony($"Collecting ejection modifiers:");
            Logger.Harmony(new string(c: '-', count: 80));

            // pilot health
            Logger.Harmony("Pilot Percent");
            float pilotHealthPercent = 1 - ((float)pilot.Injuries / pilot.Health);
            if (pilotHealthPercent < 1)
            {
                ejectModifiers += Settings.PilotHealthMaxModifier * (1 - pilotHealthPercent);
            }
            Logger.Harmony(ejectModifiers);

            Logger.Harmony("Unsteady");
            if (mech.IsUnsteady)
            {
                ejectModifiers += Settings.UnsteadyModifier;
            }
            Logger.Harmony(ejectModifiers);

            // Head
            Logger.Harmony("Head Percent");
            var headHealthPercent = (mech.HeadArmor + mech.HeadStructure) /
                                    (mech.GetMaxArmor(ArmorLocation.Head) +
                                     mech.GetMaxStructure(ChassisLocations.Head));
            if (headHealthPercent < 1)
            {
                ejectModifiers += Settings.HeadDamageMaxModifier * (1 - headHealthPercent);
            }
            Logger.Harmony(ejectModifiers);

            // CT  
            var ctPercent = (mech.CenterTorsoFrontArmor + mech.CenterTorsoStructure + mech.CenterTorsoRearArmor) /
                            (mech.GetMaxArmor(ArmorLocation.CenterTorso) +
                             mech.GetMaxStructure(ChassisLocations.CenterTorso));
            if (ctPercent < 1)
            {
                ejectModifiers += Settings.CTDamageMaxModifier * (1 - ctPercent);
            }
            Logger.Harmony("CT Percent");
            Logger.Harmony(ejectModifiers);

            // LT/RT
            var ltStructurePercent = mech.LeftTorsoStructure / mech.GetMaxStructure(ChassisLocations.LeftTorso);
            if (ltStructurePercent < 1)
            {
                ejectModifiers += Settings.SideTorsoInternalDamageMaxModifier * (1 - ltStructurePercent);
            }
            Logger.Harmony("LT Percent");
            Logger.Harmony(ejectModifiers);

            var rtStructurePercent = mech.RightTorsoStructure / mech.GetMaxStructure(ChassisLocations.RightTorso);
            if (rtStructurePercent < 1)
            {
                ejectModifiers += Settings.SideTorsoInternalDamageMaxModifier * (1 - rtStructurePercent);
            }
            Logger.Harmony("RT Percent");
            Logger.Harmony(ejectModifiers);

            // weaponless
            if (weapons.TrueForAll(w => w.DamageLevel == ComponentDamageLevel.Destroyed))
            {
                ejectModifiers += Settings.WeaponlessModifier;
            }
            Logger.Harmony("Weaponless");
            Logger.Harmony(ejectModifiers);

            // alone
            if (mech.Combat.GetAllAlliesOf(mech).TrueForAll(m => m == mech as AbstractActor || m.IsDead))
            {
                ejectModifiers += Settings.AloneModifier;
            }
            Logger.Harmony("Sole Survivor");
            Logger.Harmony(ejectModifiers);

            //dZ Because this is how it should be. Make this changeable. 
            ejectModifiers = (ejectModifiers - Settings.BaseEjectionResist - (Settings.GutsEjectionResistPerPoint * guts) -
                             (Settings.TacticsEjectionResistPerPoint * tactics)) * Settings.EjectChanceMultiplier;
            Logger.Harmony("Calculation:");
            Logger.Harmony(ejectModifiers);

            if (pilot.pilotDef.PilotTags.Contains("pilot_dependable"))
            {
                ejectModifiers -= Settings.TagDependableModifier;
                Logger.Harmony("Dependable Pilot");
                Logger.Harmony(ejectModifiers);
            }

            if (mech.team == mech.Combat.LocalPlayerTeam)
            {
                ejectModifiers -= (mech.Combat.LocalPlayerTeam.Morale - Settings.MedianMorale) / 2;
                Logger.Harmony("Morale calcuation:");
                Logger.Harmony(ejectModifiers);
            }

            Logger.Harmony("Final roll to beat:");
            var rollToBeat = (float)Math.Round(ejectModifiers);
            Logger.Harmony($"");
            // passes through if last straw is met to force an ejection roll
            if (rollToBeat <= 0 && !IsLastStrawPanicking(mech, ref panicStarted))
            {
                mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(
                    new ShowActorInfoSequence(mech, $"RESISTED EJECTION!", FloatieMessage.MessageNature.Buff, true)));
                Logger.Harmony($"RESISTED EJECTION!");
                return false;
            }

            if (!panicStarted)
            {
                rollToBeat = Math.Min(rollToBeat, Settings.MaxEjectChance);
            }
            else
            {
                rollToBeat = Math.Min(rollToBeat, Settings.MaxEjectChanceWhenEarlyEjectThresholdMet);
            }

            Logger.Harmony($"RollToBeat: {rollToBeat}");
            var roll = RNG.Next(1, 101);
            Logger.Harmony($"Rolled: {roll}");
            Logger.Harmony($"{rollToBeat}% EJECTION CHANCE!");
            mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(
                new ShowActorInfoSequence(mech, $"{rollToBeat}% EJECTION CHANCE!", FloatieMessage.MessageNature.Debuff, true)));
            if (roll < rollToBeat)
            {
                Logger.Harmony($"FAILED SAVE: Punchin' Out!!");
                mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(
                                                         new ShowActorInfoSequence(mech, $"FAILED SAVE: Punchin' Out!!", FloatieMessage.MessageNature.Debuff, true)));
            }
            else
            {
                Logger.Harmony($"AVOIDED!");
                mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(
                                                         new ShowActorInfoSequence(mech, $"AVOIDED!", FloatieMessage.MessageNature.Buff, true)));
            }
            return roll < rollToBeat;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <param name="weapons"></param>
        /// <returns></returns>
        private static float CheckLastStraws(Mech mech, float panicModifiers, List<Weapon> weapons)
        {
            // weaponless
            if (weapons.TrueForAll(w =>
                w.DamageLevel == ComponentDamageLevel.Destroyed ||
                w.DamageLevel == ComponentDamageLevel.NonFunctional))
            {
                panicModifiers += Settings.WeaponlessModifier;
            }

            // alone
            if (mech.Combat.GetAllAlliesOf(mech).TrueForAll(m => m.IsDead || m == mech as AbstractActor))
            {
                panicModifiers += Settings.AloneModifier;
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers and updates lowestHealthLethalLocation
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float GetLegModifier(Mech mech, float panicModifiers)
        {
            // dZ Check legs independently. Code here significantly improved.  Handles missing legs
            var legPercentRight = 1 - (mech.RightLegStructure + mech.RightLegArmor) / (mech.GetMaxStructure(ChassisLocations.RightLeg) + mech.GetMaxArmor(ArmorLocation.RightLeg));
            var legPercentLeft = 1 - (mech.LeftLegStructure + mech.LeftLegArmor) / (mech.GetMaxStructure(ChassisLocations.LeftLeg) + mech.GetMaxArmor(ArmorLocation.LeftLeg));
            if (legPercentRight + legPercentLeft < 2)
            {
                panicModifiers += Settings.LeggedMaxModifier * (legPercentRight + legPercentLeft);
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float CheckRT(Mech mech, float panicModifiers)
        {
            var rtStructurePercent = mech.RightTorsoStructure / mech.GetMaxStructure(ChassisLocations.RightTorso);
            if (rtStructurePercent < 1)
            {
                panicModifiers += Settings.SideTorsoInternalDamageMaxModifier * (1 - rtStructurePercent);
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float GetLTModifier(Mech mech, float panicModifiers)
        {
            var ltStructurePercent = mech.LeftTorsoStructure / mech.GetMaxStructure(ChassisLocations.LeftTorso);
            if (ltStructurePercent < 1)
            {
                panicModifiers += Settings.SideTorsoInternalDamageMaxModifier * (1 - ltStructurePercent);
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers and updates lowestHealthLethalLocation
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float GetCTModifier(Mech mech, float panicModifiers)
        {
            var ctPercent = (mech.CenterTorsoFrontArmor + mech.CenterTorsoStructure + mech.CenterTorsoRearArmor) /
                            (mech.GetMaxArmor(ArmorLocation.CenterTorso) +
                             mech.GetMaxStructure(ChassisLocations.CenterTorso));
            if (ctPercent < 1)
            {
                panicModifiers += Settings.CTDamageMaxModifier * (1 - ctPercent);
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float GetHeadModifier(Mech mech, float panicModifiers)
        {
            var headHealthPercent = (mech.HeadArmor + mech.HeadStructure) /
                                    (mech.GetMaxArmor(ArmorLocation.Head) +
                                     mech.GetMaxStructure(ChassisLocations.Head));
            if (headHealthPercent < 1)
            {
                panicModifiers += Settings.HeadDamageMaxModifier * (1 - headHealthPercent);
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float GetUnsteadyModifier(Mech mech, float panicModifiers)
        {
            if (mech.IsUnsteady)
            {
                panicModifiers += Settings.UnsteadyModifier;
            }
            return panicModifiers;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="pilot"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static float GetPilotHealthModifier(Pilot pilot, float panicModifiers)
        {
            if (pilot != null)
            {
                float pilotHealthPercent = 1 - ((float)pilot.Injuries / pilot.Health);
                if (pilotHealthPercent < 1)
                {
                    panicModifiers += Settings.PilotHealthMaxModifier * (1 - pilotHealthPercent);
                }
            }
            return panicModifiers;
        }

        /// <summary>
        /// true implies the pilot is tracked
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static bool CheckTrackedPilots(Mech mech, ref int index)
        {
            if (index < 0)
            {
                TrackedPilots.Add(new PanicTracker(mech)); //add a new tracker to tracked pilot, then we run it all over again;
                index = GetTrackedPilotIndex(mech);
                if (index < 0)
                {
                    return false;
                }
            }

            if (TrackedPilots[index].TrackedMech != mech.GUID)
            {
                return false;
            }

            if (TrackedPilots[index].TrackedMech == mech.GUID &&
                TrackedPilots[index].ChangedRecently && Settings.AlwaysGatedChanges)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// returns modifiers
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="panicModifiers"></param>
        /// <returns></returns>
        private static bool CheckPanicFromDamage(Mech mech, AttackDirector.AttackSequence attackSequence)
        {
            if (!attackSequence.attackDidDamage)
            {
                Logger.Harmony($"No damage.");
                return false;
            }
            else
            {
                Logger.Harmony($"Attack does {attackSequence.attackArmorDamage} armor and {attackSequence.attackStructureDamage} structure.");
            }

            if (attackSequence.attackStructureDamage > 0)
            {
                Logger.Harmony($"{attackSequence.attackStructureDamage} structural damage causes a panic check.");
                return true;
            }

            if (attackSequence.attackArmorDamage / (GetCurrentMechArmour(mech) + attackSequence.attackArmorDamage) * 100 < Settings.MinimumArmourDamagePercentageRequired)
            {
                Logger.Harmony($"Not enough armor damage ({attackSequence.attackArmorDamage}).");
                return false;
            }

            Logger.Harmony($"{attackSequence.attackArmorDamage} damage attack causes a panic check.");
            return true;
        }

        /// <summary>
        /// true implies ejection is possible
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="guts"></param>
        /// <param name="pilot"></param>
        /// <param name="tactics"></param>
        /// <param name="gutsAndTacticsSum"></param>
        /// <returns></returns>
        private static bool CheckCantEject(Mech mech, int guts, Pilot pilot, int tactics, int gutsAndTacticsSum)
        {
            // guts 10 makes you immune, player character cannot be forced to eject
            if ((guts == 10 && Settings.GutsTenAlwaysResists) ||
                (Settings.PlayerCharacterAlwaysResists && pilot.IsPlayerCharacter))
            {
                return false;
            }

            // tactics 10 makes you immune, or combination of guts and tactics makes you immune.
            if ((tactics == 10 && Settings.TacticsTenAlwaysResists) ||
                (gutsAndTacticsSum >= 10 && Settings.ComboTenAlwaysResists))
            {
                return false;
            }

            // pilots that cannot eject or be headshot shouldn't eject
            if (mech != null && !mech.CanBeHeadShot || !pilot.CanEject)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        ///  true implies panic is possible
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="attackSequence"></param>
        /// <returns></returns>
        private static bool CheckCanPanic(Mech mech, AttackDirector.AttackSequence attackSequence)
        {
            if (mech == null || mech.IsDead || (mech.IsFlaggedForDeath && mech.HasHandledDeath))
            {
                Logger.Harmony($"{mech.DisplayName} incapacitated by {attackSequence.attacker.DisplayName}.");
                return false;
            }

            if (attackSequence == null)
            {
                Logger.Harmony($"No attack.");
                return false;
            }

            if (mech.team.IsLocalPlayer && !Settings.PlayerTeamCanPanic)
            {
                Logger.Harmony($"Players can't panic.");
                return false;
            }

            if (!mech.team.IsLocalPlayer && !Settings.EnemiesCanPanic)
            {
                Logger.Harmony($"AI can't panic.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// sums armour from all locations
        /// </summary>
        /// <param name="mech"></param>
        /// <returns></returns>
        private static float GetCurrentMechArmour(Mech mech)
        {
            float totalArmor = 0;
            totalArmor += mech.GetCurrentArmor(ArmorLocation.Head);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.CenterTorso);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.CenterTorsoRear);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.LeftTorso);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.LeftTorsoRear);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.RightTorso);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.RightTorsoRear);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.RightArm);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.LeftArm);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.RightLeg);
            totalArmor += mech.GetCurrentArmor(ArmorLocation.LeftLeg);
            return totalArmor;
        }

        /// <summary>
        /// not in use
        /// </summary>
        /// <param name="mech"></param>
        /// <returns></returns>
        private static float GetTotalMechArmour(Mech mech)
        {
            float maxArmor = 0;
            maxArmor += mech.GetMaxArmor(ArmorLocation.CenterTorso);
            maxArmor += mech.GetMaxArmor(ArmorLocation.LeftArm);
            maxArmor += mech.GetMaxArmor(ArmorLocation.CenterTorsoRear);
            maxArmor += mech.GetMaxArmor(ArmorLocation.Head);
            maxArmor += mech.GetMaxArmor(ArmorLocation.LeftTorso);
            maxArmor += mech.GetMaxArmor(ArmorLocation.RightTorso);
            maxArmor += mech.GetMaxArmor(ArmorLocation.RightTorsoRear);
            maxArmor += mech.GetMaxArmor(ArmorLocation.LeftTorsoRear);
            maxArmor += mech.GetMaxArmor(ArmorLocation.RightArm);
            maxArmor += mech.GetMaxArmor(ArmorLocation.LeftLeg);
            maxArmor += mech.GetMaxArmor(ArmorLocation.RightLeg);
            return maxArmor;
        }

        /// <summary>
        /// applied combat modifiers to tracked mechs based on panic status
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="index"></param>
        public static void ApplyPanicDebuff(Mech mech, int index)
        {
            Logger.Harmony("FAILED PANIC CHECK!");
            mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, $"FAILED PANIC CHECK!", FloatieMessage.MessageNature.Debuff, true)));
            if (TrackedPilots[index].TrackedMech == mech.GUID &&
                TrackedPilots[index].PilotStatus == PanicStatus.Confident)
            {
                Logger.Harmony("UNSETTLED!");
                mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, $"UNSETTLED!", FloatieMessage.MessageNature.Debuff, true)));
                TrackedPilots[index].PilotStatus = PanicStatus.Unsettled;
                mech.StatCollection.ModifyStat("Panic Attack Reset: Accuracy", -1, "AccuracyModifier", StatCollection.StatOperation.Set, 0f);
                mech.StatCollection.ModifyStat("Panic Attack Reset: Mech To Hit", -1, "ToHitThisActor", StatCollection.StatOperation.Set, 0f);
                mech.StatCollection.ModifyStat("Panic Attack: Unsettled Aim", -1, "AccuracyModifier", StatCollection.StatOperation.Float_Add, Settings.UnsettledAttackModifier);

            }
            else if (TrackedPilots[index].TrackedMech == mech.GUID &&
                     TrackedPilots[index].PilotStatus == PanicStatus.Unsettled)
            {
                Logger.Harmony("STRESSED!!");
                mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, $"STRESSED!", FloatieMessage.MessageNature.Debuff, true)));
                TrackedPilots[index].PilotStatus = PanicStatus.Stressed;
                mech.StatCollection.ModifyStat("Panic Attack Reset: Accuracy", -1, "AccuracyModifier", StatCollection.StatOperation.Set, 0f);
                mech.StatCollection.ModifyStat("Panic Attack Reset: Mech To Hit", -1, "ToHitThisActor", StatCollection.StatOperation.Set, 0f);
                mech.StatCollection.ModifyStat("Panic Attack: Stressed Aim", -1, "AccuracyModifier", StatCollection.StatOperation.Float_Add, Settings.StressedAimModifier);
                mech.StatCollection.ModifyStat("Panic Attack: Stressed Defence", -1, "ToHitThisActor", StatCollection.StatOperation.Float_Add, Settings.StressedToHitModifier);
            }
            else if (TrackedPilots[index].TrackedMech == mech.GUID &&
                     TrackedPilots[index].PilotStatus == PanicStatus.Stressed)
            {
                Logger.Harmony("PANICKED!");
                mech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, $"PANICKED!", FloatieMessage.MessageNature.Debuff, true)));
                TrackedPilots[index].PilotStatus = PanicStatus.Panicked;
                mech.StatCollection.ModifyStat("Panic Attack Reset: Accuracy", -1, "AccuracyModifier", StatCollection.StatOperation.Set, 0f);
                mech.StatCollection.ModifyStat("Panic Attack Reset: Mech To Hit", -1, "ToHitThisActor", StatCollection.StatOperation.Set, 0f);
                mech.StatCollection.ModifyStat("Panic Attack: Panicking Aim!", -1, "AccuracyModifier", StatCollection.StatOperation.Float_Add, Settings.PanickedAimModifier);
                mech.StatCollection.ModifyStat("Panic Attack: Panicking Defence!", -1, "ToHitThisActor", StatCollection.StatOperation.Float_Add, Settings.PanickedToHitModifier);
            }
            TrackedPilots[index].ChangedRecently = true;
        }

        /// <summary>
        /// returning true and ref here implies they're on their last straw
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="PanicStarted"></param>
        /// <returns></returns>
        public static bool IsLastStrawPanicking(Mech mech, ref bool PanicStarted)
        {
            //Logger.Harmony($"----- IsLastStrawPanicking -----");
            if (mech == null || mech.IsDead || (mech.IsFlaggedForDeath && mech.HasHandledDeath))
            {
                return false;
            }

            Pilot pilot = mech.GetPilot();

            int i = GetTrackedPilotIndex(mech);
            var weapons = mech.Weapons;

            if (pilot != null && !pilot.LethalInjuries && pilot.Health - pilot.Injuries <= Settings.MinimumHealthToAlwaysEjectRoll)
            {
                Logger.Harmony($"Last straw health.");
                PanicStarted = true;
                return true;
            }
            if (weapons.TrueForAll(w => w.DamageLevel == ComponentDamageLevel.Destroyed || w.DamageLevel == ComponentDamageLevel.NonFunctional) && Settings.ConsiderEjectingWithNoWeaps)
            {
                Logger.Harmony($"Last straw weapons.");
                PanicStarted = true;
                return true;
            }

            if (mech.Combat.GetAllAlliesOf(mech).TrueForAll(m => m.IsDead || m.GUID == mech.GUID) && Settings.ConsiderEjectingWhenAlone)
            {
                Logger.Harmony($"Last straw sole survivor.");
                PanicStarted = true;
                return true;
            }

            if (i > -1)
            {
                if (TrackedPilots[i].TrackedMech == mech.GUID &&
                    TrackedPilots[i].PilotStatus == PanicStatus.Panicked)
                {
                    Logger.Harmony($"Panicked.");
                    PanicStarted = true;
                    return true;
                }

                if (CanEjectBeforePanicked(mech, i))
                {
                    Logger.Harmony($"Early ejection danger.");
                    PanicSystem.PanicStarted = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// true implies this weight class of mech can eject before reachign Panicked
        /// </summary>
        /// <param name="mech"></param>
        /// <param name="i"></param>
        /// <returns></returns>
        private static bool CanEjectBeforePanicked(Mech mech, int i)
        {
            if (TrackedPilots[i].TrackedMech == mech.GUID && mech.team.IsLocalPlayer)
            {
                if (mech.team.IsLocalPlayer)
                {
                    if (Settings.PlayerLightsConsiderEjectingEarly && mech.weightClass == WeightClass.LIGHT)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.LightMechEarlyEjecthreshold)
                        {
                            return true;
                        }
                    }

                    else if (Settings.PlayerMediumsConsiderEjectingEarly && mech.weightClass == WeightClass.MEDIUM)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.MediumMechEarlyEjectThreshold)
                        {
                            return true;
                        }
                    }

                    else if (Settings.PlayerHeaviesConsiderEjectingEarly && mech.weightClass == WeightClass.HEAVY)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.HeavyMechEarlyEjectThreshold)
                        {
                            return true;
                        }
                    }

                    else if (Settings.PlayerAssaultsConsiderEjectingEarly && mech.weightClass == WeightClass.ASSAULT)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.AssaultMechEarlyEjectThreshold)
                        {
                            return true;
                        }
                    }
                }
                else
                {
                    if (Settings.EnemyLightsConsiderEjectingEarly && mech.weightClass == WeightClass.LIGHT)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.LightMechEarlyEjecthreshold)
                        {
                            return true;
                        }
                    }

                    else if (Settings.EnemyMediumsConsiderEjectingEarly && mech.weightClass == WeightClass.MEDIUM)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.MediumMechEarlyEjectThreshold)
                        {
                            return true;
                        }
                    }

                    else if (Settings.EnemyHeaviesConsiderEjectingEarly && mech.weightClass == WeightClass.HEAVY)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.HeavyMechEarlyEjectThreshold)
                        {
                            return true;
                        }
                    }

                    else if (Settings.EnemyAssaultsConsiderEjectingEarly && mech.weightClass == WeightClass.ASSAULT)
                    {
                        if (TrackedPilots[i].PilotStatus >= Settings.AssaultMechEarlyEjectThreshold)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public class ModSettings
        {
            public bool PlayerCharacterAlwaysResists = true;
            public bool PlayerTeamCanPanic = true;
            public bool EnemiesCanPanic = true;
            public bool Debug = false;

            //new mechanics for considering when to eject based on mech class
            public bool PlayerLightsConsiderEjectingEarly = false;
            public bool EnemyLightsConsiderEjectingEarly = true;
            public PanicStatus LightMechEarlyEjecthreshold = PanicStatus.Unsettled;

            public bool PlayerMediumsConsiderEjectingEarly = false;
            public bool EnemyMediumsConsiderEjectingEarly = false;
            public PanicStatus MediumMechEarlyEjectThreshold = PanicStatus.Stressed;

            public bool PlayerHeaviesConsiderEjectingEarly = false;
            public bool EnemyHeaviesConsiderEjectingEarly = false;
            public PanicStatus HeavyMechEarlyEjectThreshold = PanicStatus.Stressed;

            public bool PlayerAssaultsConsiderEjectingEarly = false;
            public bool EnemyAssaultsConsiderEjectingEarly = false;
            public PanicStatus AssaultMechEarlyEjectThreshold = PanicStatus.Stressed;

            public float MaxEjectChanceWhenEarlyEjectThresholdMet = 10;

            //minmum armour and structure damage
            public float MinimumArmourDamagePercentageRequired = 10; //if no structure damage, a Mech must lost a bit of its armour before it starts worrying

            //general panic roll
            //rolls out of 20
            //max guts and tactics almost prevents any panicking (or being the player character, by default)
            public bool AtLeastOneChanceToPanic = true;
            public int AtLeastOneChanceToPanicPercentage = 10;
            public bool AlwaysGatedChanges = true;
            public float MaxPanicResistTotal = 15; //at least 20% chance to panic if you can't nullify the whole thing
            public float MedianMorale = 25;

            public bool LosingLimbAlwaysPanics = false;

            //Unsettled debuffs
            //+1 difficulty to attacks
            public float UnsettledAttackModifier = 1;
            public float BraveModifier = 5;
            public float TagDependableModifier = 5;

            //stressed debuffs
            //+2 difficulty to attacks
            //-1 difficulty to being hit

            public float StressedAimModifier = 2;
            public float StressedToHitModifier = -1;

            //ejection
            //+4 difficulty to attacks
            //-2 difficulty to being hit
            public float PanickedAimModifier = 4;
            public float PanickedToHitModifier = -2;
            public bool GutsTenAlwaysResists = false;
            public bool ComboTenAlwaysResists = false;
            public bool TacticsTenAlwaysResists = false;
            public int MinimumHealthToAlwaysEjectRoll = 1;
            public bool KnockedDownCannotEject = true;

            public bool ConsiderEjectingWithNoWeaps = true;
            public bool ConsiderEjectingWhenAlone = true;
            public float MaxEjectChance = 50;
            public float EjectChanceMultiplier = 5;

            public float BaseEjectionResist = 10;
            public float GutsEjectionResistPerPoint = 2;
            public float TacticsEjectionResistPerPoint = 1;
            public float UnsteadyModifier = 5;
            public float PilotHealthMaxModifier = 10;

            public float HeadDamageMaxModifier = 10;
            public float CTDamageMaxModifier = 10;
            public float SideTorsoInternalDamageMaxModifier = 10;
            public float LeggedMaxModifier = 10;

            public float WeaponlessModifier = 15;
            public float AloneModifier = 20;
        }
    }
}