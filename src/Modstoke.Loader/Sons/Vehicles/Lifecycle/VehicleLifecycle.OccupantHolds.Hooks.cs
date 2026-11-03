// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using global::Sons.Ai.Vail;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Animation;
using global::Sons.Characters;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private static partial class CartOccupantWriterHooks
        {
            private static bool IsSuspendedTick(string name) => name is
                "Update" or "UpdateInput" or "LastUpdate" or "LateUpdate" or "FixedUpdate"
                or "Render" or "Simulate" or "ForceUpdate" or "UpdateSolverExternal"
                or "Tick" or "TickClient" or "TickServer" or "TickMovementClient"
                or "TickNetworkActorServer" or "TickNetworkActorClient" or "TickActorMovementServer"
                or "OwnerUpdateStateVars" or "TickAttachedMovement" or "UpdateTransform"
                or "PostClientUpdateTransform" or "OnAnimatorMove" or "UpdateActiveAction"
                or "PushApart" or "PushAwayFromCapsule" or "ClientInCartUpdateTransform";

            private static void QualifyIndependentWriters(List<MethodInfo> receivers,
                List<MethodInfo> actorFirst, List<MethodInfo> actorSecond, List<MethodInfo> actorPairs)
            {
                // Matched 9BEED267... / 6A1A92A2.... Each entry is unique except
                // ClientInCartUpdateTransform/ClientRaftUpdateTransform (same type and ABI).
                // Manager loops remain live; only their exact retained actor receivers stop.
                receivers.AddRange(new[]
                {
                    Actor("Tick", 0x2724860, 4912, "067B290AD12B27205F497ED6C0AB4F2B114AA86D046B71ADE8437FCB57ED68F2", typeof(float), typeof(float), typeof(bool), typeof(int)),
                    Actor("TickClient", 0x2725FA0, 1712, "A525A8C20EBD21AA0EF77B9C35AABDBFE74405863057A5068E1B49EC6855024D", typeof(float), typeof(int), typeof(Vector3), typeof(bool)),
                    Actor("TickNetworkActorServer", 0x2725B90, 160, "C8B04AF91315D850C243B4EF0CEB0A54E8C50214EDDC6FE0662974CF073CF82A"),
                    Actor("TickNetworkActorClient", 0x2725C30, 160, "877B8FB82031F6FCCF8C36F0BB822C98AC21F0F5033EF8175CBF806A900A2E7C"),
                    Actor("TickMovementClient", 0x2726650, 96, "CA45E47CE85DF01E06ED2BE2D36B24DB981257EA8C57F425ED100742709214CA", typeof(float)),
                    Actor("TickActorMovementServer", 0x274F0E0, 4656, "5A9719A56E2C8DDBDE6E138A51D0650EC0300015C310DA97E36D11F175C03BD7", typeof(float)),
                    Actor("PushAwayFromCapsule", 0x2751C80, 480, "16DF0F93DA9ECD5591E8F08C0A07CB665C598D02E8A32A54B6D1D4C7C94D2011", typeof(Vector3), typeof(float), typeof(float), typeof(float)),
                    Actor("UpdateTransform", 0x2751E60, 1008, "B3E2BE1F52E0FABAE650EB64A73AEA7B4084A6207DFFFC662F28F97EB39D0E0B", typeof(float), typeof(bool)),
                    Actor("ClientInCartUpdateTransform", 0x2750310, 160, "3E9ED442EAFBB9AE89655524538D26E45B0A2BBC6BB7B6C29A14792DD1BE41E2", typeof(Vector3), typeof(Quaternion)),
                    Actor("PostClientUpdateTransform", 0x2752430, 336, "7BEDB17F817F3FA18373DD128FC0437065D8F4A2232833F87883662862E98AD2"),
                    Actor("OnAnimatorMove", 0x2752B20, 256, "AA7FF3EC12B6E09769F2BAE6C981193E2492713F5CB09196E2180ADF6D75E458"),
                    CartWriterHooks.Qualify(typeof(VailActor), "UpdateActiveAction", typeof(bool), 0x2761F10, 1488,
                        "F511DE6815B3BE80ED004A129B2F417D13357201894F0530A0690398AADA60C7"),
                    Actor("SetCurrentState", 0x2726CF0, 496, "640272BF592F8B89799334A824C00C7A664FC149E5D7CDC1EF3D878BFF34B29F", typeof(global::Sons.Ai.Vail.State), typeof(bool)),
                    Actor("SetActiveStimuli", 0x27608A0, 976, "F2131130CBEE2BF5FEF4744C6DCB891762D217BD925526ED62329DD0851AFE27", typeof(IStimuli)),
                    Actor("AddImmediateStimuli", 0x275CA40, 256, "54AC2ADCDC6B3B52062680FB498CD807762CE0E739190A1CED779014D1D4FC25", typeof(ImmediateStimuli)),
                    Actor("InterruptCurrentAction", 0x27616F0, 1760, "369DD068E93CA986597322AAA20432090F501F6DC39CFB3D75B5F689D61786FE", typeof(InterruptReason)),
                    Actor("OnExitGolfCartState", 0x2763920, 416, "708E6BC256B7C1373348805A1744251EC346E945F77663BB0718B43E431BA9DA"),
                    Actor("Deactivate", 0x2723290, 1584, "BEA7C533D8D7E4FA16AACA5FD079C94F19F8F03BF313380F8394C2A52CF98279", typeof(bool)),
                    Actor("OnDisable", 0x271E4B0, 272, "FD2C34DE88EFC56D8234563D392D16A6E67C841796FDA905B39430B45E17A937"),
                    Actor("OnDestroy", 0x271E5C0, 656, "2B8167C70D95F8A2190829C33BC280287FF93EA1449DE6C37C4875CFC29C2128"),
                    Q(typeof(VailActorGolfCartAction), "Sons_Gameplay_IGolfCartAction_Initialize", 0x328DA40, 272,
                        "D059B1B7F2F73FF3BAE8002FBCE990DBDC0796D7CF7E969FB7BECA3371B2575C", typeof(GolfCartController), typeof(int)),
                    Q(typeof(VailActorGolfCartAction), "Sons_Gameplay_IGolfCartAction_Disconnect", 0x328DB50, 16,
                        "7142A15A89818528D3373816863921C29555C736474721E04257BB196275C50C", typeof(GolfCartController), typeof(bool)),
                    Q(typeof(VailActorGolfCartAction), "OnDisconnect", 0x328DB60, 448,
                        "2002FF718EDE69FD00206F8B9D727720B90A86E7D6298BE4CB355DE1C1BD9D3E"),
                    NetworkActor("TickClient", 0x27C4D10, 144, "682F79F2B9AA2CEA155CE6592653C65D4C2C05782B6216433BE7ACF2677D9EFD"),
                    NetworkActor("TickServer", 0x27C4DA0, 144, "87DD373792C48723F83BCE49D05D8DD90769BF39B97720AD79830D17B5EAD7B2"),
                    NetworkActor("OwnerUpdateStateVars", 0x27C4E30, 3552, "746B969B7D030110583D7D90FD93CADA3DDBFEBFC97198FD5DBE23FB8DA62808"),
                    NetworkActor("TickAttachedMovement", 0x27C66B0, 560, "EEAD8BF25E51B8D92E5DBFD68A9A28B3A653C581176FE3A7D6AC7BE9AB6186BC"),
                    Q(typeof(IkTargetsController), "LateUpdate", 0x3528DB0, 544,
                        "53DE3A36C6B7E25969A13A139049449E511044F5E70BFF44CC04CD48F00376CB"),
                    Q(typeof(IkTargetsController), "ForceUpdate", 0x3528FD0, 352,
                        "1DE2A5A3832B745A654EE75B1F22CA62DFA10F4DC13FDFEC039870C7F602D1AC")
                });
                actorPairs.Add(Actor("PushApart", 0x2751710, 592,
                    "4E27754E169F5923620A20FFC261A310D3F00C25EF7E0754878E59EF30A28299", typeof(VailActor), typeof(float), typeof(float)));
                actorFirst.AddRange(new[]
                {
                    Stimulus("OnActionInterrupted", 0x2856D70, 144, "90B1993F244048B5D6399AE53BC9DF226FCC681EC091DE93922E660B07E30CD6", typeof(VailActor)),
                    Stimulus("ClearActorSeatTarget", 0x28572A0, 384, "1419A66160844C1FC8739C65AAEBA14917673E496008B944CF28A605C6B5FB56", typeof(VailActor)),
                    Stimulus("OnActorExitCart", 0x2857530, 144, "8AA3409406F19549BF3BFC73D4EE905BC3B4EF392B1D2E6DDACEF8062EE6D047", typeof(VailActor)),
                    Stimulus("UnRegister", 0x28575C0, 112, "EEE7A3DFEE32C7A6BF3E8037E5385BC6FBBBE183EFBB8FA8A38374DEF3F77D46", typeof(VailActor)),
                    Q(typeof(VailActorManager), "RemoveActorInternal", 0x278E580, 320,
                        "D43BEAC83C3143C7A5C38914C1B94E5ADA7576534499741132FB5D503E403BBE", typeof(VailActor)),
                    Q(typeof(VailActorManager), "DestroyActor", 0x278E800, 272,
                        "92EA1FE63AC77A4F99F3B889F7A02AB7EB9ED19F0A8C6D0A04EED93D99E553B1", typeof(VailActor)),
                    Q(typeof(VailActorManager), "ClearActorManagedStimuli", 0x27956C0, 400,
                        "C3E482902DAD31AF2C39E0CF4BEBDF0D28202260D6DEB9A3B8FC6D5E1DA3B911", typeof(VailActor))
                });
                actorSecond.AddRange(new[]
                {
                    Stimulus("SetActorTargetSeat", 0x2857210, 144, "C246BEA01A854C89983D174D3034495BCED0693D93AA9603A76ACBC0BE9EDD71", typeof(int), typeof(VailActor)),
                    Stimulus("SetActorConnectedToCart", 0x2857420, 272, "C07E46D3D5523DE5C1FBB7B7484F5FE617466E1834626016410453D30FC9B0B2", typeof(int), typeof(VailActor))
                });

                // Use the assembly of the exact generated property, without adding a shared
                // project reference. Never hook SolverManager's shared empty virtual methods.
                var limb = typeof(IkTargetsController).GetProperty("_leftHandLimb", BindingFlags.Instance | BindingFlags.Public);
                Require(limb != null, "The qualified RootMotion limb API is unavailable.");
                var manager = limb.PropertyType.Assembly.GetType("RootMotion.SolverManager", true);
                var solver = limb.PropertyType.Assembly.GetType("RootMotion.FinalIK.IKSolver", true);
                receivers.AddRange(new[]
                {
                    Q(manager, "Update", 0x258E720, 64, "BF937F690585B17C375A6685F44B1D3A28E674766922CE84DD658CC5B50491D5"),
                    Q(manager, "FixedUpdate", 0x258EC70, 80, "989F887631F4FA5B9048B5B6101D07A88EB4B5359EF8628F57641EB3761E5869"),
                    Q(manager, "LateUpdate", 0x258ECC0, 80, "0264D367F0BC4F050438D1F635B54A01A1EB4344785ADA324DF007659CDA2781"),
                    Q(manager, "UpdateSolverExternal", 0x258ED10, 112, "734AFE8F171EBC80753D51EB058578DF5FF4FC9C970ED4C64A5BDE083E8040C7"),
                    Q(solver, "Update", 0x25C8EA0, 112, "8D73CBC1243A03552948133F12034998E7E0922F29EBEF5D157910B5E2DCA7A1")
                });
            }

            private static MethodInfo Actor(string name, int rva, int length, string hash, params Type[] parameters)
                => Q(typeof(VailActor), name, rva, length, hash, parameters);
            private static MethodInfo NetworkActor(string name, int rva, int length, string hash)
                => Q(typeof(VailActorNetworked), name, rva, length, hash);
            private static MethodInfo Stimulus(string name, int rva, int length, string hash, params Type[] parameters)
                => Q(typeof(GolfCartStimuli), name, rva, length, hash, parameters);
        }
    }
}
