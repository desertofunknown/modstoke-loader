// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using TheForest.UI.Multiplayer;
using TheForest.Utils;
using ChatTable = Il2CppSystem.Collections.Generic.Dictionary<Bolt.NetworkId, TheForest.UI.Multiplayer.ChatBox.Player>;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private ChatBox _chat;
        private ChatTable _chatTable;
        private ChatBox.Player _chatPlayer;
        private string _chatName;
        private UnityEngine.Color _chatColor;

        private bool CaptureChat(out string detail)
        {
            detail = "The original chat owner/table must be available and stable.";
            var hud = HudGui.Instance;
            if (!Alive(hud) || !Alive(hud.Chatbox)) return false;
            _chat = hud.Chatbox;
            _chatTable = _chat._players;
            if (!Live(_chatTable)) return false;
            if (_chatTable.TryGetValue(new NetworkId(OriginalNetworkId), out var player))
            {
                if (!Live(player)) return false;
                _chatPlayer = player;
                _chatName = player._name;
                _chatColor = player._color;
            }
            return ChatOwnerCurrent();
        }

        private bool ChatOwnerCurrent() => Alive(HudGui.Instance) && Alive(_chat)
            && Same(HudGui.Instance.Chatbox, _chat) && Live(_chatTable) && Same(_chat._players, _chatTable);
        private bool ChatRetired() => ChatOwnerCurrent() && !_chatTable.ContainsKey(new NetworkId(OriginalNetworkId));
        private bool SameChatValues(ChatBox.Player player) => Live(player)
            && string.Equals(player._name, _chatName, StringComparison.Ordinal) && player._color.Equals(_chatColor);
        private bool CanRestoreChat(ulong id)
        {
            if (!ChatRetired() || (_chatPlayer != null && !SameChatValues(_chatPlayer))) return false;
            // Attached on the new carrier may already have populated the new key. Keep that
            // native record and its subscriptions; never replay attachment to obtain a record.
            return _chatPlayer == null || !_chatTable.TryGetValue(new NetworkId(id), out var existing)
                || SameChatValues(existing);
        }
        private void RestoreChat(ulong id, ref bool wrote)
        {
            if (!CanRestoreChat(id)) throw new InvalidOperationException("Chat identity changed.");
            if (_chatPlayer == null || _chatTable.ContainsKey(new NetworkId(id))) return;
            wrote = true;
            _chatTable.Add(new NetworkId(id), _chatPlayer);
        }
        private bool ChatRestored(ulong id) => ChatRetired() && (_chatPlayer == null
            || (_chatTable.TryGetValue(new NetworkId(id), out var existing) && SameChatValues(existing)));

        private bool NativeEquipmentPublishersAbsent()
        {
            // These were removed by qualified native BoltPlayerSetup.Detached. The managed
            // Equipment source subscriptions replace them, including race, with queued forwarding.
            // Native reinstallation would publish locally to a server-owned state after rebind.
            return Alive(_setup) && Alive(_setup._inventory) && Alive(_setup._inventoryProps)
                && Alive(_setup._clothingSystem) && Alive(_setup._armourSystem) && Alive(_setup._raceSystem)
                && NoSetupTarget(_setup._inventory.OnRightHandItemLit)
                && NoSetupTarget(_setup._inventory.OnItemTransferredToSlot)
                && NoSetupTarget(_setup._inventoryProps.OnHeldItemInitialized)
                && NoSetupTarget(_setup._inventoryProps.OnHeldItemRemoved)
                && NoSetupTarget(_setup._clothingSystem.OnClothingEquipped)
                && NoSetupTarget(_setup._clothingSystem.OnClothingUnequipped)
                && NoSetupTarget(_setup._armourSystem.OnArmourEquipped)
                && NoSetupTarget(_setup._armourSystem.OnArmourUnequipped)
                && NoSetupTarget(_setup._armourSystem.OnArmourBroken)
                && NoSetupTarget(_setup._raceSystem.OnRaceChanged);
        }
        private bool NoSetupTarget(Il2CppSystem.Delegate root)
        {
            if (root == null) return true;
            if (!Live(root)) return false;
            var list = root.TryCast<Il2CppSystem.MulticastDelegate>()?.delegates;
            if (list == null) return !Same(root.Target, _setup);
            if (!Live(list) || list.Length == 0 || list.Length > 256) return false;
            foreach (var entry in list)
            {
                if (!Live(entry) || Same(entry.Target, _setup)) return false;
                // Nested invocation lists cannot be checked using Method/Target's last entry.
                if (entry.TryCast<Il2CppSystem.MulticastDelegate>()?.delegates != null) return false;
            }
            return true;
        }
    }
}
