// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Startup;

namespace Modstoke.Preloader
{
    internal static class InteropClassInitResolver
    {
        private const string RuntimeHash = "4E2D8D90E2E478B8A565CF3590048DDFE55F4AF4501BF34F40FC85431773DDBC";
        private const string ValueTypeRuntimeHash = "440B875ED85CF0C17717A0BAFCDBBD84EAAB117CE1810F5A4C9C1D0077FBD835";
        private const string CompositeValueRuntimeHash = "A224108AFDC8101EC2D49626DA4CE5769BED05C57E7B1D5D8A108AE0363D0F12";
        private const string CallbackReferenceRuntimeHash = "C48B3C234DA2840686335B23C40B63760009EA843923F335A88D3EF266D725D7";
        private const string NativeValueReferenceRuntimeHash = "558737EBA1A09B5A772B32FC141558867965D613B1D9C7AED7DB8E22080AEFA5";
        private const string NativeValueAbiRuntimeHash = "F79F678A6E473C5686529B39A1706EEAAEA4EE9E47DE25CDD9A2840DCA12CEA2";
        private const string NativeJobInvokerRuntimeHash = "49ACB1906CF244AC4356006772812C091F3CD95E7BAC3D6D9F2B9BD14051933E";
        private const string NativeJobBridgeRuntimeHash = "F2041B0BAA43D772C87EC2790B98ADA83D732BA91B9FA800C4168CCA8F8C93AE";
        private const string NativeJobReflectionRuntimeHash = "F0A4C6DA4240E0FDB7B25CB6D9CD4830EB15A3100E4EED4B585D437F7308C790";
        private const string NativeSharedMemoryRuntimeHash = "8E03A0A21529AA3A8AB9AC2551F4C37FB1E8EC6A540513E77CB616DC63A35F38";
        private const string NativeSharedStaticRuntimeHash = "6719CAFD77AF03321E748CB738307B0A3A8D9053607309F66FA1316F799FA459";
        private const string ManagedJobContextRuntimeHash = "00BDB91823843C483E011DBCF4B7BD3FCC60B85FCEB792D86FF1C79515863336";
        private const string NativeJobSchedulingRuntimeHash = "EBB000CE9AC39CD9438791B6EAB5BC6B82687CC46430C5B2E5AE7887E9BCF34A";
        private const string NativeJobFailureRuntimeHash = "D7B4D1C61F95846802435DAE54F992CEFD0DD84996604FDE13AE165F91FB250A";
        private const string RestoredJobRunRuntimeHash = "885B130FB934CE99806E58EB141B06D2529B6A61724DAB0DBD66E28DC3A70DBF";
        private const string NativeJobDependencyRuntimeHash = "3A4343256F2AFFA9ED268D65EEA2CEE93223374B6B62C7D9AA98D21E4116481A";
        private const string NativeJobHandleRuntimeHash = "8F0AC0092F648BC3B53B3D12277E92640C8EE0DEAB7AB9815F9DECAE7A3B5603";
        private const string NativeJobObserverRuntimeHash = "1BAC8A0076F86CBC60C9E037DB22DAD207E542966AF5C1264429F5BABCD03944";
        private const string NativeJobAsyncRuntimeHash = "3D9CBDA23C7578A5C2B0FEE47D6941A3706E4E64BD1C1F27606B21572AD9E761";
        private const string NativeArrayPayloadRuntimeHash = "83466211DF0CA76816A37FAD2E43E963B322D0A3FB0DA239A808BAC9069B340E";
        private const string NativeSlicePayloadRuntimeHash = "B1182B02B68B9B6A70F8FA76F5FB5FD7F815AC259916E3F1535EA36F526B74E2";
        private const string NativeReadOnlyPayloadRuntimeHash = "6E556C5540F43C43D87CAEDA5AA2ACB13C2A70FBCDEA0C1420D0C318E4D1FE2D";
        private const string NativeTransformJobRuntimeHash = "387F833C0D3B4AC26B7E46237F587485FBF274A94477A5A95B8A84828E4FD03C";
        private const string NativeParticleJobRuntimeHash = "6E8AA3A80F512E42D6C6728DB2B2A990779203AF37A4A720B45439B150C85232";
        private const string NativeParticleHandleRuntimeHash = "3023B3122A941A94C74C728DF517D0D7701A5939E88168E7C6C3302157E39B7E";
        private const string NativeAnimationCallbackRuntimeHash = "61C37529D549C75FC452EB68D668EDB37879C93F5CA74971703F4F06A655C385";
        private const string NativeAnimationCallRuntimeHash = "623068792D126DC81DDE0B1645BEB23C179F88DACBC8455131335DE22E6BCD9E";
        private const string NativeAnimationOwnershipRuntimeHash = "28F693162DA3017A6C8865140C19654EB0BE22074CCBA5C99E87A66114A70D36";
        private const string NativeAnimationDataRuntimeHash = "32FFB868EFA8605D3C70E8016DCE4F44FD3A39493988B35678E2D53421C8E713";
        private const string NativeAnimationStreamRuntimeHash = "B7F2245180F8572E8A98B2D79924CFB1760250DCF472783DB982241160BA047E";
        private const string NativeAnimationBulkRuntimeHash = "D64FF5729A273ADDE66AB556AA75EAA9A0F1B10D1833416C014B6C3D98E05B73";
        private const string NativeArrayStorageRuntimeHash = "BC83D6893E8D0B491D66DB26152B29B9D72F8023370F146DE94A755B351023A2";
        private const string NativeArrayRoutingRuntimeHash = "DBAAC6C38CD8D1B07943F16DE2F822D324174E7D71E7B59A065CB36275173C2D";
        private const string NativeArrayViewsRuntimeHash = "8CB34A023F413F1CE7114915E350F6AB7140084DA4E86D6BFA96AAFFF2364BA8";
        private const string NativeArrayProxiesRuntimeHash = "46F51A37CAA11957ECF44400369E33810EFBE1C8B44C22361C09EC1360B6F1BA";
        private const string NativeSliceStorageRuntimeHash = "5DBB359498E4C5403660276DB15FC96D56B7D9257FB4465AB6361F069DBE6E35";
        private const string NativeNavigationRuntimeHash = "24513D538AF805D96D2ABF574F7BFD106B6AF3F080175DF138C0CEB2CBA60F87";
        private const string NativeClassPublicationRuntimeHash = "67D9FC5C4B8BC6CF9FE883D5DFA979055DFA3248EAA31132754B9ACCB6E301D9";
        private const string NativeNavMeshDataMethodsRuntimeHash = "4B565C975AE9350320F56A036FCACFFAC0E1E1C262CDB0D57CF0E13F6E125126";
        private const string NativeNavMeshDataMetadataRuntimeHash = "EC6862302A3302FB6A5F3F6CDFA275647BCAB09A2BCA6BD12B7E59D420A3EAA5";
        private const string NativeNavMeshSettingsRuntimeHash = "D3C9E81BDDA29121DD99EE4E7D95D33331A9426A2BA83FB22A3BF1C4F21F9834";
        private const string NativeNavMeshReflectionRuntimeHash = "5AEC1A120275E784901E1BA230CCEEDB9ABA84E24F427808B584E40B0FB75A68";
        private const string NativeNavMeshAttributesRuntimeHash = "0D08463E7DFC7ABBF8EC7BD8010720C7FD13E815DBC518B01090F31987A85D31";
        private const string NativeNavMeshPublicationRuntimeHash = "DD0755710ABF15C4E313A2838F562C67537E9012BE70F4638B38D313A9B0CB4C";
        private const string NativeNavMeshWrappersRuntimeHash = "043B6E7024570A215BDA0623D3A5E53C9CE9BA54D38659715CAEF05CBFB27A40";
        private const string NativeNavMeshSourcesRuntimeHash = "D779D6A3C348C626A77FE62508221AD08BE48462CEE66809C8555621E52B7249";
        private const string NativeNavMeshBuilderRuntimeHash = "3B0B29454AC1F53B56C87E5E7D6A1EFE5AC04588444ED902859C9787337100AF";
        private const string NativeTypeHandleRuntimeHash = "AFA137CFF658DA06A793D100D20AEE2FCA494ACB1D37826824E6DCEA14872E1B";
        private const string NativeNavMeshStringRuntimeHash = "4B4BABFBFFC7922428051FCB212B07855110E0563E0698461E1AB80048A1DB7B";
        private const string NativeNavMeshWritableRuntimeHash = "BDFA86CF25101CADB51B7F3A79045C1F6F9835DEE4073948AD501482A5C4B039";
        private const string NativeAttributeLayoutRuntimeHash = "B25DEFA18364A9717B5A0B6825DB315044F6DFA942977C59F4B88E49D3D8DB32";
        private const string NativeValueTypeClassificationRuntimeHash = "7F2799F820914A97EC2669B1B542015394F49F2897CA2889A36717535115012C";
        private const string NativeNullableAllocationRuntimeHash = "5D41156F857F9E0DFE0C3594697F1D15204DB7577510BE0370D3ED0D7BFA8A78";
        private const string InjectedTargetOwnershipRuntimeHash = "D72E4C62B837D7337A82C2A0DD1AE31BF6F3892AA92EABDAC003448DC5F2D679";
        private const string NativeAudioStateRuntimeHash = "B45AF8E286DD95380A266610B02E9E49BA79DD723949BFBF029BEC5054AAD412";
        private const string NativeAudioConsumptionRuntimeHash = "2E02D846B7ECBD93B38E92B07C63E8C55C07740C715A4EBA75C76C8636A0CA96";
        private const string NativeAudioCallbacksRuntimeHash = "8A49A0C34DB38483758400E247CE3F4BBB88BE93FC8BC762EBCB68625C7BF3A0";
        private const string TripleCallbacksRuntimeHash = "52037FACCDF4F221B1D54E2F86F4557EB5913A819F0FD4F50D0433AA9F254F0A";
        private const string QueryProjectionRuntimeHash = "DF0E9A3099D39964B16BB762FEE69E9085E592FE07A3B5103722C0265A96BD41";
        private const string ScreenDisplayRuntimeHash = "219C42E4FF4BC79EFAAB21C705B9E6AD8AA777CDAFF69D60B41CECE69832092B";
        private const string ParticleTriggerRuntimeHash = "0C4BA3F636F9C8B2327386FB6A0D2250EB66CCD4F3C7A98018737F178ED535FB";
        private const string ScreenLayoutRuntimeHash = "7A7782BBF0F1A1B39A5D9108748AEAF34F0820EBFB50C29B2266024B6C5D4F1F";
        private const string ShaderWarmupRuntimeHash = "AB55D5DCB241CA4810A594A3653D4D6424261E3F00152EB2327CAA13F63821E1";
        private const string EventDebuggerStateRuntimeHash = "FD2B2409CFD83BA0AD505261FFF43DA391104993BE4676FE821AB6ABDDDF979B";
        private const string EventDebuggerTraceRuntimeHash = "171B0E962E0EC4FDDB61D78952611301C1DEADFD464CCD99B5BBE262C7A60721";
        private const string EventDebuggerFieldsRuntimeHash = "25C9889921AE0ADF04320FBFAE081748708BDBB26359B2CE22D78D26DF89F889";
        private const string Net11RuntimeHash = "0AA211B006FEC94F660E5E4B938E4743E74DD25B1A5A04C3C161EFF8DE32E92D";
        private const string EventReplayRuntimeHash = "E7E33FA6AB62DB318804E11AA4865D787F2C296DD9343CBC001855C9772FC27B";
        private const string StyleEnumRuntimeHash = "EB8E3AC715FA0981E0C257D2BA85BD96F048AB8A3DF2CB13A36B8D6A9321ADBB";
        private const string ComparisonCallbacksRuntimeHash = "E12D53A8833EDB34EEA47F55479BFE4C80F3276C64E5E5A18F4319DF910140AD";
        private const string ScalarPairFunctionsRuntimeHash = "521C80005E1FA32FA49335ACDEE042C148B3A012A304AEBE77920F5496216F87";
        private const string Vector3CallbacksRuntimeHash = "6C9351A1DB36C2D9DC8163C41E985D2E763585B73327EAEC71AA32523F441D27";
        private const string SelectorCallbacksRuntimeHash = "15B7022CF81637493433CDF72FD80185F582A2EE1809D23F24942BF4D8A5F675";
        private const string ReferenceFunctionsRuntimeHash = "481F41610AE1DEA45833DEE061BFCA194EC78D6C967C3B004AE43F2D2FC8B36A";
        private const string TransitionAnimationsRuntimeHash = "733CDE0674D0C940247F7722B699043195FD1A52EE0909AA87085A5AA91717DE";
        private const string AggregateFunctionsRuntimeHash = "2576A3C339F472D4168FA56DB3D84638BB12E44FC4B1630DF3A34A246D241BA4";
        private const string AnimationCallbacksRuntimeHash = "4D6EFD138B2AE7F516A91F138A7A4C218B47C81E419FDB59C473A7685E7DD92B";
        private const string StyleValuesReturnRuntimeHash = "C98044D0F27575D94DF9874CAF10131E760353DD4D672BE4C8DC908644D6277D";
        private const string WebFormStateRuntimeHash = "DA644939CF7848CF6B8D003A1817272B17ADC47CD119466998CBB888D5EE631D";
        private const string NativeImageMetadataRuntimeHash = "2DCDE11D837A37D4291651C2A22DA902FA71ACCA46C33A4311A79F13E5504089";
        private const string VfxSpawnerRuntimeHash = "BFE6DEBE82C37AA260FAF7CFFFA4381C2D079FC1FA89C76912914117E87E38B6";
        private const string WebRequestConstructionRuntimeHash = "E60C59A8F2FB02D99983729D3DA1B23F79AF7E5E2FD1A6AF8EB87F273A1AA387";
        private const string OwnedAttributeRuntimeHash = "E9ABACA4F8B74673237B4BD0554969E3E4380776D9ECB76D4DC02F08D062473C";
        private const string WwwCallbacksRuntimeHash = "33810062D6D91248F2BAD985517B3620FAF49C1086C7EB84A9E44148EDF46A96";
        private const string AudioTypeRuntimeHash = "F3C75421CE2613BE97B4E8A06F599190AF3C2CC2BFC52E1EE5B078FC9C96412C";
        private const string MovieTextureRuntimeHash = "C1297324F34B1F04C35400EAAB4B28F54A1FD26759D121A21145423D68983BEC";
        private const string TypeNameLookupRuntimeHash = "A05B6DEF33CE456ED4483E62A1F200EB452AEFB830DD463056D555E818145FA3";
        private const string TypeEnumerationRuntimeHash = "610AD96674BA23ADC704EE4207F86188C7B704841E3DA33F29C56F508FCE6ABA";
        private const string OwnedImageLookupRuntimeHash = "8DDACD2AE355839C5069308362E77B5B79F96DAD9E9E0C6393E9676EB5E45F29";
        private const string WwwGraphRuntimeHash = "DA6BD9B103F723DA0E83AEFB62419AFD7C4E42E4ED17434F5928DB8E805407B7";
        private const string OwnedAssemblyLookupRuntimeHash = "EDECE40351D580F4A1C91212F75046551FC1D76A1698B5A4900DB86572957887";
        private const string GenericArrayElementRuntimeHash = "EF64E1C35BEAC60DDB4078E26EC17CCDE4BAEE34663A832FDB8EF738715C945D";
        private const string GenericMethodSignatureRuntimeHash = "B70443280B1B348AE9ECBAA2C5FDA806064B0BEDB7847B66962B8B65892CCBD5";
        private const string GenericMethodContextRuntimeHash = "D2165A186094283484C9EBE37298D318F4A6CA518DC524D611388B6A61110CE0";
        private const string ArrayConstructionRuntimeHash = "2C41A8814E1B07BDBE1EAA9EF4E241A657D47ED8977C83FDF17503C1EF8F8829";
        private const string StyleEnumDiagnosticsRuntimeHash = "60F64AA0A404C3F081764F1B9788FD7D650451A4E609154268806D94A78B5C77";
        private const string StyleEnumSharedInvokerRuntimeHash = "16C0C62845970E57AB5750B62EA2277593F11AAFAEC1317024DA1D114CD9E64F";
        private const string StyleEnumAbstractSlotsRuntimeHash = "758220254F77EBE1E9054970A6CC58CBC0AC413B0E1E0F8E755D986CBF120CC4";
        private const string GameAssemblyHash = "9BEED2673A205A07F9CB00A6911CA7B3372F74F98F8AA05D90A021423980AA9B";
        private const int InstanceSizeRva = 0x538740;
        private const int ClassInitRva = 0x508B80;
        private const int ClassInitLockedRva = 0x5087A0;
        private static int _attempted;

