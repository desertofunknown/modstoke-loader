// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        // Each entry has exactly one metadata implementation in the recognized Windows x64 binary.
        // Keep this order paired with Hooks.cs; qualify the entire set before applying any detour.
        private static readonly (Type Return, int Rva, int Length, string Hash)[] NativeBoundaries =
        {
            (typeof(bool), 0x31E5020, 704, "F4C15BE9562106F14DBAD77BB3F86C4EB77ECA6273ADBE9C211038D4E1D01808"),
            (typeof(bool), 0x31E52E0, 32, "C54728299D4AE53AAC8AC375F745CAC4428A8E73B453CCBBE4971679CB484158"),
            (typeof(bool), 0x31E5300, 976, "DE89C13542DE63F6B574FEB01B340C31F786BB3CDBE3E281A4BCB55049E7A971"),
            (typeof(int), 0x31E56D0, 336, "A099880667963699F2FB5F2E40E49E737990DBCE11091B72EF3C75EA053C34A2"),
            (typeof(bool), 0x31E5820, 448, "78536BAB81A9D6847BD84CB16C74BCB947CB5F2D729DB36926A8F8801598CA0A"),
            (typeof(void), 0x31E59E0, 480, "EFDD34D4CD6C6A439E23562607699278430F3F7DBEC7E94C1F7A4BB2DCB5D4C6"),
            (typeof(void), 0x31E5BC0, 256, "3702EA4140064EA87DEEAF0E0C84E41D3E6F0F15C215F5BC28883D966BDF74C5"),
            (typeof(void), 0x31E4E40, 480, "17C244AA821F37D655AE48B80BEA31E2F0DF75AD782AF75F11944543F0AE7B03"),
            (typeof(void), 0x31E66D0, 2704, "6F6B312DB2B4EE6A556D508FAF1A2197C775EE14CEB760A6F46E42EDAEEFE8AF"),
            (typeof(bool), 0x30E54D0, 3088, "789E7C200F40CF8D728506C159E308E9922C0C4F3D9101B4D48317E705CB94DF"),
            (typeof(bool), 0x30E62F0, 1152, "C381BF36F788C7A3F2A5558785D79346B44B0F35732355471DEDB649474C899B"),
            (typeof(bool), 0x30EA220, 288, "2BE77EF9C7BD8F6DB31E6220CCF8183388A41DD66C4180C7AD5302F0B076FE80"),
            (typeof(bool), 0x30EA340, 1520, "DAAB6A1607A48E83D977DF155301CFD6FBCB82CB683A835A54A569CCDF2B141B"),
            (typeof(bool), 0x30EF010, 704, "B1A56EEA9D49592F8B0EDDCDE354238BA98EAD2962003518E11D1E72A5B2D313"),
            (typeof(void), 0x30EABF0, 1952, "440C20D0C58EA77C92384C5235DC042D852EC5874C80F0126AC9C243FE901419"),
            (typeof(void), 0x30E7040, 352, "04D9D26087A1DEEFE2FA1593DB4CFA24A05AE724A6106EBEF6BE39D4525BA53B"),
            (typeof(void), 0x30E71A0, 224, "A35C1CC88C93C829300D5906F105DF181A6C4B32EAABC2C08F05E8CD316349EE"),
            (typeof(void), 0x30EE510, 1264, "C2D3F6C869A2EA3E5D406CBC8BD11C9C55C583D1790622A19F57007E66E11E06"),
            (typeof(void), 0x30F05C0, 3136, "78D32949FE3BC8F41E446740C211E277EE5E629E53273385381D73078EFF81EC"),
            (typeof(void), 0x345C9A0, 1600, "FE1C91488FE00CC81B3932E375C930EE80D99D0E043776C04EDC12B77A7614B7"),
            (typeof(void), 0x31EC680, 224, "C35E72CC60DF8B1F56AC07EB01F5A52597242222A48BDB2947E1D5CB094BD0C1"),
            (typeof(void), 0x31F2570, 368, "E106BA84FA08FD79FDE39ED9665E3E1A3FDB645A2DB7DE0E63E58D38020CDEB9"),
            (typeof(void), 0x31F26E0, 528, "A765E422E5CB09169CED03B884DFF9C753C84AFE0068FCA62CEF5487A949A149"),
            (typeof(void), 0x31F28F0, 256, "B256978BB4CC5A981AC5BB9301696B29F300464D998814072394816BB6BAB175"),
            (typeof(void), 0x31ECAC0, 2000, "E2AC846B5BB89C9FFFAC801B3352D482351236A7F745E300E308ACADFB000DDA"),
            (typeof(void), 0x31ED290, 208, "DCC7813213B1A42045A3655E64337D4613BA02F9BA55BFF12F45B22BE2A2D8B4"),
            (typeof(void), 0x31ED530, 160, "57C2481D0ECD86942BDC2235FAF95F27CA7AB35C63381198679314714B3DC4B2"),
            (typeof(bool), 0x31CF990, 864, "C61C04A481EB3CCE41391C34A246528ABFB35DDC58FB7A867E0A849FA8A16A7B"),
            (typeof(void), 0x30AB280, 480, "7AB4840299662B8EAFC260D79EC0FEF04DC95D7321A99BCAF12DC5B6DEB885A7"),
            (typeof(void), 0x30AC140, 512, "00C73C89FF3CC75788637E9D22810C9104574C7A0A267AE0DDA88C0E3F276071"),
            (typeof(void), 0x30AB460, 736, "913B7703BC1C33E8306851B5C79A9A0ECF0A5A0897A5FC01B66C63E479710B06"),
            (typeof(void), 0x31CFCF0, 576, "8DE6277E6D97A262FD6C8009EC1EEA42851A4EF22BFAD1A063306A3A50319977")
        };

        private static void QualifyBoundary(MethodInfo method, int index)
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || index < 0 || index >= NativeBoundaries.Length)
                throw new InvalidOperationException("Inventory hooks require the qualified Windows x64 boundary set.");
            var pin = NativeBoundaries[index];
            QualifyBoundary(method, pin);
        }

        private static nint QualifyBoundary(MethodInfo method, (Type Return, int Rva, int Length, string Hash) pin,
            bool isStatic = false)
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidOperationException("Inventory native boundaries require Windows x64.");
            string boundary = method == null ? "<missing>" : method.DeclaringType?.FullName + "." + method.Name;
            if (method == null || method.IsStatic != isStatic || method.ReturnType != pin.Return || method.ContainsGenericParameters)
                throw new MissingMethodException($"Inventory boundary signature differs: {boundary}; "
                    + $"expected {(isStatic ? "static" : "instance")} {pin.Return.FullName}, RVA 0x{pin.Rva:X}; "
                    + $"actual {(method == null ? "<missing>" : (method.IsStatic ? "static " : "instance ") + method)}.");
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0)
                throw new InvalidOperationException("Inventory native method information is unavailable: " + boundary);
            nint entry = (nint)BitConverter.ToInt64(ReadNativeMemory(info, IntPtr.Size), 0);
            nint module = GetModuleHandle("GameAssembly.dll");
            if (module == 0)
                throw new InvalidOperationException("GameAssembly.dll is unavailable while qualifying inventory boundary: " + boundary);
            if (entry != module + pin.Rva)
                throw new InvalidOperationException($"Inventory native entry differs: {boundary}; expected RVA 0x{pin.Rva:X}, "
                    + $"actual offset 0x{entry.ToInt64() - module.ToInt64():X}." + InventoryPatchOwners(method));
            string actualHash = Convert.ToHexString(SHA256.HashData(ReadNativeMemory(entry, pin.Length)));
            if (actualHash != pin.Hash)
                throw new InvalidOperationException($"Inventory native bytes differ: {boundary}; RVA 0x{pin.Rva:X}, "
                    + $"length {pin.Length}, expected SHA256 {pin.Hash}, actual {actualHash}."
                    + InventoryPatchOwners(method));
            return info;
        }

        private static string InventoryPatchOwners(MethodInfo method)
        {
            // Owner metadata explains startup overlap; it never qualifies changed native bytes.
            var patches = HarmonyLib.Harmony.GetPatchInfo(method);
            return patches != null && patches.Owners.Count != 0
                ? " Registered Harmony owners: " + string.Join(", ", patches.Owners)
                    + ". Inventory qualification must precede overlapping hook installation."
                : " No Harmony patch owner is registered; changed bytes or an unregistered native detour remain possible.";
        }

        private static nint QualifyModuleUnregister()
            => QualifyBoundary(typeof(ItemInstanceModule).GetMethod("Finalize",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null),
                (typeof(void), 0x31E75A0, 320, "58C29C5F47D326A1BD933C6C8CFDA8A33B73158728E93177509EE7C128392878"));

        private static unsafe void UnregisterNativeModule(ItemInstanceModule module, nint method)
        {
            // The qualified base body only removes this target's virtual Update listener from
            // the current ModuleUpdater. Invoke that body directly, not a derived finalizer.
            // This does not free the object or suppress its eventual native GC finalization.
            nint error = 0;
            IL2CPP.il2cpp_runtime_invoke(method, module.Pointer, null, ref error);
            Il2CppException.RaiseExceptionIfNecessary(error);
        }

        private static byte[] ReadNativeMemory(nint address, int length)
        {
            var bytes = new byte[length];
            if (!ReadProcessMemory(new nint(-1), address, bytes, (nuint)length, out nuint read) || read != (nuint)length)
                throw new InvalidOperationException("Inventory native boundary could not be read.");
            return bytes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandle(string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] bytes, nuint size, out nuint read);
    }
}
