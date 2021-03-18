using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection;

namespace BohemiaPlus
{
    public static class PluginInfo
    {
        public const string Name = "BohemiaPlus";
        public const string Guid = "Bohemia." + Name;
        public const string Version = "0.1";
    }

    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        public static ConfigEntry<double> DropDespawnTimer;
        public static ConfigEntry<double> ServerSleepPercent;
        public static ConfigEntry<int> ServerDataLimit;

        public static SyncedList adminList;
        public static BepInEx.Logging.ManualLogSource logger;

        public void Awake()
        {
            DropDespawnTimer = Config.Bind(PluginInfo.Name, "DropDespawnTimer", 3600.0, "Changes how long before a dropped item is despawned");
            ServerSleepPercent = Config.Bind(PluginInfo.Name, "ServerSleepPercent", 100.0, "Changes the percent of players needed to be sleeping to skip the night");
            ServerDataLimit = Config.Bind(PluginInfo.Name, "ServerDataLimit", 245760, "Changes the limit of data that the server sends per second");
            try
            {
                Harmony harmony = new Harmony(PluginInfo.Guid);
                harmony.PatchAll();
                Logger.LogInfo("Patching for BohemiaPlus");
                ZDOMan zDOMan = ZDOMan.instance;
                if (zDOMan != null)
                {
                    zDOMan.GetType().GetField("m_dataPerSec", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(zDOMan, ServerDataLimit.Value);
                }
            }
            catch (Exception e)
            {
                Logger.LogInfo(e);
            }
            logger = Logger;
        }

        public static void RPC_SendAnnouncement(ZRpc rpc, string message)
        {
            var znetInstance = ZNet.instance;
            if (adminList != null && !adminList.Contains(rpc.GetSocket().GetHostName()))
            {
                znetInstance.RemotePrint(rpc, "You are not admin");
                return;
            }
            InternalSendAnnouncement(rpc, message);
        }

        public static void InternalDeleteGroundItems(ZRpc rpc)
        {
            var znetInstance = ZNet.instance;
            znetInstance.RemotePrint(rpc, "Removing ground items...");
            ItemDrop[] pickableItems = UnityEngine.Object.FindObjectsOfType<ItemDrop>();
            znetInstance.RemotePrint(rpc, "Found: " + pickableItems.Length + " items drops.");
            foreach (ItemDrop item in pickableItems)
            {
                var nview = (ZNetView) item.GetType().GetField("m_nview", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(item);
                var save = item.GetType().GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance);

                znetInstance.RemotePrint(rpc, "Removing: " + item.name);
                nview.Destroy();
                save.Invoke(item, null);
            }
        }

        public static void InternalSendAnnouncement(ZRpc rpc, string message)
        {
            MessageHud.instance.MessageAll(MessageHud.MessageType.Center, message);
        }

        public static void CommandSendAnnouncement(string message)
        {
            var znetInstance = ZNet.instance;
            if (znetInstance.IsServer())
            {
                Plugin.InternalSendAnnouncement(null, message);
                return;
            }
            ZRpc serverRPC = znetInstance.GetServerRPC();
            if (serverRPC != null)
            {
                serverRPC.Invoke("SendAnnouncement", new object[] { message });
            }
        }

        [HarmonyPatch]
        public static class Patches
        {
            [HarmonyPrefix]
            [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
            public static void RPC_PeerInfo_Hook(ZRpc rpc, ZPackage pkg, ref SyncedList ___m_adminList)
            {
                logger.LogInfo("Adding new action SendAnnouncement to rpc...");
               
                Plugin.adminList = ___m_adminList;
                rpc.Register<string>("SendAnnouncement", new Action<ZRpc, string>(Plugin.RPC_SendAnnouncement));
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Console), "IsCheatsEnabled")]
            public static void Console_IsCheatsEnabled_Hook(ref bool __result)
            {
                
                __result = true;
            }

            [HarmonyReversePatch]
            [HarmonyPatch(typeof(ItemDrop), "TimedDestruction")]
            public static void ItemDrop_TimedDestruction(ItemDrop __instance)
            {
                var isInsideBase = __instance.GetType().GetMethod("IsInsideBase", BindingFlags.NonPublic | BindingFlags.Instance);
                if ((bool)isInsideBase.Invoke(__instance, null))
                {
                    return;
                }
                if (Player.IsPlayerInRange(__instance.gameObject.transform.position, 25f))
                {
                    return;
                }
                var getTimeSinceSpawned = __instance.GetType().GetMethod("GetTimeSinceSpawned", BindingFlags.NonPublic | BindingFlags.Instance);
                if ((double)getTimeSinceSpawned.Invoke(__instance, null) < Plugin.DropDespawnTimer.Value)
                {
                    return;
                }
                var nview = (ZNetView)__instance.GetType().GetField("m_nview", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
                nview.Destroy();
            }

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Console), "InputText")]
            public static void Console_InputText_Hook(Console __instance)
            {
                string text = __instance.m_input.text;
                logger.LogInfo(text);
                if (text.StartsWith("help"))
                {
                    logger.LogInfo("Inserting custom commands text");
                    var addText = __instance.GetType().GetMethod("AddString", BindingFlags.NonPublic | BindingFlags.Instance);
                    addText.Invoke(__instance, new object[] { "[BohemiaPlus] deleteGroundItems - Removes all dropped ground items" });
                    addText.Invoke(__instance, new object[] { "[BohemiaPlus] announce - Send an announcement to all players" });
                    
                }
                else
                {
                    string[] array = text.Split(new char[]
                    {
                        ' '
                    });
                    if (text.StartsWith("deleteGroundItems"))
                    {
                        Plugin.InternalDeleteGroundItems(null);
                    } else if (array[0] == "announce")
                    {
                        if (array.Length >= 1)
                        {
                            Plugin.CommandSendAnnouncement(new ArraySegment<string>(array, 1, array.Length - 1).Join(null, " "));
                        }
                    }
                }
            }

            public static int prevInBedCount = 0;

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Game), "EverybodyIsTryingToSleep")]
            public static void Game_EverybodyIsTryingToSleep(ref bool __result)
            {
                List<ZDO> allCharacterZDOS = ZNet.instance.GetAllCharacterZDOS();
                if (allCharacterZDOS.Count == 0)
                {
                    return;
                }
                int inBedCount = 0;
                using (List<ZDO>.Enumerator enumerator = allCharacterZDOS.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        if (enumerator.Current.GetBool("inBed", false))
                        {
                            inBedCount += 1;
                        }
                    }
                }
                int totalNeeded = (int)Math.Round(allCharacterZDOS.Count * (ServerSleepPercent.Value / 100.0));
                if (prevInBedCount != inBedCount)
                {
                    MessageHud.instance.MessageAll(MessageHud.MessageType.Center, String.Format("{0}/{1} players needed in bed to sleep",
                        inBedCount, totalNeeded));
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ChatMessage", new object[]
                    {
                        new Vector3(0, 0, 0),
                        2,
                        "Server",
                        String.Format("{0}/{1} players needed in bed to sleep", inBedCount, totalNeeded)
                    });
                }
                prevInBedCount = inBedCount;
                __result = inBedCount >= totalNeeded;
            }
        }
    }
}