        // Called during native bootstrap when supported, otherwise at live-domain preload.
        internal static void TryInstall(string gameAssemblyPath)
        {
            if (Interlocked.Exchange(ref _attempted, 1) != 0)
                return;

            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
                    || Il2CppInteropRuntime.Instance.UnityVersion != new Version(2022, 2, 16))
                    throw new NotSupportedException("Class::Init resolution requires the matched Windows x64 Unity 2022.2.16 runtime.");

                var runtime = typeof(IL2CPP).Assembly;
                var runtimeId = runtime.ManifestModule.ModuleVersionId;
                var runtimeHash = HashFile(runtime.Location);
                if (runtime.GetName().Version != new Version(1, 5, 2, 0)
                    || !(runtimeId == new Guid("bcd6fc36-3528-449d-9238-f4b40e36b1f4") && runtimeHash == RuntimeHash
                        || runtimeId == new Guid("2090b79e-01fe-4a9d-a7ee-d58c0623de8e") && runtimeHash == ValueTypeRuntimeHash
                        || runtimeId == new Guid("4fe7b8a8-4862-4128-9956-46e03fba7a1f") && runtimeHash == CompositeValueRuntimeHash
                        || runtimeId == new Guid("04d0d078-769b-4ed8-ad39-5abb88c7cb3a") && runtimeHash == CallbackReferenceRuntimeHash
                        || runtimeId == new Guid("5941b90a-b758-463e-a385-4f7d31fa772b") && runtimeHash == NativeValueReferenceRuntimeHash
                        || runtimeId == new Guid("280098ed-31ed-4b69-8807-0e86e68b5958") && runtimeHash == NativeValueAbiRuntimeHash
                        || runtimeId == new Guid("fc87dc01-5ec8-472c-8cbe-a901a81aeb1e") && runtimeHash == NativeJobInvokerRuntimeHash
                        || runtimeId == new Guid("aa39beba-18e9-46ab-a184-f9b2021a7ac2") && runtimeHash == NativeJobBridgeRuntimeHash
                        || runtimeId == new Guid("9ca5b254-cd73-46c9-b9fb-05347680c9d3") && runtimeHash == NativeJobReflectionRuntimeHash
                        || runtimeId == new Guid("1c59e917-4713-47a8-a391-ecdc1792427e") && runtimeHash == NativeSharedMemoryRuntimeHash
                        || runtimeId == new Guid("bdce6d75-cbcd-48b6-a397-241b7f4913ea") && runtimeHash == NativeSharedStaticRuntimeHash
                        || runtimeId == new Guid("3cd877aa-98d0-416f-982c-db6246c23cea") && runtimeHash == ManagedJobContextRuntimeHash
                        || runtimeId == new Guid("f621049f-cb0a-428c-922d-6532568745c6") && runtimeHash == NativeJobSchedulingRuntimeHash
                        || runtimeId == new Guid("c048cd62-5394-4cca-8a19-ebb668b2c320") && runtimeHash == NativeJobFailureRuntimeHash
                        || runtimeId == new Guid("82e236e0-fd60-4510-89dc-8d0bd7bacf55") && runtimeHash == RestoredJobRunRuntimeHash
                        || runtimeId == new Guid("666d18fb-468c-4ce7-92bb-aba2999e8a35") && runtimeHash == NativeJobDependencyRuntimeHash
                        || runtimeId == new Guid("81d94a4a-726f-4f8c-813e-fa5bcd9598e0") && runtimeHash == NativeJobHandleRuntimeHash
                        || runtimeId == new Guid("1c2d5191-ed29-4db0-a40e-6598b0b7202d") && runtimeHash == NativeJobObserverRuntimeHash
                        || runtimeId == new Guid("19bd9e9e-dad1-4ff3-a2a5-11a62f4b4f05") && runtimeHash == NativeJobAsyncRuntimeHash
                        || runtimeId == new Guid("b46f3895-4ed3-4533-af71-7a79bfd8d0d9") && runtimeHash == NativeArrayPayloadRuntimeHash
                        || runtimeId == new Guid("0a2dcb62-677d-44f1-808b-fbe1b14b7062") && runtimeHash == NativeSlicePayloadRuntimeHash
                        || runtimeId == new Guid("5d06dbb1-85de-4048-9695-342bf95b43af") && runtimeHash == NativeReadOnlyPayloadRuntimeHash
                        || runtimeId == new Guid("21d05173-290a-4d8c-a88c-cb9845f0bebe") && runtimeHash == NativeTransformJobRuntimeHash
                        || runtimeId == new Guid("8407f858-5ac7-49e2-8669-b6b144ea93a2") && runtimeHash == NativeParticleJobRuntimeHash
                        || runtimeId == new Guid("bea79237-64a7-4dcb-972d-43e09b9299cd") && runtimeHash == NativeParticleHandleRuntimeHash
                        || runtimeId == new Guid("d0dbce4d-4d41-45f5-8cf4-8cd1771a5513") && runtimeHash == NativeAnimationCallbackRuntimeHash
                        || runtimeId == new Guid("5e94a847-906e-4629-a95b-32cb1140d249") && runtimeHash == NativeAnimationCallRuntimeHash
                        || runtimeId == new Guid("9c2cbd4d-b0be-4f88-8f39-55e494e63d95") && runtimeHash == NativeAnimationOwnershipRuntimeHash
                        || runtimeId == new Guid("725145ef-d585-498f-b516-e9ce66296b68") && runtimeHash == NativeAnimationDataRuntimeHash
                        || runtimeId == new Guid("a1e0c513-8224-4035-8c44-753c692dfd3d") && runtimeHash == NativeAnimationStreamRuntimeHash
                        || runtimeId == new Guid("55dfc3ba-f6c3-4c6c-a390-9a3e54be17e1") && runtimeHash == NativeAnimationBulkRuntimeHash
                        || runtimeId == new Guid("e3fed75b-6e84-4451-892d-200838105358") && runtimeHash == NativeArrayStorageRuntimeHash
                        || runtimeId == new Guid("5147d047-c9cc-40c6-a0f9-030e3a01e99a") && runtimeHash == NativeArrayRoutingRuntimeHash
                        || runtimeId == new Guid("ad8415fd-571e-463c-a36c-2c446d28cf93") && runtimeHash == NativeArrayViewsRuntimeHash
                        || runtimeId == new Guid("aae5dc08-03c1-4273-9aab-42d85308792f") && runtimeHash == NativeArrayProxiesRuntimeHash
                        || runtimeId == new Guid("98ae473f-2865-4a59-a916-875dfe5c8c14") && runtimeHash == NativeSliceStorageRuntimeHash
                        || runtimeId == new Guid("a46acf05-978c-4e57-b503-ddd5d8dcf4a3") && runtimeHash == NativeNavigationRuntimeHash
                        || runtimeId == new Guid("f6ac0277-a438-46f7-86f9-aef505b6fce4") && runtimeHash == NativeClassPublicationRuntimeHash
                        || runtimeId == new Guid("3e7ffdd8-a39d-463d-acf8-b5c66f2fb939") && runtimeHash == NativeNavMeshDataMethodsRuntimeHash
                        || runtimeId == new Guid("e8a50844-73d4-40d7-84a4-f0d0998becc6") && runtimeHash == NativeNavMeshDataMetadataRuntimeHash
                        || runtimeId == new Guid("6b6ba33a-c4f8-49c6-b543-d388dd3e5004") && runtimeHash == NativeNavMeshSettingsRuntimeHash
                        || runtimeId == new Guid("70a71cc7-2efb-48b5-8db5-3898f6d10b55") && runtimeHash == NativeNavMeshReflectionRuntimeHash
                        || runtimeId == new Guid("87f48f34-c5d5-4052-a9a9-cedc5aa7c3c5") && runtimeHash == NativeNavMeshAttributesRuntimeHash
                        || runtimeId == new Guid("dbbce48b-a362-4b7a-8b05-5a5d40688ea6") && runtimeHash == NativeNavMeshPublicationRuntimeHash
                        || runtimeId == new Guid("145b7d51-1371-4ab3-ae1b-ec0657ff0573") && runtimeHash == NativeNavMeshWrappersRuntimeHash
                        || runtimeId == new Guid("23440006-5c2e-4ae7-91df-7343dc1842fc") && runtimeHash == NativeNavMeshSourcesRuntimeHash
                        || runtimeId == new Guid("3b755500-dff0-4400-b5ae-ee954655bf19") && runtimeHash == NativeNavMeshBuilderRuntimeHash
                        || runtimeId == new Guid("90747f62-f9a6-4fa6-a509-5a12ea83f79b") && runtimeHash == NativeTypeHandleRuntimeHash
                        || runtimeId == new Guid("0f95ce14-a9a9-403f-8f7e-44cf0fec6af0") && runtimeHash == NativeNavMeshStringRuntimeHash
                        || runtimeId == new Guid("2f4475e6-f635-40af-95d5-e498fa8ea13e") && runtimeHash == NativeNavMeshWritableRuntimeHash
                        || runtimeId == new Guid("9e12174a-fba8-4447-9a2c-cc4c6f84111b") && runtimeHash == NativeAttributeLayoutRuntimeHash
                        || runtimeId == new Guid("b8240b04-4ac2-4c49-9ba1-3a683fa3a275") && runtimeHash == NativeValueTypeClassificationRuntimeHash
                        || runtimeId == new Guid("688c132e-4aa6-4467-80a3-b7621be24ab9") && runtimeHash == NativeNullableAllocationRuntimeHash
                        || runtimeId == new Guid("d640331d-1a22-49e0-abbb-f5c25ab645a0") && runtimeHash == InjectedTargetOwnershipRuntimeHash
                        || runtimeId == new Guid("c19fd91d-8fe2-4dcf-ac3e-860aa62ca4c0") && runtimeHash == NativeAudioStateRuntimeHash
                        || runtimeId == new Guid("6ed789bd-7d2d-48b4-88f1-21ff6c7f6416") && runtimeHash == NativeAudioConsumptionRuntimeHash
                        || runtimeId == new Guid("610d6d4c-08e1-4d58-bac6-3ad5fbdd88ac") && runtimeHash == NativeAudioCallbacksRuntimeHash
                        || runtimeId == new Guid("fcb3acd4-6c97-4788-99e2-41931646ef8c") && runtimeHash == TripleCallbacksRuntimeHash
                        || runtimeId == new Guid("b8e64578-7620-42bb-ae1a-ba5c3716ee50") && runtimeHash == QueryProjectionRuntimeHash
                        || runtimeId == new Guid("78bc97c7-9bbf-47f3-93e8-1a44009f48a1") && runtimeHash == ScreenDisplayRuntimeHash
                        || runtimeId == new Guid("ab0815cf-f1af-4dac-99da-fd85b4bcdb55") && runtimeHash == ParticleTriggerRuntimeHash
                        || runtimeId == new Guid("04209096-8383-4db2-a6c8-2f34f4ea7fda") && runtimeHash == ScreenLayoutRuntimeHash
                        || runtimeId == new Guid("b6328a08-4d89-42e1-9a90-ae31a54a509c") && runtimeHash == ShaderWarmupRuntimeHash
                        || runtimeId == new Guid("551d4888-d666-4e02-8609-8edc6870a1d7") && runtimeHash == EventDebuggerStateRuntimeHash
                        || runtimeId == new Guid("baf5bc7b-af26-4887-87f7-1422d30ebcb7") && runtimeHash == EventDebuggerTraceRuntimeHash
                        || runtimeId == new Guid("472426d9-59d2-4255-8a63-943375631cad") && runtimeHash == EventDebuggerFieldsRuntimeHash
                        || runtimeId == new Guid("d101532a-3ade-4d49-8da7-43b108c8d8d4") && runtimeHash == Net11RuntimeHash
                        || runtimeId == new Guid("834d2948-052a-4d19-9d19-71b0c286147b") && runtimeHash == EventReplayRuntimeHash
                        || runtimeId == new Guid("65058113-6d95-4e80-905a-bb27abdb0146") && runtimeHash == StyleEnumRuntimeHash
                        || runtimeId == new Guid("928e47d2-953c-4c6a-a741-1243d9c898c5") && runtimeHash == ComparisonCallbacksRuntimeHash
                        || runtimeId == new Guid("95556989-2c6c-4d0d-9d94-c6626b1c0ad5") && runtimeHash == ScalarPairFunctionsRuntimeHash
                        || runtimeId == new Guid("31841827-4fea-46f5-a536-68d802e0ecc6") && runtimeHash == Vector3CallbacksRuntimeHash
                        || runtimeId == new Guid("4d0e737b-e1a1-4e22-97ad-0c363c5dc382") && runtimeHash == SelectorCallbacksRuntimeHash
                        || runtimeId == new Guid("b4d124d7-81f1-407e-b07f-1464d564a24b") && runtimeHash == ReferenceFunctionsRuntimeHash
                        || runtimeId == new Guid("cabdc620-fb52-4243-975a-5d19bd391911") && runtimeHash == TransitionAnimationsRuntimeHash
                        || runtimeId == new Guid("39cfb5a3-1d7f-42a9-a963-16af9bc29d93") && runtimeHash == AggregateFunctionsRuntimeHash
                        || runtimeId == new Guid("1c4b61f2-9c7c-4d61-9eba-674bf1f87bcd") && runtimeHash == AnimationCallbacksRuntimeHash
                        || runtimeId == new Guid("950dfeff-8c2e-4517-8d9d-a83d9a54d9b3") && runtimeHash == StyleValuesReturnRuntimeHash
                        || runtimeId == new Guid("cd991c20-2a6d-4ea2-9e9e-fe2d8fbadf41") && runtimeHash == WebFormStateRuntimeHash
                        || runtimeId == new Guid("3c1350e4-4f41-4377-9264-f76eb8caa3b4") && runtimeHash == NativeImageMetadataRuntimeHash
                        || runtimeId == new Guid("a4fba34e-733e-4f09-8b97-03fe14b49081") && runtimeHash == VfxSpawnerRuntimeHash
                        || runtimeId == new Guid("6552ba40-8452-4183-b28b-9956062c0e2e") && runtimeHash == WebRequestConstructionRuntimeHash
                        || runtimeId == new Guid("449a2cbc-5071-424e-a06b-4bb54ec6c47b") && runtimeHash == OwnedAttributeRuntimeHash
                        || runtimeId == new Guid("0bd7ef2e-12b5-414a-b942-762345795081") && runtimeHash == WwwCallbacksRuntimeHash
                        || runtimeId == new Guid("458ee077-cc69-455b-b25e-b0d9058e6eb8") && runtimeHash == AudioTypeRuntimeHash
                        || runtimeId == new Guid("f14872eb-9472-482e-9be0-b6f5b8c6c1f0") && runtimeHash == MovieTextureRuntimeHash
                        || runtimeId == new Guid("ac653d3f-5ae9-4bbf-9cdc-794e2aa15f35") && runtimeHash == TypeNameLookupRuntimeHash
                        || runtimeId == new Guid("07feef7c-042d-4be7-86fd-824292bee25a") && runtimeHash == TypeEnumerationRuntimeHash
                        || runtimeId == new Guid("4568c6a7-dc2e-4683-baa4-8908344200e2") && runtimeHash == OwnedImageLookupRuntimeHash
                        || runtimeId == new Guid("a305c339-e1da-4df9-b907-dcbca25401f1") && runtimeHash == WwwGraphRuntimeHash
                        || runtimeId == new Guid("a5415491-abce-4bbc-a593-c2bdb72e2d3c") && runtimeHash == OwnedAssemblyLookupRuntimeHash
                        || runtimeId == new Guid("6cc777aa-e980-4c4c-bbd3-9416fbea0634") && runtimeHash == GenericArrayElementRuntimeHash
                        || runtimeId == new Guid("31a0ae7a-a5c2-42d9-a1b5-15bd5cbcdbdd") && runtimeHash == GenericMethodSignatureRuntimeHash
                        || runtimeId == new Guid("9b747575-8f64-472b-ae27-c96ce03fa65a") && runtimeHash == GenericMethodContextRuntimeHash
                        || runtimeId == new Guid("0ca30aee-cb8b-433d-8956-dde5c161db69") && runtimeHash == ArrayConstructionRuntimeHash
                        || runtimeId == new Guid("90e01276-56cc-47ef-bec2-59618a86b96a") && runtimeHash == StyleEnumDiagnosticsRuntimeHash
                        || runtimeId == new Guid("e930ce94-b70a-4e5e-83a8-2c7974063ff2") && runtimeHash == StyleEnumSharedInvokerRuntimeHash
                        || runtimeId == new Guid("a52ee7b3-3678-4247-b552-37b6a46f8269") && runtimeHash == StyleEnumAbstractSlotsRuntimeHash))
                    throw new NotSupportedException("The loaded Il2CppInterop.Runtime does not match the Class::Init adapter.");

