// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using Endnight.Utilities;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.Gameplay;
using global::Sons.Gameplay.Swimming;
using global::Sons.Physics;
using UnityEngine;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>Observe startup and coroutine lifetimes before gameplay. Writer gates arm only on explicit preparation.</summary>
        internal static void InstallCartLifecycleHooks(HarmonySupportSnapshot support, Action<string> warning)
            => CartWriterHooks.Install(support, warning);

        internal static bool TryGetGolfCartCandidates(out IReadOnlyList<Behaviour> carts)
            => CartWriterHooks.TryGetCandidates(out carts);

        // Native OnDisable unregisters interactions, LastUpdate, cloud and time receivers, but NOT
        // MainThreadRunner's cart actions. A queued runner action and Bolt callbacks also need a gate.
        private static class CartWriterHooks
        {
            private static readonly HashSet<IntPtr> Started = new();
            private static readonly Dictionary<IntPtr, int> StartFrames = new();
            private static readonly Dictionary<IntPtr, IntPtr> ProxyFixes = new();
            private static readonly Dictionary<IntPtr, Behaviour> Candidates = new();
            // Occupant/event callbacks whose original ordering cannot be replayed after a handoff.
            private static readonly HashSet<string> OrderedCallbacks = new(StringComparer.Ordinal)
            {
                "OnEvent", "OnConnectedSlotChanged", "ConnectSlot", "DisconnectSlot", "OnDeserialize", "Attached",
                "OnSerialize", "ChargeBattery", "DrainBattery", "TriggerShove", "OnCollisionEnter", "SetHandBrake",
                "OnActivated", "OnDeactivated", "TriggerActivated", "OnEnable", "RegisterThreads",
                "SetFluidDensity", "SetFluidPressure", "DelayedFixPlayerProxyRotation"
            };
            private static IReadOnlyList<Behaviour> _candidates = Array.Empty<Behaviour>();
            private static bool _candidatesSeeded, _candidatesChanged;
            private static int _candidateFailure;
            private static MethodInfo _enableMethod;
            private static HeldWriter[] _heldWriters = Array.Empty<HeldWriter>();
            private static bool _attempted;
            private static bool _writerAttempted;
            private static volatile bool _writersInstalled;
            private static int _ownerThread;
            private static int _callbackDepth;
            private static int _historyIncomplete;
            private static int _activationFrame;
            private static string _writerFailure;
            private static SharedFloatSetter[] _floatSetters;
            internal static bool Installed { get; private set; }
            [ThreadStatic] private static string _allowedMethod;
            [ThreadStatic] private static CartWriterHold _allowedHold;

            private readonly record struct HeldWriter(IntPtr Pointer, CartWriterHold Hold);
            private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _ownerThread;

            internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
            {
                if (_attempted) return;
                _attempted = true;
                HarmonyLib.Harmony harmony = null;
                string stage = "qualifying passenger exit boundaries";
                try
                {
                    if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized) return;
                    Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                        "Cart lifecycle observation requires the qualified Windows x64 build.");
                    QualifyCartPassengerExitHooks();
                    stage = "qualifying cart lifecycle boundaries";
                    var start = Cart("Start", 0x33C2D60, 528, "EF0BD5C5EE16DA01F936D1CFF827FA4868C851F1F709AB9AE0B287D2E6029CC2");
                    var destroy = Cart("OnDestroy", 0x33C5380, 112, "F6CBDC8CE371083B9C929F94EEA4570984279324A7752A98AEB40C5EE0263721");
                    _enableMethod = Cart("OnEnable", 0x33C4EA0, 656, "E50A601F794FB4A887ABB2117757380C54DAFA18B54F71C1EE0352F137649963");
                    var factory = Qualify(typeof(GolfCartController), "DelayedFixPlayerProxyRotation",
                        typeof(Il2CppSystem.Collections.IEnumerator), 0x33D0720, 256,
                        "DA2727B43DBB186D4C35721BE79C1B412239513A3B035F72DEA90E1DA8E484A6", typeof(Transform));
                    var iterator = typeof(GolfCartController).GetNestedType("_DelayedFixPlayerProxyRotation_d__270",
                        BindingFlags.Public | BindingFlags.NonPublic);
                    var step = Qualify(iterator, "MoveNext", typeof(bool), 0x33D5FB0, 544,
                        "B499DA3F37CD163ED6C8B1A1FA69AAB3EBB75E6F62C6DF0BFAD6B947B6C7C39A");

                    // No cart/sensor/float update methods are patched until explicit preparation.
                    _ownerThread = Environment.CurrentManagedThreadId;
                    harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-cart-lifecycle");
                    stage = "patching GolfCartController.OnEnable observation";
                    harmony.Patch(_enableMethod, prefix: new HarmonyMethod(typeof(CartWriterHooks), nameof(ObserveCandidate))
                        { priority = Priority.First });
                    stage = "patching GolfCartController.Start";
                    harmony.Patch(start, prefix: Hook(nameof(BeforeStart)), postfix: Hook(nameof(StartCompleted)),
                        finalizer: Hook(nameof(FinishStart)));
                    stage = "patching GolfCartController.OnDestroy";
                    harmony.Patch(destroy, prefix: Hook(nameof(Before)), finalizer: Hook(nameof(FinishDestroy)));
                    stage = "patching GolfCartController.DelayedFixPlayerProxyRotation";
                    harmony.Patch(factory, prefix: Hook(nameof(Before)), postfix: Hook(nameof(ProxyFixCreated)),
                        finalizer: Hook(nameof(After)));
                    stage = "patching GolfCartController delayed proxy rotation MoveNext";
                    harmony.Patch(step, prefix: Hook(nameof(BeforeProxyStep)), finalizer: Hook(nameof(ProxyFixStepped)));
                    Installed = true;
                    RuntimeEntryPoint.LogSource?.LogInfo("Cart lifecycle hooks installed; migration writers remain opt-in.");
                }
                catch (Exception error)
                {
                    Installed = false;
                    string rollback = "";
                    try { harmony?.UnpatchSelf(); }
                    catch (Exception cleanup) { rollback = " Patch rollback is unconfirmed: " + cleanup.GetType().Name + ": " + cleanup.Message; }
                    warning?.Invoke("Cart lifecycle hooks unavailable while " + stage + " (" + error.GetType().Name
                        + "): " + error.Message + rollback);
                }
            }

            private static HarmonyMethod Hook(string name) => new(typeof(CartWriterHooks), name) { priority = Priority.Last };

            internal static bool TryGetCandidates(out IReadOnlyList<Behaviour> carts)
            {
                carts = Array.Empty<Behaviour>();
                if (!Installed || !OnOwnerThread || _callbackDepth != 0
                    || Volatile.Read(ref _historyIncomplete) != 0 || Volatile.Read(ref _candidateFailure) != 0) return false;
                try
                {
                    if (!_candidatesSeeded)
                    {
                        // Seeding is object discovery, never evidence that native Start completed.
                        foreach (var cart in UnityEngine.Object.FindObjectsOfType<GolfCartController>()) AddCandidate(cart);
                        _candidatesSeeded = true;
                    }
                    foreach (var pair in Candidates)
                    {
                        if (Live(pair.Value)) continue;
                        Candidates.Remove(pair.Key);
                        _candidatesChanged = true;
                    }
                    if (_candidatesChanged)
                    {
                        var snapshot = new Behaviour[Candidates.Count];
                        Candidates.Values.CopyTo(snapshot, 0);
                        _candidates = Array.AsReadOnly(snapshot);
                        _candidatesChanged = false;
                    }
                    if (Volatile.Read(ref _historyIncomplete) != 0 || Volatile.Read(ref _candidateFailure) != 0) return false;
                    carts = _candidates;
                    return true;
                }
                catch (Exception error)
                {
                    FailCandidates(error);
                    return false;
                }
            }

            private static void ObserveCandidate(GolfCartController __instance)
            {
                if (!Installed || Volatile.Read(ref _candidateFailure) != 0) return;
                try
                {
                    Require(OnOwnerThread, "Cart candidate observation left the game thread.");
                    AddCandidate(__instance);
                }
                catch (Exception error) { FailCandidates(error); }
            }

            private static void AddCandidate(GolfCartController cart)
            {
                if (!Live(cart)) return;
                if (Candidates.TryGetValue(cart.Pointer, out var current) && Live(current)) return;
                Candidates[cart.Pointer] = cart;
                _candidatesChanged = true;
            }

            private static void FailCandidates(Exception error)
            {
                if (Interlocked.Exchange(ref _candidateFailure, 1) == 0)
                    RuntimeEntryPoint.LogSource?.LogWarning("Cart candidate catalog is unavailable; use native discovery: " + error);
            }

            private static void ActivateWriters()
            {
                if (_writerAttempted) return;
                _writerAttempted = true;
                HarmonyLib.Harmony harmony = null;
                try
                {
                    // Qualify the complete set before changing any native entry. NetworkFixedUpdate
                    // is the shared ret stub (8,156 aliases), not a writer and never a patch target.
                    _ = Cart("NetworkFixedUpdate", 0x597160, 16, "28F844B5972C241E804231B0E41B93E13696E59B98DDE74F19029386DB5DA0C5");
                    var methods = new[]
                    {
                        // Qualified before passive observation patched the same native entry.
                        _enableMethod,
                        Cart("OnDisable", 0x33C5130, 592, "C33C2D64BB5D8B63E1D5488A12E8764359DAC747F2B131C08B81A5DD25877F41"),
                        Cart("Attached", 0x33CD940, 576, "6896745FA6F6F0B5F2D37A38BDC66FE52A241433FF1486FBAA419089BA5B7D64"),
                        Cart("RegisterThreads", 0x33CD660, 288, "9B779F90748362C2C5613255B178A1BE9D6BD754A9CD2E80F5BEE4BBE8D4E6E6", typeof(bool)),
                        Cart("ManagedFixedUpdate", 0x33C31B0, 1152, "733BA71A1CD72B580775CCE78348AE6319F046EB50273EC851AAC9327A722402"),
                        Cart("ManagedLateUpdate", 0x33C3640, 2112, "2FB52B96169970274D6E5FE59503AE56D632A1CBF39E060DE6CF9CBBA60F4CD1"),
                        Cart("LateUpdate", 0x33C3630, 16, "B486B906CECE34212A1C0EE9890491E448B7759ED319E9F84C43E4D1D0A6A76B"),
                        Cart("LastUpdate", 0x33C5810, 720, "800D89DD9D164272534F8791CEDA15FEB9B0123DE68BD530BCF09D9000A30956"),
                        Cart("NetworkLateUpdate", 0x33CE770, 16, "7142A15A89818528D3373816863921C29555C736474721E04257BB196275C50C"),
                        Cart("NetworkLastUpdate", 0x33CDEC0, 80, "49228A148645C4E1847DFDF96CE8B57CF64CA759908103E74F347D1D37678126"),
                        Cart("UpdateCloud", 0x33CC6D0, 16, "B376B3E89D3F4DF8F7AB3382CC246D8EDF6D1C58FE9B1F8B5512B9EB16279FA8", typeof(float)),
                        Cart("UpdateTime", 0x33CC6E0, 32, "AAB4CFA3A92D46AD270D2D217F11CC6E22782681699892210B0FF948D002C697",
                            typeof(global::Sons.Environment.TimeOfDay), typeof(global::Sons.Environment.TimeOfDay)),
                        Cart("OnActivated", 0x33CD370, 16, "09FED57B0B79E278DB6CDB3EDC0E7D073EF6455499D64FB0E74253A9A0481B82"),
                        Cart("OnDeactivated", 0x33CD380, 16, "32431054961B4BAF7F2DDEF7E0B9D738BD2AE61004F2739E2915B0A0DE2C51F2"),
                        Cart("TriggerActivated", 0x33CD390, 720, "A3E24F29C5D501EF8EEE13A2C9C8572F80EDCB4DFC8C798F5874338F2CAC1516", typeof(bool)),
                        Cart("OnConnectedSlotChanged", 0x33CF250, 608, "E0F8C7ECAB47AE23D4A66727B8FB060ED13CDE53A40065C5A5D365CBA5F4C07A",
                            typeof(global::Bolt.IState), typeof(string), typeof(global::Bolt.ArrayIndices)),
                        Cart("RefreshConnectedSlots", 0x33CDB80, 496, "2291D07712D186BF251F04535157DD87D94F6CF69ECF5EB3B9C7682A9B521E3B"),
                        Cart("OnEvent", 0x33D4720, 240, "A181C931DECF70912683E1C6FCB5AC939349F5CD7BF2B3AB7E8049EB22AF78A8", typeof(GolfCartShoveEvent)),
                        Cart("OnEvent", 0x33D4810, 240, "9509AF66BE3F711F4D1F8E8FAAD55BC36EB94DF2A86E7D70F286997F82A8A402", typeof(GolfCartConnectSlotEvent)),
                        Cart("ConnectSlot", 0x33D4900, 576, "4090383F9D48FEB27BB44ABEF0F1B55E573D149EF5E2937DB62100C3B183043B", typeof(GolfCartConnectSlotEvent)),
                        Cart("DisconnectSlot", 0x33D4B40, 672, "3A53B4A367038E6726C693D4C0BF572B2D925D3E4261A552CC25886EDB4D70CD", typeof(GolfCartConnectSlotEvent)),
                        Cart("OnDriverInteraction", 0x33C7610, 800, "B80EDB9474F036A20C00513F1BF14E213C8CD7D5C158885A6F79CE844DD956E3"),
                        Cart("OnPassengerAInteraction", 0x33C7C40, 16, "6C499956D332506B7469EC4C6E201C41721E6CECA4AE9E93F52C39906776E348"),
                        Cart("OnPassengerBInteraction", 0x33C7C50, 16, "A5D09355B56656C527BE236D816FE41B7FAA878657394B1732C8D0593A6F894F"),
                        Cart("OnPassengerCInteraction", 0x33C7C60, 16, "300757D9E6EF7AFA258ADA3F1F09D4EDF369AFCA3F5E3DA13985D3255686202C"),
                        Cart("OnActorPassengerInteraction", 0x33C83E0, 400, "3222BDEC1D3B950EC3D46F88DFBD54E3FD39A3BD8A724778A31C39871C55D882", typeof(int), typeof(global::Sons.Ai.Vail.VailActor)),
                        Cart("OnPlayerPassengerInteraction", 0x33C8570, 640, "D258BC0C4B9E56BDD660D7246E2492A753034F6BD087982E109E168B718DC78E", typeof(int)),
                        Cart("OnShovedInteraction", 0x33C6A00, 624, "0CE8AA25FAD5C5622634D8AD2267F150363BA304218C82F251144EE92877CAF5"),
                        Cart("TriggerShove", 0x33C6C70, 2096, "ABC642109CF9008CEABF9FF34745AC0E911C0B98323F77F713AA51DAB8A9C373", typeof(Vector3)),
                        Cart("OnCollisionEnter", 0x33C3E80, 704, "2A74D1EAD57325F03B96C9D58C850194D859F2352D2E41B769D2F903DF6A8363", typeof(Collision)),
                        Cart("OnSerialize", 0x33C2F70, 400, "F389BA924655EF278B10FB7501C8E554E55BF79B8BA88031F248E16EA7343F1F", typeof(global::Sons.Inventory.WorldItemState)),
                        Cart("OnDeserialize", 0x33C3100, 176, "382E4C2D096003701B810AE8000213749AFFC60F67DC847D47EC9CAB59C1348A", typeof(global::Sons.Inventory.WorldItemState)),
                        Cart("ChargeBattery", 0x33D52C0, 272, "FB6BAFBA2F1A53425E7465A7CA99149824B516D9045AA8089FEF75678FC1304A"),
                        Cart("DrainBattery", 0x33D53D0, 256, "191A07E4628A04E13AFBB061E34A28EFE9EA539F4A3423B03C075425183138AD"),
                        Cart("UpdateAudio", 0x33C0A80, 560, "A31DDFF89009786CDDAB4B9A11712DA15121257F6075988B19A0A8BF19BCC8BA"),
                        Cart("UpdatePower", 0x33D4E70, 1040, "F726B0C9B1F0985C46D3039E527A11F6E2B8FA3AEE101E730D2CC2FB8B914687"),
                        Cart("UpdateInWater", 0x33C9850, 176, "8DD6A2C855E48698813ABC404DC29D2B21F991C8469548D12CDF75C366A9416A"),
                        Cart("SetHandBrake", 0x33CC5E0, 16, "042828751F7E7FAF00BB0BA12C377C11E3DB4B74514C7771AD6952EC57C341AA", typeof(bool)),
                        Cart("SetLights", 0x33C46A0, 736, "34FFCFBDC8BF670DA0499CE3E6D3E6E54647E8D2AA7C742BF5D98B4394602869", typeof(bool)),
                        Cart("StartIdleBrakeTimer", 0x33CC330, 368, "9A1DBA757C21D28F29408D70BAE9B833B166381331DF3D58A439BA57423EFEB7", typeof(float)),
                        Cart("UnconstrainToGhost", 0x33D01E0, 1344, "CCCF2B20DCB855D343093EA16578D7CBD86649F61C1009736810924077909BFE"),
                        Cart("UnconstrainPassenger", 0x33D0820, 160, "D1EED2E509820E6BED29854C31EB38A2317D898CCDE4099EBE24640AE8B91A10", typeof(int)),
                        Cart("ConstrainToGhost", 0x33D0BA0, 624, "824F72E6FBA6999E069735C33A3FB45C2F03767C37DC4D9CC4C2161646800D90", typeof(BoltEntity)),
                        Cart("ConstrainPassenger", 0x33D1440, 1440, "D58DA6F0F82FD7804B12B50D7AA93C20E833EE86AF416E78C8B6ABFD80EFC2B2", typeof(BoltEntity), typeof(int)),
                        Sensor("OnEnable", 0x2E7BC50, 160, "508659E0D3916D425B85FE9C5937B5A48558326735A16284889469DC284A1C87"),
                        Sensor("OnDisable", 0x2E7BCF0, 48, "8D0BE26C4A5233A7FFBB0124587D74E06D28D27B006DEBE8A56AEFC0ADE11E5A"),
                        Sensor("ManagedUpdate", 0x2E7BD20, 784, "4D4F24FED4C4CE41B7D0DDB48F06718A89ED58CAC020DA43C429437A46653B9E"),
                        Sensor("OnTriggerEnter", 0x2E7EF00, 256, "6831384AD2F5637AF4DBD76A9B63DA08A7500FE11DFEFE4DC279F11984C4498A", typeof(Collider)),
                        // The only alias is WaterLevelSensor.TryRemoveCollider(Collider), with the same receiver and ABI.
                        Sensor("OnTriggerExit", 0x2E7F000, 144, "F3338DA6EBBF3920847407BBC7F0F23932D9DFD45CC074DD7798C433DF74713C", typeof(Collider)),
                        Sensor("UpdateTriggerStay", 0x2E7C030, 880, "3D10081CAF28A37D09299FD4B5AAB9410D4E99C1BDD416BA74D309502233DE38"),
                        Sensor("UpdateWaterDataCache", 0x2E7C6B0, 1952, "874F7078C6E78FCE4563F3DA3424B2E745119BCB9CB744330487BF0F2B088D8C"),
                        Sensor("ForceReset", 0x2E7B830, 240, "211EB83DB975EF497C0EB67C883F6D57986F72580225C7D4670D3A2579403841"),
                        Float("OnEnable", 0x2E65CF0, 496, "58B0D43D33D212622040680C40B9216B59995C58EF2CC44F45329471C5EC3F58"),
                        Float("OnDisable", 0x2E65CC0, 48, "4828C6C50EB4E21767A2EF6743E8A63CF1F84E0D0228BCE1A6EA25F0818CD208"),
                        Float("FixedUpdateManaged", 0x2E66120, 1984, "3392B71D1D668F61810013651A4B11BA936A8CE195BA1F3CF19045B8974CEE19"),
                        Float("SetWaterLoggedFactor", 0x2E652B0, 48, "18E5EF0DD927BB8D31AC844149126960148089F572025C4189C5D4C202FABDB4", typeof(float))
                    };
                    // These two stores also serve unrelated classes and structs. Never put a
                    // typed Harmony receiver on them; raw gates compare retained pointers only.
                    _floatSetters = new[]
                    {
                        new SharedFloatSetter("SetFluidDensity", 0xB57260, "795D126FD21D51AA9A3286CDA6A445AAD1115577F417F5F9DA3BD4440FB768E0"),
                        new SharedFloatSetter("SetFluidPressure", 0x2A3D270, "3B5F2F99D11C90FAF68CC71CB44C29E8225746B8CDEF9DEE66A5F4665A187690")
                    };
                    var driverMethods = CartDriverAuthorityHooks.Qualify();
                    harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-cart-writers");
                    foreach (var method in methods)
                        harmony.Patch(method, prefix: Hook(nameof(BeforeWriter)),
                            finalizer: Hook(method.DeclaringType == typeof(GolfCartController) && method.Name == "Attached"
                                ? nameof(AfterAttached) : nameof(AfterWriter)));
                    CartDriverAuthorityHooks.Patch(harmony, driverMethods, Hook(nameof(BeforeWriter)), Hook(nameof(AfterWriter)));
                    foreach (var setter in _floatSetters) setter.Install();
                    _activationFrame = Time.frameCount;
                    _writersInstalled = true;
                }
                catch (Exception error)
                {
                    _writerFailure = "Cart writer activation failed: " + error.Message;
                    try { harmony?.UnpatchSelf(); } catch (Exception) { }
                    // Any residual hooks are passive. Raw callbacks/trampolines remain rooted.
                }
            }

            private static MethodInfo Cart(string name, int rva, int length, string hash, params Type[] parameters)
                => Qualify(typeof(GolfCartController), name, typeof(void), rva, length, hash, parameters);
            private static MethodInfo Sensor(string name, int rva, int length, string hash, params Type[] parameters)
                => Qualify(typeof(WaterLevelSensor), name, typeof(void), rva, length, hash, parameters);
            private static MethodInfo Float(string name, int rva, int length, string hash, params Type[] parameters)
                => Qualify(typeof(FloatController), name, typeof(void), rva, length, hash, parameters);

            internal static MethodInfo Qualify(Type type, string name, Type result, int rva, int length, string hash, params Type[] parameters)
            {
                var method = type == null ? null : AccessTools.DeclaredMethod(type, name, parameters);
                if (method == null || method.IsStatic || method.ReturnType != result)
                    throw new MissingMethodException(type?.FullName, name);
                string boundary = type.FullName + "." + name;
                var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field?.GetValue(null) is not nint info || info == 0)
                    throw new InvalidOperationException("Missing cart lifecycle method information: " + boundary);
                var entry = (nint)BitConverter.ToInt64(ReadMemory(info, IntPtr.Size), 0);
                var module = GetModuleHandle("GameAssembly.dll");
                Require(module != 0, "GameAssembly.dll is unavailable while qualifying cart boundary: " + boundary);
                if (entry != module + rva)
                    throw new InvalidOperationException($"Cart lifecycle native entry differs: {boundary}; expected RVA 0x{rva:X}, "
                        + $"actual offset 0x{entry.ToInt64() - module.ToInt64():X}." + PatchOwners(method));
                string actualHash = Convert.ToHexString(SHA256.HashData(ReadMemory(entry, length)));
                if (actualHash != hash)
                    throw new InvalidOperationException($"Cart lifecycle native bytes differ: {boundary}; RVA 0x{rva:X}, "
                        + $"length {length}, expected SHA256 {hash}, actual {actualHash}." + PatchOwners(method));
                return method;
            }

            private static string PatchOwners(MethodInfo method)
            {
                // Keep unknown-byte refusal even when a familiar Harmony owner is registered.
                var patches = HarmonyLib.Harmony.GetPatchInfo(method);
                return patches != null && patches.Owners.Count != 0
                    ? " Registered Harmony owners: " + string.Join(", ", patches.Owners)
                        + ". Cart qualification must precede overlapping hook installation."
                    : " No Harmony patch owner is registered; changed bytes or an unregistered native detour remain possible.";
            }

            private static byte[] ReadMemory(nint address, int length)
            {
                var bytes = new byte[length];
                if (!ReadProcessMemory(new nint(-1), address, bytes, (nuint)length, out nuint read) || read != (nuint)length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cart lifecycle native validation failed.");
                return bytes;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
            private static extern nint GetModuleHandle(string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] bytes, nuint size, out nuint read);

            private static bool BeforeWriter(Component __instance, MethodBase __originalMethod, bool __runOriginal, out bool __state)
            {
                __state = false;
                if (!_writersInstalled) return true;
                if (!Before(__instance, __originalMethod, __runOriginal, out __state)) return false;
                return !__runOriginal || CartDriverAuthorityHooks.Before(__instance, __originalMethod.Name);
            }

            private static Exception AfterWriter(Component __instance, MethodBase __originalMethod,
                bool __state, bool __runOriginal, Exception __exception)
            {
                try
                {
                    if (__state) CartDriverAuthorityHooks.After(__instance, __originalMethod.Name, __runOriginal, __exception);
                    return __exception;
                }
                finally { After(__instance, __state, __runOriginal, __exception); }
            }

            private static bool Before(Component __instance, MethodBase __originalMethod, bool __runOriginal, out bool __state)
            {
                __state = false;
                if (!Installed || !__runOriginal || __instance is null) return true;
                if (!CartReplacementGate.Allow(__instance, __originalMethod.Name)) return false;
                var writers = Volatile.Read(ref _heldWriters);
                if (writers.Length != 0 && !AllowWriter(writers, __instance.Pointer, __originalMethod.Name)) return false;
                EnterCallback(out __state);
                return true;
            }

            private static void EnterCallback(out bool entered)
            {
                entered = false;
                if (!OnOwnerThread) { Interlocked.Exchange(ref _historyIncomplete, 1); return; }
                ++_callbackDepth;
                entered = true;
            }

            private static CartWriterHold FindHold(HeldWriter[] writers, IntPtr pointer)
            {
                foreach (var writer in writers) if (writer.Pointer == pointer) return writer.Hold;
                return null;
            }

            private static bool AllowWriter(HeldWriter[] writers, IntPtr pointer, string name)
            {
                var hold = FindHold(writers, pointer);
                if (hold == null) return true;
                if (!OnOwnerThread)
                {
                    Interlocked.Exchange(ref _historyIncomplete, 1);
                    Interlocked.CompareExchange(ref hold.Conflict, "A held cart writer arrived outside the game thread.", null);
                    return false;
                }
                if (name == "OnDisable" || name == "OnDestroy"
                    || (name == "OnSerialize" && hold.SerializeAllowed)
                    || (ReferenceEquals(_allowedHold, hold) && _allowedMethod == name)) return true;
                // A lease has already repaired processed-seat caches for its exact raw writes.
                // Suppress only matching notifications; any differing seat still quarantines.
                if (name == "OnConnectedSlotChanged" && OccupantsFor(hold.Binding.Cart)?.OwnSeatNotification(hold.Binding) == true)
                    return false;
                // Never replay an occupant/event callback whose original ordering is lost.
                if (OrderedCallbacks.Contains(name) || name.StartsWith("Constrain", StringComparison.Ordinal)
                    || name.StartsWith("Unconstrain", StringComparison.Ordinal) || name.Contains("Interaction", StringComparison.Ordinal))
                    Interlocked.CompareExchange(ref hold.Conflict, "Native " + name + " arrived during the cart handoff.", null);
                return false;
            }

            private static Exception After(Component __instance, bool __state, bool __runOriginal, Exception __exception)
            {
                if (!__state) return __exception;
                --_callbackDepth; // Balance even if a later Harmony prefix skipped the original.
                if (__runOriginal && __exception != null)
                {
                    var hold = FindHold(Volatile.Read(ref _heldWriters), __instance.Pointer);
                    if (hold != null)
                        Interlocked.CompareExchange(ref hold.Conflict, "A native lifecycle callback failed during the cart handoff.", null);
                }
                return __exception;
            }

            private static Exception AfterAttached(Component __instance, bool __state, bool __runOriginal, Exception __exception)
            {
                var result = After(__instance, __state, __runOriginal, __exception);
                if (__state && __runOriginal && __exception == null) CartReplacementGate.AttachedCompleted(__instance);
                return result;
            }

            private static bool BeforeStart(GolfCartController __instance, MethodBase __originalMethod, bool __runOriginal, out bool __state)
            {
                ObserveCandidate(__instance);
                bool run = Before(__instance, __originalMethod, __runOriginal, out __state);
                if (__state) Started.Remove(__instance.Pointer);
                return run;
            }

            private static void StartCompleted(GolfCartController __instance, bool __state, bool __runOriginal)
            {
                // A postfix is not reached after a native exception, unlike a finalizer.
                if (__state && __runOriginal)
                {
                    Started.Add(__instance.Pointer);
                    StartFrames[__instance.Pointer] = Time.frameCount;
                }
            }

            private static Exception FinishStart(Component __instance, bool __state, bool __runOriginal, Exception __exception)
            {
                if (__state && __exception != null) Started.Remove(__instance.Pointer);
                return After(__instance, __state, __runOriginal, __exception);
            }

            private static Exception FinishDestroy(Component __instance, bool __state, bool __runOriginal, Exception __exception)
            {
                if (__state) CartDriverAuthorityHooks.After(__instance, "OnDestroy", __runOriginal, __exception);
                if (__state && __runOriginal)
                {
                    if (__exception != null) FailCandidates(__exception);
                    else if (Candidates.Remove(__instance.Pointer)) _candidatesChanged = true;
                    Started.Remove(__instance.Pointer);
                    StartFrames.Remove(__instance.Pointer);
                    foreach (var pair in ProxyFixes)
                        if (pair.Value == __instance.Pointer) ProxyFixes.Remove(pair.Key);
                }
                return After(__instance, __state, __runOriginal, __exception);
            }

            private static void ProxyFixCreated(GolfCartController __instance, Il2CppSystem.Collections.IEnumerator __result,
                bool __state, bool __runOriginal)
            {
                if (__state && __runOriginal && __result != null) ProxyFixes[__result.Pointer] = __instance.Pointer;
            }

            private static void BeforeProxyStep(bool __runOriginal, out bool __state)
            {
                __state = false;
                if (Installed && __runOriginal) EnterCallback(out __state);
            }

            private static Exception ProxyFixStepped(Il2CppObjectBase __instance, bool __result, bool __state,
                bool __runOriginal, Exception __exception)
            {
                if (__state)
                {
                    --_callbackDepth;
                    if (__runOriginal && (!__result || __exception != null)) ProxyFixes.Remove(__instance.Pointer);
                }
                return __exception;
            }

            private static void RequireQuiescent()
            {
                Require(Installed && OnOwnerThread && OnSelectedThread(), "Cart lifecycle acquisition requires the selected game thread.");
                Require(Volatile.Read(ref _historyIncomplete) == 0, "Cart lifecycle observation left the game thread; writer history is incomplete.");
                Require(_callbackDepth == 0 && _allowedHold == null, "A native vehicle callback is executing; call outside vehicle callbacks.");
            }

            internal static void RequireInitialized(GolfCartController cart)
            {
                RequireQuiescent();
                Require(Started.Contains(cart.Pointer), "Native cart Start must complete under lifecycle observation before preparation.");
                Require(!ProxyFixes.ContainsValue(cart.Pointer), "A pending proxy-rotation coroutine retains a player reference.");
                ActivateWriters();
                Require(_writersInstalled, _writerFailure ?? "Cart writer hooks are unavailable.");
                // Activation may originate inside a previously unhooked callback. Never acquire
                // until a later Unity frame, when that synchronous native stack has returned.
                Require(Time.frameCount != _activationFrame, "Cart writers were armed this frame; prepare again on a later game frame.");
            }

            internal static bool HasSettledStart(GolfCartController cart)
                => Live(cart) && Started.Contains(cart.Pointer) && StartFrames.TryGetValue(cart.Pointer, out int frame)
                    && Time.frameCount > frame && !ProxyFixes.ContainsValue(cart.Pointer);

            internal static bool IsHeld(GolfCartController cart)
                => FindHold(Volatile.Read(ref _heldWriters), cart.Pointer) != null;

            internal static void Acquire(CartWriterHold hold)
            {
                RequireInitialized(hold.Binding.Cart);
                Require(!CartDriverAuthorityHooks.IsAcquired(hold.Binding.Cart),
                    "Complete the exact driver authority dismount before acquiring a cart migration hold.");
                var current = Volatile.Read(ref _heldWriters);
                foreach (var component in hold.Components)
                    Require(FindHold(current, component.Pointer) == null, "A cart writer is already held.");
                var next = new HeldWriter[current.Length + hold.Components.Length];
                Array.Copy(current, next, current.Length);
                for (int i = 0; i < hold.Components.Length; i++)
                    next[current.Length + i] = new(hold.Components[i].Pointer, hold);
                Volatile.Write(ref _heldWriters, next);
            }

            internal static void RequireHeld(CartWriterHold hold)
            {
                RequireQuiescent();
                Require(hold.Conflict == null, hold.Conflict);
                var writers = Volatile.Read(ref _heldWriters);
                foreach (var component in hold.Components)
                    Require(ReferenceEquals(FindHold(writers, component.Pointer), hold), "Cart writer barrier changed.");
            }

            internal static CartWriterHold RequireExistingHold(Binding binding)
            {
                var hold = FindHold(Volatile.Read(ref _heldWriters), binding.Cart.Pointer);
                Require(hold != null && hold.Binding.Id == binding.Id
                    && hold.Binding.Entity.Pointer == binding.Entity.Pointer, "The exact cart participant writer hold is required.");
                hold.RequireSuspended();
                return hold;
            }

            internal static void Release(CartWriterHold hold)
            {
                RequireHeld(hold);
                var current = Volatile.Read(ref _heldWriters);
                var next = new HeldWriter[current.Length - hold.Components.Length];
                int index = 0;
                foreach (var writer in current) if (!ReferenceEquals(writer.Hold, hold)) next[index++] = writer;
                Volatile.Write(ref _heldWriters, next);
            }

            internal static void Allow(CartWriterHold hold, string method, Action action)
            {
                Require(OnOwnerThread && _allowedHold == null, "Cart lifecycle callback reentry or wrong thread.");
                _allowedHold = hold; _allowedMethod = method;
                try { action(); }
                finally { _allowedHold = null; _allowedMethod = null; }
            }

            private sealed class SharedFloatSetter
            {
                [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
                private delegate void Setter(nint receiver, float value, nint methodInfo);
                private readonly string _name;
                private readonly int _rva;
                private readonly string _hash;
                private INativeDetour _detour;
                private Setter _callback, _original;

                internal SharedFloatSetter(string name, int rva, string hash)
                {
                    _name = name; _rva = rva; _hash = hash;
                    _ = Float(name, rva, 16, hash, typeof(float));
                }

                internal void Install()
                {
                    _ = Float(_name, _rva, 16, _hash, typeof(float));
                    _callback = Invoke;
                    _detour = INativeDetour.Create(GetModuleHandle("GameAssembly.dll") + _rva, _callback);
                    Volatile.Write(ref _original, _detour.GenerateTrampoline<Setter>());
                    _detour.Apply();
                }

                private void Invoke(nint receiver, float value, nint methodInfo)
                {
                    try
                    {
                        // The qualified originals are single stores with no callbacks. An unmatched
                        // receiver may be an unrelated value type: do not inspect or wrap it.
                        if (!_writersInstalled || AllowWriter(Volatile.Read(ref _heldWriters), receiver, _name))
                            Volatile.Read(ref _original)(receiver, value, methodInfo);
                    }
                    catch (Exception) { Interlocked.Exchange(ref _historyIncomplete, 1); }
                }
            }
        }

        private sealed class CartWriterHold
        {
            internal Binding Binding;
            internal readonly Component[] Components;
            internal CartLocalState State;
            internal string Conflict;
            internal bool SerializeAllowed;
            private readonly bool _active;
            private readonly bool _frozen;
            private readonly bool _enabled;
            private readonly bool _owner, _proxy;
            private readonly bool _threads;
            private readonly bool[] _runnerRegistrations;
            private readonly VehicleBodyValues _body;
            private bool _suspended;
            internal bool IsHeld => _suspended;

            internal CartWriterHold(Binding binding, bool allowReplica = false)
            {
                Require(CartWriterHooks.Installed, "Cart lifecycle hooks must be installed before gameplay.");
                CartWriterHooks.RequireInitialized(binding.Cart);
                RequireLifecycleCart(binding);
                Require(allowReplica || (binding.Entity.isOwner && !binding.Cart.IsNetworkProxy),
                    "Local cart handoff requires the actual native owner, not a network proxy.");
                Require(binding.Entity.transform.parent == null && !binding.Entity._detachOnDisable,
                    "A parented cart or detach-on-disable entity cannot be suspended without changing identity.");
                Binding = binding;
                _owner = binding.Entity.isOwner; _proxy = binding.Cart.IsNetworkProxy;
                _active = binding.Entity.gameObject.activeSelf;
                _frozen = binding.Entity.isFrozen;
                _enabled = binding.Cart.enabled;
                _body = CaptureBody(binding.Body);
                Require((binding.Cart._managedFixedUpdateThread == 0) == (binding.Cart._managedLateUpdateThread == 0),
                    "Partial cart runner registration cannot be reproduced.");
                _threads = binding.Cart._managedFixedUpdateThread != 0;
                var components = new List<Component> { binding.Cart };
                foreach (var sensor in binding.Entity.GetComponentsInChildren<WaterLevelSensor>(true)) components.Add(sensor);
                foreach (var floating in binding.Entity.GetComponentsInChildren<FloatController>(true)) components.Add(floating);
                Components = components.ToArray();
                _runnerRegistrations = new bool[Components.Length];
                for (int i = 1; i < Components.Length; i++)
                {
                    var component = Components[i];
                    bool registered = component is WaterLevelSensor sensor
                        ? sensor._cachedThreadRunnerInstanceId != 0
                        : ((FloatController)component)._cachedThreadRunnerInstanceId != 0;
                    Require(registered == ((Behaviour)component).isActiveAndEnabled,
                        "Water/buoyancy writer registration does not match its native enabled policy.");
                    _runnerRegistrations[i] = registered;
                }
                Require(Owned(binding.Cart._vacantWaterLevelSensor) && Owned(binding.Cart._activeWaterLevelSensor)
                    && Owned(binding.Cart._floatController), "Cart water/buoyancy writers are outside its hierarchy.");
                foreach (var component in Components)
                    if (component is FloatController floating)
                        Require(Owned(floating._waterLevelSensor), "Buoyancy references an external water sensor.");
                State = CartLocalState.Capture(binding, Components);
            }

            private bool Owned(Component component)
            {
                if (!Live(component)) return false;
                foreach (var current in Components) if (current.Pointer == component.Pointer) return true;
                return false;
            }

            internal void Suspend()
            {
                Require(!_suspended, "Cart writer hold is single-use.");
                CartWriterHooks.Acquire(this);
                _suspended = true;
                // MainThreadRunner copies its work list; the gate covers already queued actions.
                CartWriterHooks.Allow(this, "RegisterThreads", () => Binding.Cart.RegisterThreads(false));
                Binding.Entity.Freeze(true);
                Binding.Entity.gameObject.SetActive(false);
                // Sensors may already have an inactive ancestor and receive no new OnDisable.
                foreach (var component in Components)
                {
                    if (component is WaterLevelSensor sensor && sensor._cachedThreadRunnerInstanceId != 0)
                    {
                        int id = sensor._cachedThreadRunnerInstanceId;
                        MainThreadRunner.RemoveFromUpdate(ref id);
                        sensor._cachedThreadRunnerInstanceId = id;
                    }
                    else if (component is FloatController floating && floating._cachedThreadRunnerInstanceId != 0)
                    {
                        int id = floating._cachedThreadRunnerInstanceId;
                        MainThreadRunner.RemoveFromFixedUpdate(ref id);
                        floating._cachedThreadRunnerInstanceId = id;
                    }
                }
                State.PauseTimers(Binding.Cart);
                State.Restore(Binding, Components, false);
                if (CaptureBody(Binding.Body) != _body) RestoreBody(Binding.Body, _body);
                Require(CaptureBody(Binding.Body) == _body, "Suspension changed the captured Rigidbody values.");
                RequireSuspended();
            }

            internal void RequireSuspended()
            {
                CartWriterHooks.RequireHeld(this);
                Require(_suspended && Binding.Matches() && Binding.Entity.isOwner == _owner
                    && Binding.Cart.IsNetworkProxy == _proxy && Binding.Entity.isFrozen
                    && !Binding.Entity.gameObject.activeSelf && Binding.Cart.enabled == _enabled
                    && Binding.Cart._managedFixedUpdateThread == 0 && Binding.Cart._managedLateUpdateThread == 0,
                    "Cart resumed or its exact attachment/runner registration changed.");
                RequireLifecycleCart(Binding);
                State.RequirePausedTimers(Binding.Cart);
                State.RequireQuiet(Binding);
                var sensors = Binding.Entity.GetComponentsInChildren<WaterLevelSensor>(true);
                var floatingBodies = Binding.Entity.GetComponentsInChildren<FloatController>(true);
                Require(sensors.Length + floatingBodies.Length + 1 == Components.Length,
                    "The held vehicle's writer set changed.");
                foreach (var sensor in sensors) Require(Owned(sensor), "A water writer was replaced.");
                foreach (var floating in floatingBodies) Require(Owned(floating), "A buoyancy writer was replaced.");
                foreach (var component in Components)
                {
                    Require(Live(component), "A held cart component was destroyed.");
                    if (component is WaterLevelSensor sensor) Require(sensor._cachedThreadRunnerInstanceId == 0, "Water writer resumed.");
                    if (component is FloatController floating) Require(floating._cachedThreadRunnerInstanceId == 0, "Buoyancy writer resumed.");
                }
            }

            internal void RequireCompatible(CartWriterHold target)
            {
                Require(Components.Length == target.Components.Length && _enabled == target._enabled,
                    "Replacement native writer layout differs.");
                State.RequireCompatible(target.State);
            }

            internal void RequireCancellationState()
            {
                RequireSuspended();
                State.RequireUnchanged(Binding, Components);
                Require(CaptureBody(Binding.Body) == _body, "Original held physics changed before cancellation.");
            }

            internal void Rebind(Binding rebound)
            {
                Require(rebound.Id == Binding.Id && rebound.Entity.Pointer == Binding.Entity.Pointer
                    && rebound.Cart.Pointer == Binding.Cart.Pointer && rebound.Body.Pointer == Binding.Body.Pointer,
                    "Writer hold cannot move to a different attachment.");
                Binding = rebound;
            }

            internal void ReleaseRetired()
            {
                Require(!Live(Binding.Entity) && !Live(Binding.Saver), "Source lifetime is still live.");
                foreach (var component in Components) Require(!Live(component), "A source writer is still live.");
                CartWriterHooks.Release(this);
                _suspended = false;
            }

            internal void KeepSuspended()
            {
                // Quarantine, not rollback: do not resume or replay a partly applied lifecycle.
                SerializeAllowed = false;
                try { if (Live(Binding.Entity) && Binding.Entity.isAttached) Binding.Entity.Freeze(true); } catch (Exception) { }
                try { if (Live(Binding.Entity)) Binding.Entity.gameObject.SetActive(false); } catch (Exception) { }
                try { if (Live(Binding.Cart)) CartWriterHooks.Allow(this, "RegisterThreads", () => Binding.Cart.RegisterThreads(false)); } catch (Exception) { }
                try { if (Live(Binding.Saver)) Binding.Saver.SetSaveActive(false); } catch (Exception) { }
            }

            internal void QuarantineIfHeld()
            {
                if (_suspended) KeepSuspended();
            }

            internal void RestoreLocalState(CartWriterHold target)
            {
                target.RequireSuspended();
                State.Restore(target.Binding, target.Components, false);
                // The target now carries the source timer state, not its prefab-default timers.
                target.State.CopyTimerExpectations(State);
                target.RequireSuspended();
            }

            internal void ImportLocalState(CartLifecycleData data)
            {
                RequireSuspended();
                var imported = CartLocalState.Import(data);
                State.RequireCompatible(imported);
                imported.Restore(Binding, Components, false);
                State = imported;
                RequireSuspended();
                State.RequireUnchanged(Binding, Components);
            }

            internal void Resume(CartWriterHold policy)
            {
                RequireSuspended();
                RequireCompatible(policy);
                State.RequireQuiet(Binding);
                // Native OnEnable restores receiver registrations. The gate suppresses its light
                // and idle-brake reset, and only releases writers after restored-state readback.
                CartWriterHooks.Allow(this, "OnEnable", () => Binding.Entity.gameObject.SetActive(policy._active));
                policy.State.Restore(Binding, Components, true);
                if (policy._threads)
                    CartWriterHooks.Allow(this, "RegisterThreads", () => Binding.Cart.RegisterThreads(true));
                Require(Binding.Matches() && Binding.Entity.isOwner == _owner && Binding.Cart.IsNetworkProxy == _proxy
                    && Binding.Cart.enabled == policy._enabled
                    && Binding.Entity.gameObject.activeSelf == policy._active
                    && (Binding.Cart._managedFixedUpdateThread != 0) == policy._threads
                    && (Binding.Cart._managedLateUpdateThread != 0) == policy._threads,
                    "Cart native registration or attachment readback failed during resumption.");
                for (int i = 1; i < Components.Length; i++)
                {
                    bool registered = Components[i] is WaterLevelSensor sensor
                        ? sensor._cachedThreadRunnerInstanceId != 0
                        : ((FloatController)Components[i])._cachedThreadRunnerInstanceId != 0;
                    Require(registered == policy._runnerRegistrations[i],
                        "Water/buoyancy native registration readback failed during resumption.");
                }
                Binding.Entity.Freeze(policy._frozen);
                Require(Binding.Entity.isFrozen == policy._frozen && !_reentered,
                    "Cart freeze-policy readback failed or lifecycle restoration reentered.");
                CartWriterHooks.Release(this);
                _suspended = false;
            }
        }
    }
}
