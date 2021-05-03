using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BattleTech;
using BattleTech.UI;
using Harmony;
using UnityEngine;
using static PanicSystem.PanicSystem;
using static PanicSystem.Logger;
using static PanicSystem.Components.Controller;
// ReSharper disable InconsistentNaming

namespace PanicSystem.Components
{
    public class LimitedEjectInvocation : InvocationMessage
    {
        public override MessageCenterMessageType MessageType => MessageCenterMessageType.AllMessages;
        private readonly AbstractActor actor;

        public override bool Invoke(CombatGameState combatGameState)
        {
            void Message(string input)
            {
                UnityGameInstance.BattleTechGame.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(
                    new ShowActorInfoSequence(actor, input, FloatieMessage.MessageNature.Neutral, false)));
            }

            var intersectingTags = modSettings.LimitManualEjectionTags.Intersect(actor.GetPilot().pilotDef.PilotTags).ToList();
            if (intersectingTags.Any())
            {
                LogDebug("TAG EXCLUSION");
                intersectingTags.Do(x =>
                {
                    LogDebug($"\t{x}");
                    Message($"Can't - {x}");
                });
            }
            else if (modSettings.LimitManualEjection &&
                     TrackedActors[GetActorIndex(actor)].PanicStatus <= (PanicStatus) modSettings.LimitManualEjectionLevel)
            {
                LogDebug("STATUS EXCLUSION");
                Message($"NOT WORSE THAN {modSettings.PanicStates[(int) modSettings.LimitManualEjectionLevel]}!");
            }
            else
            {
                LogDebug("REGULAR EJECT");
                var stackSequence = new EjectSequence(actor, false);
                PublishStackSequence(combatGameState.MessageCenter, stackSequence, this);
            }

            return true;
        }

        public LimitedEjectInvocation(AbstractActor actor, int randomSeed) : base(randomSeed)
        {
            this.actor = actor;
        }
    }

    [HarmonyPatch(typeof(SelectionStateEject), "CreateConfirmationOrders")]
    public static class SelectionStateEject_CreateConfirmationOrders_Patch
    {
        // replace EjectInvocation with custom InvocationMessage type
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var index = codes.FindIndex(c => c.opcode == OpCodes.Callvirt &&
                                             c.operand is MethodInfo info &&
                                             info == AccessTools.Method(typeof(MessageCenter), "PublishMessage"));

            // new LimitedEjectInvocation(actor, int);
            var stack = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0), // this
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SelectionState), "get_SelectedActor")), // Mech SelectionState.SelectedMech
                new CodeInstruction(OpCodes.Ldc_I4_0, 0),
                new CodeInstruction(OpCodes.Ldc_I4, int.MaxValue),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Random), "Range", new[] {typeof(int), typeof(int)})), // Random.Range(0, int.MaxValue);
                new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(LimitedEjectInvocation), new[] {typeof(AbstractActor), typeof(int)})),
            };

            // dump EjectInvocation we don't want
            codes[index - 1].opcode = OpCodes.Nop;
            // inject new instructions
            codes.InsertRange(index, stack);
            //LogDebug("CreateConfirmationOrders after transpiler:");
            //codes.Do(x => LogDebug($"{x.opcode,-30}{x.operand}"));
            return codes.AsEnumerable();
        }
    }
}