                var field = FindClassInitField(runtime);
                var expectedPath = Path.GetFullPath(gameAssemblyPath);
                using var process = Process.GetCurrentProcess();
                using var module = process.Modules.Cast<ProcessModule>().SingleOrDefault(candidate =>
                    string.Equals(candidate.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFullPath(candidate.FileName), expectedPath, StringComparison.OrdinalIgnoreCase));
                if (module == null || module.ModuleMemorySize != 0x6281000
                    || HashFile(module.FileName) != GameAssemblyHash)
                    throw new NotSupportedException("The loaded GameAssembly does not match the Class::Init adapter.");

                if (!NativeLibrary.TryGetExport(module.BaseAddress, "mono_class_instance_size", out var export)
                    || export != IntPtr.Add(module.BaseAddress, InstanceSizeRva))
                    throw new NotSupportedException("The native class-size export does not match the inspected entry.");

                // This export calls Class::Init, then reads instance_size. Bind its verified callee,
                // not the size-reading substitute or the class-init API that executes static constructors.
                var exportCode = ReadValidatedCode(module, InstanceSizeRva, 26,
                    "9178EFA9CF034BBA21FB6DCB428B76AEF14344E1D60557DC46C959675D436818");
                var target = IntPtr.Add(export, checked(14 + BitConverter.ToInt32(exportCode, 10)));
                if (target != IntPtr.Add(module.BaseAddress, ClassInitRva))
                    throw new NotSupportedException("The native class-size call no longer targets the inspected Class::Init.");
                ReadValidatedCode(module, ClassInitRva, 94,
                    "68BDE539DA40047FC485AF4A4CA3D16E4207F043E6C063433604797AF370E9DF");
                ReadValidatedCode(module, ClassInitLockedRva, 986,
                    "FFBF54C8CC7A8073A2B13515AACA9FAB5181F5359BA23CB2712AFAA31BF1F99D");

                if (field.GetValue(null) != null)
                {
                    InteropGenerationDiagnostics.Log(LogLevel.Debug, "Class::Init already has an interop initializer; preserving it.");
                    return;
                }

                // Use the dependency's checked Cdecl void(Il2CppClass*) type, rooted by its static field.
                var initializer = Marshal.GetDelegateForFunctionPointer(target, field.FieldType);
                field.SetValue(null, initializer);
                InteropGenerationDiagnostics.Log(LogLevel.Info,
                    "Resolved Class::Init at GameAssembly+0x508B80 using the matched native export call and code hashes.");
            }
            catch (Exception error)
            {
                InteropGenerationDiagnostics.Log(LogLevel.Warning,
                    "Direct Class::Init resolution was not applied; retaining Il2CppInterop's original resolver and fallback.", error);
            }
        }

        private static FieldInfo FindClassInitField(Assembly runtime)
        {
            var helpers = runtime.GetType("Il2CppInterop.Runtime.Injection.InjectorHelpers", throwOnError: true);
            var field = helpers.GetField("ClassInit", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var delegateType = helpers.GetNestedType("d_ClassInit", BindingFlags.NonPublic);
            if (!helpers.IsAbstract || !helpers.IsSealed || field == null || !field.IsStatic || !field.IsAssembly
                || field.DeclaringType != helpers
                || field.IsInitOnly || field.IsLiteral || delegateType == null || field.FieldType != delegateType
                || !delegateType.IsNestedAssembly || !delegateType.IsSealed
                || delegateType.BaseType != typeof(MulticastDelegate) || delegateType.ContainsGenericParameters
                || delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention != CallingConvention.Cdecl)
                throw new NotSupportedException("The pinned Class::Init field or native delegate contract changed.");

            var invoke = delegateType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var parameters = invoke?.GetParameters();
            var nativeClass = runtime.GetType("Il2CppInterop.Runtime.Runtime.Il2CppClass", throwOnError: true);
            if (invoke == null || invoke.ReturnType != typeof(void) || parameters.Length != 1
                || parameters[0].ParameterType != nativeClass.MakePointerType())
                throw new NotSupportedException("Class::Init must take exactly one Il2CppClass pointer and return void.");
            return field;
        }

        private static byte[] ReadValidatedCode(ProcessModule module, int rva, int length, string expectedHash)
        {
            if (rva < 0 || length <= 0 || rva > module.ModuleMemorySize - length)
                throw new NotSupportedException("Class::Init code is outside the loaded GameAssembly image.");
            var bytes = new byte[length];
            Marshal.Copy(IntPtr.Add(module.BaseAddress, rva), bytes, 0, bytes.Length);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
                throw new NotSupportedException($"Loaded GameAssembly code at RVA 0x{rva:X} does not match Class::Init evidence.");
            return bytes;
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
