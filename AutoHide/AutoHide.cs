using BepInEx;
using BepInEx.Configuration;
using Bounce.Mathematics;
using Bounce.Unmanaged;
using DataModel;
using HarmonyLib;
using PluginUtilities;
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AutoHide
{
    [BepInPlugin(Guid, PluginName, Version)]
    [BepInDependency(SetInjectionFlag.Guid)]
    public class AutoHidePlugin : DependencyUnityPlugin<AutoHidePlugin>
    {
        public const string PluginName = "AutoHide Diagnostic Plugin";
        public const string Guid = "org.talespire.plugins.autohide";
        public const string Version = "0.0.1.0";

        private ConfigEntry<KeyboardShortcut> _toggleTrackingKey;
        private ConfigEntry<KeyboardShortcut> _staticHideVolumeKey;
        private ConfigEntry<bool> _autoStartTracking;
        private ConfigEntry<KeyboardShortcut> _probeTeleportKey;
        private ConfigEntry<KeyboardShortcut> _probeRefreshKey;
        private ConfigEntry<string> _probePosition;
        private static ConfigEntry<bool> _rememberSeenTerrain;
        private ConfigEntry<KeyboardShortcut> _boardVolumeCensusKey;
        private ConfigEntry<KeyboardShortcut> _boardVolumePurgeKey;
        private ConfigEntry<KeyboardShortcut> _gmBlockToggleKey;
        private ConfigEntry<KeyboardShortcut> _gmBlockCensusKey;
        private ConfigEntry<KeyboardShortcut> _gmBlockMemoryKey;
        private ConfigEntry<KeyboardShortcut> _gmBlockFogResetKey;

        private Harmony _harmony;

        // Real-time LoS tracking state
        private static bool _losTrackingActive;
        private static HideVolumeManager _cachedHvManager;
        private static BepInEx.Logging.ManualLogSource _log;

        private static short3 _creatureZoneCoord;

        // A zone is 16x16x16 cells of one world unit each.
        private const int ZONE_CELLS = 16;
        private const int MASK_LENGTH = ZONE_CELLS * ZONE_CELLS;

        // Active bit set; creature-filter bit set so creatures stay under TaleSpire's own LoS.
        private const byte HIDE_VOLUME_STATE = 1 | 8;

        private static readonly Dictionary<short3, ushort[]> _zoneMasks = new Dictionary<short3, ushort[]>();
        private static readonly HashSet<short3> _dirtyZones = new HashSet<short3>();
        private static readonly ushort[] _maskScratch = new ushort[MASK_LENGTH];
        private static readonly bool[] _cellFogged = new bool[ZONE_CELLS * ZONE_CELLS * ZONE_CELLS];
        private static readonly bool[] _cellScratch = new bool[ZONE_CELLS * ZONE_CELLS * ZONE_CELLS];
        private static readonly Dictionary<short3, List<HideVolume>> _zoneHideVolumes = new Dictionary<short3, List<HideVolume>>();
        private static int _rebuildLogCount;
        private int _pendingInitialRefresh;
        private int _lastBoardEventCounter;
        private const float REMOTE_CHANGE_DEBOUNCE = 0.25f;
        private static bool _remoteScriptStatePending;
        private float _nextRemoteChangeTime;
        private static ViewPointManager.ViewMapRef _lastViewMapRef;
        private static bool _hasLastViewMapRef;

        private static HideVolume? _staticTestHideVolume;

        private static MethodInfo _btnPlayAsPlayerMethod;
        private static MethodInfo _btnPlayAsGMMethod;

        // FogMask GPU LoS interception — reflected members on ComputeFogUpdateMaskTask
        private static FogMaskManager _cachedFogMaskManager;
        private static MethodInfo _removeFogUsingViewMethod;   // FogMaskManager.RemoveFogUsingView(ViewMapRef)
        private static MethodInfo _getTaskObjectMethod;         // FogMaskManager.GetTaskObject(Zone, ref ViewMapRef)
        private static FieldInfo _taskQueueField;               // static Queue<ComputeFogUpdateMaskTask>
        private static MethodInfo _viewMapRefStillValidMethod;  // ViewPointManager.ViewMapRefStillValid(ref ViewMapRef)
        private static FieldInfo _fmtZoneField;                 // ComputeFogUpdateMaskTask._zone
        private static FieldInfo _fmtUpdateMaskField;           // ComputeFogUpdateMaskTask._updateMask
        private static FieldInfo _fmtViewMapField;              // ComputeFogUpdateMaskTask._viewMap

        // UpdateProcess() sets _zone=null before returning true; capture it in the Prefix
        // (single-threaded Unity main loop — no lock needed)
        private static Zone _fogTaskPendingZone;

        private static int _diagnosticTaskCount;
        private static int _viewCaptureLogCount;
        private static int _zoneCensusLogCount;

        private bool _autoStartDone;

        protected override void OnAwake()
        {
            // Bare function keys collide with TaleSpire: F8 is its Player/GM mode toggle
            // (which fights this plugin directly, since it also switches mode) and F9 opens
            // the camera settings panel. Modifier combos keep us out of its way.
            _toggleTrackingKey = Config.Bind("Controls", "ToggleTracking",
                new KeyboardShortcut(KeyCode.H, KeyCode.LeftControl),
                "Toggle real-time line-of-sight tile hiding for the selected creature");

            // The diagnostics below all write to the board or move creatures, so they ship
            // unbound — set a key explicitly if you need them.
            _staticHideVolumeKey = Config.Bind("Controls", "StaticHideVolumeTest",
                KeyboardShortcut.Empty,
                "Add/remove a HideVolume over the selected creature's zone (sanity check). "
                + "Writes a networked, board-persisted volume — unbound by default.");

            _probeTeleportKey = Config.Bind("Diagnostics", "ProbeTeleport",
                KeyboardShortcut.Empty,
                "Teleport the selected creature to ProbePosition and re-dump the fog mask grid");
            _probePosition = Config.Bind("Diagnostics", "ProbePosition", "",
                "Absolute world position for ProbeTeleport, as \"x,y,z\".");
            _probeRefreshKey = Config.Bind("Diagnostics", "ProbeRefresh",
                new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl),
                "Re-run the line-of-sight pass without moving anything");

            _boardVolumeCensusKey = Config.Bind("Diagnostics", "BoardVolumeCensus",
                new KeyboardShortcut(KeyCode.K, KeyCode.LeftControl),
                "Log how many hide volumes the board itself carries, grouped by size");

            _boardVolumePurgeKey = Config.Bind("Diagnostics", "BoardVolumePurge",
                KeyboardShortcut.Empty,
                "Remove EVERY hide volume stored on the board, dumping each one to the log first. "
                + "Recovery tool for a board polluted by an earlier build of this plugin — it also "
                + "deletes hand-placed hide volumes, so it ships unbound.");

            // The GM block is the board-level switch: while one is present, every client with
            // the plugin runs line-of-sight hiding, with no per-player toggling.
            _gmBlockToggleKey = Config.Bind("Controls", "GmBlockToggle",
                new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl),
                "Arm or disarm line-of-sight hiding for the whole board, by placing or removing an "
                + "AutoHide GM block at the selected creature. GM only — it writes to the board.");
            _gmBlockMemoryKey = Config.Bind("Controls", "GmBlockToggleMemory",
                new KeyboardShortcut(KeyCode.M, KeyCode.LeftControl),
                "Flip the seen-terrain memory flag on the AutoHide GM block, for every client on the board");
            _gmBlockFogResetKey = Config.Bind("Controls", "GmBlockResetFog",
                new KeyboardShortcut(KeyCode.B, KeyCode.LeftControl),
                "Forget all seen terrain on every client on the board, by bumping the reset counter "
                + "on the AutoHide GM block");

            _gmBlockCensusKey = Config.Bind("Diagnostics", "GmBlockCensus",
                KeyboardShortcut.Empty,
                "Log every GM block the board carries and whether it is marked as ours");

            _rememberSeenTerrain = Config.Bind("Behaviour", "RememberSeenTerrain", true,
                "Keep terrain visible once it has been seen, the way fog of war does, instead of "
                + "re-hiding it when it leaves line of sight. Creatures are unaffected — TaleSpire "
                + "hides those by its own line of sight. Memory is discarded when tracking is "
                + "switched off.");

            _autoStartTracking = Config.Bind("Diagnostics", "AutoStartTracking", false,
                "Start LoS tracking automatically once a board is loaded, without needing a keypress. "
                + "Intended for headless/automated testing.");

            _log = Logger;
            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            SetupFogMaskReflection();

            Logger.LogInfo($"[AutoHide] Plugin loaded. Toggle={_toggleTrackingKey.Value} StaticHV={_staticHideVolumeKey.Value} AutoStart={_autoStartTracking.Value}");
        }

        private void SetupFogMaskReflection()
        {
            try
            {
                var fogTaskType = typeof(FogMaskManager).GetNestedType("ComputeFogUpdateMaskTask",
                    BindingFlags.NonPublic | BindingFlags.Public);
                if (fogTaskType == null)
                {
                    Logger.LogWarning("[AutoHide] ComputeFogUpdateMaskTask nested type not found");
                    return;
                }

                _fmtZoneField = fogTaskType.GetField("_zone",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _fmtUpdateMaskField = fogTaskType.GetField("_updateMask",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _fmtViewMapField = fogTaskType.GetField("_viewMap",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                var updateProcessMethod = fogTaskType.GetMethod("UpdateProcess",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                Logger.LogInfo($"[AutoHide] ComputeFogUpdateMaskTask: _zone={_fmtZoneField != null} _updateMask={_fmtUpdateMaskField != null} _viewMap={_fmtViewMapField != null} UpdateProcess={updateProcessMethod != null}");

                if (updateProcessMethod != null)
                {
                    var prefix = AccessTools.Method(typeof(AutoHidePlugin), nameof(FogTaskUpdateProcessPrefix));
                    var postfix = AccessTools.Method(typeof(AutoHidePlugin), nameof(FogTaskUpdateProcessPostfix));
                    _harmony.Patch(updateProcessMethod,
                        prefix: new HarmonyMethod(prefix),
                        postfix: new HarmonyMethod(postfix));
                    Logger.LogInfo("[AutoHide] Patched ComputeFogUpdateMaskTask.UpdateProcess (prefix+postfix)");
                }

                _getTaskObjectMethod = typeof(FogMaskManager).GetMethod("GetTaskObject",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _taskQueueField = typeof(FogMaskManager).GetField("_taskQueue",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _removeFogUsingViewMethod = typeof(FogMaskManager).GetMethod("RemoveFogUsingView",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                _viewMapRefStillValidMethod = typeof(ViewPointManager).GetMethod("ViewMapRefStillValid",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                Logger.LogInfo($"[AutoHide] FogMask reflection: GetTaskObject={_getTaskObjectMethod != null} _taskQueue={_taskQueueField != null} RemoveFogUsingView={_removeFogUsingViewMethod != null} ViewMapRefStillValid={_viewMapRefStillValidMethod != null}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoHide] SetupFogMaskReflection: {ex}");
            }
        }

        // Prefix: capture _zone before UpdateProcess() clears it (it sets _zone=null before returning true).
        private static void FogTaskUpdateProcessPrefix(object __instance)
        {
            _fogTaskPendingZone = null;
            try
            {
                if (_fmtZoneField != null)
                    _fogTaskPendingZone = _fmtZoneField.GetValue(__instance) as Zone;
            }
            catch { }
        }

        // Postfix: fires after UpdateProcess() returns. Use _fogTaskPendingZone (captured in Prefix)
        // because _zone is already null by the time we run. _updateMask is still valid until Dispose().

        // Top-down view of the update mask: one line per local z, one character per local x,
        // the character being how many of the 16 y levels are fogged. Directly comparable to a
        // top-down screenshot of the board.
        // Matches DataModel.FogOfWarMath.MaskIndexYZ: one ushort per (y, z) row, bit index = x.
        private static int MaskIndexYZ(int y, int z) => y + z * ZONE_CELLS;

        private static void LogMaskGrid(NativeArray<ushort> mask, Zone zone)
        {
            const string RAMP = ".123456789abcdeF";
            _log?.LogInfo($"[AutoHide] MaskGrid zone={zone.Coord} origin={zone.Origin} (row=z, col=x, char=fogged y count)");
            for (int z = 0; z < ZONE_CELLS; z++)
            {
                var line = new System.Text.StringBuilder();
                for (int x = 0; x < ZONE_CELLS; x++)
                {
                    int bits = 0;
                    for (int y = 0; y < ZONE_CELLS; y++)
                        if ((mask[MaskIndexYZ(y, z)] & (1 << x)) != 0) bits++;
                    line.Append(bits == 0 ? '.' : bits == ZONE_CELLS ? 'F' : RAMP[bits]);
                }
                _log?.LogInfo($"[AutoHide] MaskGrid z{z,2}: {line}");
            }
        }

        private static void FogTaskUpdateProcessPostfix(object __instance, bool __result)
        {
            if (!__result || !_losTrackingActive) return;
            var zone = _fogTaskPendingZone;
            if (zone == null) return;
            try
            {
                if (_fmtUpdateMaskField == null) return;
                var mask = (NativeArray<ushort>)_fmtUpdateMaskField.GetValue(__instance);

                int ffCount = 0, zeroCount = 0, otherCount = 0;
                for (int i = 0; i < mask.Length; i++)
                {
                    if (mask[i] == 0xFFFF) ffCount++;
                    else if (mask[i] == 0) zeroCount++;
                    else otherCount++;
                }
                int diagIdx = System.Threading.Interlocked.Increment(ref _diagnosticTaskCount);
                if (diagIdx <= 8)
                {
                    string vmInfo = "vmField=null";
                    if (_fmtViewMapField != null)
                    {
                        var vm = _fmtViewMapField.GetValue(__instance);
                        if (vm == null)
                        {
                            vmInfo = "viewMap=NULL";
                        }
                        else
                        {
                            var cubemapProp = vm.GetType().GetProperty("Cubemap",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var cubemap = cubemapProp?.GetValue(vm) as RenderTexture;
                            vmInfo = $"viewMap=ok cubemap={cubemap != null} created={cubemap?.IsCreated()}";
                        }
                    }

                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(8, mask.Length); i++)
                        sb.Append($"{mask[i]:X4} ");

                    _log?.LogInfo($"[AutoHide] Diag#{diagIdx} zone={zone.Coord} origin={zone.Origin} {vmInfo}");
                    _log?.LogInfo($"[AutoHide] Diag#{diagIdx} mask[0..7]={sb.ToString().TrimEnd()} dist={{0xFFFF={ffCount} zero={zeroCount} other={otherCount}}}");

                    if (diagIdx == 1) LogMaskGrid(mask, zone);
                }

                if (diagIdx <= 8)
                    _log?.LogInfo($"[AutoHide] FogTask done: zone={zone.Coord} maskLen={mask.Length} (0xFFFF={ffCount} zero={zeroCount} other={otherCount})");

                lock (_zoneMasks)
                {
                    bool remember = RememberSeenTerrain;
                    bool changed = !_zoneMasks.TryGetValue(zone.Coord, out var buf);
                    if (changed)
                    {
                        buf = new ushort[MASK_LENGTH];
                        // A remembered mask only ever clears bits, so it has to start fully
                        // fogged or the zone would be treated as already seen.
                        if (remember)
                            for (int i = 0; i < MASK_LENGTH; i++) buf[i] = ushort.MaxValue;
                        _zoneMasks[zone.Coord] = buf;
                    }

                    // Rebuilding a zone tears down and re-adds its volumes and dirties the
                    // zone's presentation. Most moves leave most zones' visibility untouched,
                    // so only rebuild the ones whose mask actually changed.
                    for (int i = 0; i < MASK_LENGTH; i++)
                    {
                        ushort next = remember ? (ushort)(buf[i] & mask[i]) : mask[i];
                        if (buf[i] == next) continue;
                        buf[i] = next;
                        changed = true;
                    }

                    if (changed) _dirtyZones.Add(zone.Coord);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[AutoHide] FogTaskUpdateProcessPostfix: {ex}");
            }
        }

        protected override void OnDestroyed()
        {
            // Zone.CollectDataForSector serialises _localHideVolumes, so any volume left
            // behind here would get written into the board's saved sector data.
            RemoveAllZoneHideVolumes();
            if (_losTrackingActive) SwitchToGMMode();
            _losTrackingActive = false;
            _pendingInitialRefresh = 0;
            _cachedHvManager = null;
            _cachedFogMaskManager = null;

            _harmony?.UnpatchSelf();
            Logger.LogInfo("[AutoHide] Plugin unloaded.");
        }

        private static bool TryGetSelectedCreature(out CreatureGuid creatureId)
        {
            creatureId = LocalClient.SelectedCreatureId;
            if (creatureId.Equals(default(CreatureGuid)))
            {
                _log?.LogWarning("[AutoHide] No creature selected.");
                return false;
            }
            return true;
        }

        private static bool TryGetCreatureWorldPos(CreatureGuid creatureId, out Vector3 worldPos)
        {
            worldPos = default;
            if (!CreaturePresenter.TryGetAsset(creatureId, out var asset))
            {
                _log?.LogWarning("[AutoHide] Could not find creature asset.");
                return false;
            }
            try
            {
                worldPos = asset.Transform.position;
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[AutoHide] asset.Transform.position failed: {ex.Message}");
                var lp = asset.LastPlacedPosition;
                worldPos = new Vector3(lp.x, lp.y, lp.z);
                return true;
            }
        }

        private static bool TryFindZoneForWorldPos(Vector3 worldPos, out Zone zone)
        {
            zone = default;
            var board = BoardSessionManager.Board;
            if (board == null)
            {
                _log?.LogWarning("[AutoHide] BoardSessionManager.Board is null.");
                return false;
            }

            float sl = Zone.SIDE_LENGTH;
            var coord = new short3(
                (short)(int)Math.Floor((double)(worldPos.x / sl)),
                (short)(int)Math.Floor((double)(worldPos.y / sl)),
                (short)(int)Math.Floor((double)(worldPos.z / sl)));

            if (!board.TryGetZone(coord, out zone))
            {
                _log?.LogWarning($"[AutoHide] No zone found at coord {coord} (world pos {worldPos}).");
                return false;
            }
            return true;
        }

        private void ToggleLosTracking() => SetLosTracking(!_losTrackingActive, switchClientMode: true);

        private void SetLosTracking(bool enable, bool switchClientMode)
        {
            try
            {
                if (!enable)
                {
                    if (!_losTrackingActive) return;
                    _losTrackingActive = false;
                    RemoveAllZoneHideVolumes();
                    if (switchClientMode) SwitchToGMMode();
                    Logger.LogInfo($"[AutoHide] [LoS] Tracking OFF — hide volumes removed{(switchClientMode ? ", restored GM mode" : "")}.");
                }
                else
                {
                    if (_losTrackingActive) return;
                    _losTrackingActive = true;
                    _diagnosticTaskCount = 0;
                    _viewCaptureLogCount = 0;
                    _zoneCensusLogCount = 0;
                    EnsureHvManager();
                    EnsureFogMaskManager();
                    _lastBoardEventCounter = BoardSessionManager.Board?.SyncworthyGameEventsCounter ?? 0;
                    if (switchClientMode) SwitchToPlayerMode();
                    Logger.LogInfo($"[AutoHide] [LoS] Tracking ON{(switchClientMode ? " — switched to player view" : "")} for LoS tile hiding.");
                    _pendingInitialRefresh = 10;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[AutoHide] [LoS] Toggle: {ex}");
            }
        }

        // Opening a door bumps Board.SyncworthyGameEventsCounter, which invalidates every
        // cached view map — but nothing in TaleSpire then asks for a new capture, since it
        // only does that when a creature moves. Without this the terrain stays hidden as it
        // was before the door swung. Our own hiding runs through Zone.SetHideVolume rather
        // than Board, so it does not bump the counter and cannot feed back into this.
        private void PollBoardChanges()
        {
            var board = BoardSessionManager.Board;
            if (board == null) return;

            // Fold remote script ops into the counter, rate limited: a scripted prop can send
            // one every frame and each bump costs the whole party a fresh view capture.
            if (_remoteScriptStatePending && Time.time >= _nextRemoteChangeTime)
            {
                _remoteScriptStatePending = false;
                _nextRemoteChangeTime = Time.time + REMOTE_CHANGE_DEBOUNCE;
                board.SyncworthyGameEventsCounter++;
            }

            if (board.SyncworthyGameEventsCounter == _lastBoardEventCounter) return;

            _lastBoardEventCounter = board.SyncworthyGameEventsCounter;
            Logger.LogInfo($"[AutoHide] [LoS] Board changed (event #{_lastBoardEventCounter}) — refreshing.");
            ForceLosRefresh();
        }

        private void ProbeTeleport()
        {
            try
            {
                var parts = _probePosition.Value.Split(',');
                if (parts.Length != 3
                    || !float.TryParse(parts[0].Trim(), out var px)
                    || !float.TryParse(parts[1].Trim(), out var py)
                    || !float.TryParse(parts[2].Trim(), out var pz))
                {
                    Logger.LogWarning($"[AutoHide] [Probe] ProbePosition is not \"x,y,z\": '{_probePosition.Value}'");
                    return;
                }

                var id = LocalClient.SelectedCreatureId;
                if (!CreaturePresenter.TryGetAsset(id, out var asset) || asset == null)
                { Logger.LogWarning("[AutoHide] [Probe] No creature asset."); return; }

                CreatureManager.DropAtPosition(id,
                    new Unity.Mathematics.float3(px, py, pz),
                    Unity.Mathematics.quaternion.identity);
                Logger.LogInfo($"[AutoHide] [Probe] Teleported creature to ({px:F1},{py:F1},{pz:F1})");
                ProbeRefresh();
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [Probe] {ex}"); }
        }

        private void ProbeRefresh()
        {
            try
            {
                _diagnosticTaskCount = 0;   // re-arm the grid dump
                if (CreaturePresenter.TryGetAsset(LocalClient.SelectedCreatureId, out var asset) && asset != null)
                    Logger.LogInfo($"[AutoHide] [Probe] Creature at {asset.transform.position}");
                ForceLosRefresh();
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [Probe] {ex}"); }
        }

        // MaybeUpdatePerceptionForParty only requests a new view capture when the creature
        // has moved far enough to invalidate the cached one, so on its own it cannot drive
        // a refresh for a stationary creature. The cached cubemap is still correct in that
        // case, so re-queue the fog tasks against it directly.
        private void ForceLosRefresh()
        {
            try
            {
                CreaturePerceptionManager.MaybeUpdatePerceptionForParty();

                if (!_hasLastViewMapRef) return;
                if (_viewMapRefStillValidMethod != null)
                {
                    var args = new object[] { _lastViewMapRef };
                    if (!(bool)_viewMapRefStillValidMethod.Invoke(null, args))
                    {
                        _hasLastViewMapRef = false;
                        return;
                    }
                }

                var p = _lastViewMapRef.ViewPosition;
                QueueFogMaskTasksForNearbyZones(new Vector3(p.x, p.y, p.z), _lastViewMapRef);
                Logger.LogInfo($"[AutoHide] [LoS] Forced refresh from cached view map at ({p.x:F1},{p.y:F1},{p.z:F1})");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [LoS] ForceLosRefresh: {ex.Message}"); }
        }

        private static void EnsureHvManager()
        {
            if (_cachedHvManager == null)
                _cachedHvManager = UnityEngine.Object.FindObjectOfType<HideVolumeManager>();
        }

        private static void EnsureFogMaskManager()
        {
            if (_cachedFogMaskManager != null) return;
            _cachedFogMaskManager = UnityEngine.Object.FindObjectOfType<FogMaskManager>();
            _log?.LogInfo($"[AutoHide] FogMaskManager instance: {(_cachedFogMaskManager != null ? "found" : "NOT FOUND")}");
        }

        private static void EnsureClientModeMethods()
        {
            if (_btnPlayAsPlayerMethod != null) return;
            _btnPlayAsPlayerMethod = typeof(LocalClient).GetMethod("BtnPlayAsPlayer",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _btnPlayAsGMMethod = typeof(LocalClient).GetMethod("BtnPlayAsGM",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _log?.LogInfo($"[AutoHide] ClientMode methods: BtnPlayAsPlayer={_btnPlayAsPlayerMethod != null}, BtnPlayAsGM={_btnPlayAsGMMethod != null}");
        }

        private static void InvokeClientModeBtn(MethodInfo method)
        {
            if (method == null) return;
            try
            {
                var paramType = method.GetParameters()[0].ParameterType;
                var arg = paramType.IsValueType ? Activator.CreateInstance(paramType) : null;
                method.Invoke(null, new[] { arg });
                _log?.LogInfo($"[AutoHide] {method.Name} called — IsInGmMode={LocalClient.IsInGmMode}");
            }
            catch (Exception ex) { _log?.LogWarning($"[AutoHide] {method.Name} failed: {ex.Message}"); }
        }

        private static void SwitchToPlayerMode()
        {
            EnsureClientModeMethods();
            InvokeClientModeBtn(_btnPlayAsPlayerMethod);
        }

        private static void SwitchToGMMode()
        {
            EnsureClientModeMethods();
            InvokeClientModeBtn(_btnPlayAsGMMethod);
        }

        // Sanity check that AddHideVolume still hides tiles on the current TaleSpire build.
        private void ToggleStaticHideVolume()
        {
            try
            {
                EnsureHvManager();
                if (_cachedHvManager == null) { Logger.LogWarning("[AutoHide] [StaticHV] HideVolumeManager not found."); return; }

                if (_staticTestHideVolume.HasValue)
                {
                    Logger.LogInfo($"[AutoHide] [StaticHV] Calling RemoveHideVolume id={_staticTestHideVolume.Value.Id}");
                    _cachedHvManager.RemoveHideVolume(_staticTestHideVolume.Value.Id);
                    _staticTestHideVolume = null;
                    Logger.LogInfo("[AutoHide] [StaticHV] Removed — tiles should reappear.");
                    return;
                }

                if (!TryGetSelectedCreature(out var creatureId)) return;
                if (!TryGetCreatureWorldPos(creatureId, out var worldPos)) return;
                if (!TryFindZoneForWorldPos(worldPos, out var zone)) return;

                var hv = new HideVolume(NGuid.GenerateNewGuid(), zone.Bounds, byte.MaxValue, HideVolume.SMALLEST_VALID_VERSION)
                    .CloneSettingActive(true).CloneSettingCreatureHide(true).CloneSettingPlaceableHide(true);
                Logger.LogInfo($"[AutoHide] [StaticHV] Calling AddHideVolume: coord={zone.Coord} center={zone.Bounds.center} size={zone.Bounds.size}");
                _cachedHvManager.AddHideVolume(hv);
                _staticTestHideVolume = hv;
                Logger.LogInfo("[AutoHide] [StaticHV] Added — tiles in this zone should now be hidden. Press again to remove.");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [StaticHV] {ex}"); }
        }

        // A door someone else opens arrives as a script state op, and unlike the local
        // Board.SendScriptMessage path that one never bumps SyncworthyGameEventsCounter. The
        // cached view maps therefore stay "valid" and every client but the one who opened the
        // door keeps seeing the pre-door line of sight. Bump it so a remote change looks like
        // a local one; only while tracking is on, so an idle plugin changes nothing.
        [HarmonyPatch(typeof(Board), "ApplyOp", new[] { typeof(MessageInfo), typeof(ClientGuid), typeof(ScriptStateOp) })]
        static class PatchRemoteScriptState
        {
            static void Postfix(ApplyResult __result)
            {
                if (_losTrackingActive && __result == ApplyResult.Ok) _remoteScriptStatePending = true;
            }
        }

        // A sector upload copies Zone._localHideVolumes verbatim into the saved board, so any
        // board change made while tracking is active bakes our line-of-sight boxes in
        // permanently — a GM who opens a door has polluted their board. Lift ours out for the
        // duration of the collect and put them straight back: the collect copies the list
        // synchronously, and the dirty flag Set/RemoveHideVolume raises is only consumed later,
        // so the restored set is what gets presented and nothing flickers.
        [HarmonyPatch(typeof(Zone), "CollectDataForSector")]
        static class PatchZoneCollectDataForSector
        {
            static void Prefix(Zone __instance, out List<HideVolume> __state)
            {
                __state = null;
                if (!_zoneHideVolumes.TryGetValue(__instance.Coord, out var ours) || ours.Count == 0) return;
                foreach (var hv in ours) __instance.RemoveHideVolume(in hv);
                __state = ours;
                _log?.LogInfo($"[AutoHide] [Save] Withheld {ours.Count} LoS volume(s) from zone {__instance.Coord}");
            }

            static void Postfix(Zone __instance, List<HideVolume> __state)
            {
                if (__state == null) return;
                foreach (var hv in __state) __instance.SetHideVolume(in hv, false);
            }
        }

        // Fires when the perception system finishes a view capture for the controlled creature.
        // Used to trigger FogMaskManager to compute zone-level LoS via the GPU depth cubemap.
        [HarmonyPatch(typeof(CreaturePerceptionManager), "OnViewCaptureReady")]
        static class PatchViewCaptureReady
        {
            static void Postfix(CreatureGuid creatureId, ViewPointManager.ViewMapRef viewMapRef)
            {
                // Gate on control, not selection: LoS belongs to the creature the local
                // client drives, and clicking a token is not required for perception to run.
                bool controlled = LocalClient.HasControlOfCreature(creatureId);

                // Cache even while tracking is off, so the first toggle-on has a view map
                // to refresh from without waiting for the creature to move.
                if (controlled)
                {
                    _lastViewMapRef = viewMapRef;
                    _hasLastViewMapRef = true;
                }

                if (!_losTrackingActive) return;

                if (_viewCaptureLogCount < 12)
                {
                    _viewCaptureLogCount++;
                    _log?.LogInfo($"[AutoHide] ViewCapture#{_viewCaptureLogCount} creature={creatureId} "
                        + $"controlled={controlled} selected={LocalClient.SelectedCreatureId}");
                }
                if (!controlled) return;
                try
                {
                    var pos = new Vector3(viewMapRef.ViewPosition.x, viewMapRef.ViewPosition.y, viewMapRef.ViewPosition.z);
                    float sl = Zone.SIDE_LENGTH;
                    _creatureZoneCoord = new short3(
                        (short)(int)Math.Floor(pos.x / sl),
                        (short)(int)Math.Floor(pos.y / sl),
                        (short)(int)Math.Floor(pos.z / sl));

                    QueueFogMaskTasksForNearbyZones(pos, viewMapRef);

                    _log?.LogInfo($"[AutoHide] ViewCapture ready: pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) zone={_creatureZoneCoord}");
                }
                catch (Exception ex) { _log?.LogWarning($"[AutoHide] PatchViewCaptureReady: {ex}"); }
            }
        }

        private static void QueueFogMaskTasksForNearbyZones(Vector3 pos, ViewPointManager.ViewMapRef viewMapRef)
        {
            if (_cachedFogMaskManager == null) return;

            // FogMaskManager.RemoveFogUsingView only queues zones that already carry FoW data,
            // so queue every nearby zone ourselves instead — the fog state is ours to drive now.
            if (_getTaskObjectMethod == null || _taskQueueField == null) return;

            var board = BoardSessionManager.Board;
            if (board == null) return;

            const float LOS_RANGE = 48f;
            Board.ZonesInBounds(new Bounds(pos, Vector3.one * LOS_RANGE * 2f),
                out var min, out var max, out _, out _);

            var taskQueue = _taskQueueField.GetValue(null);
            var enqueueMethod = taskQueue?.GetType().GetMethod("Enqueue");
            if (taskQueue == null || enqueueMethod == null)
            {
                _log?.LogWarning("[AutoHide] _taskQueue or Enqueue not accessible");
                return;
            }

            int queued = 0, tried = 0, noZone = 0;
            for (short x = min.x; x <= max.x; x++)
            for (short y = min.y; y <= max.y; y++)
            for (short z = min.z; z <= max.z; z++)
            {
                var coord = new short3(x, y, z);
                tried++;
                if (!board.TryGetZone(coord, out var zone)) { noZone++; continue; }

                try
                {
                    var args = new object[] { zone, viewMapRef };
                    var task = _getTaskObjectMethod.Invoke(_cachedFogMaskManager, args);
                    if (task != null)
                    {
                        enqueueMethod.Invoke(taskQueue, new[] { task });
                        queued++;
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogWarning($"[AutoHide] GetTaskObject zone={coord}: {ex.Message}");
                    break;
                }
            }

            if (_zoneCensusLogCount < 3)
            {
                _zoneCensusLogCount++;
                _log?.LogInfo($"[AutoHide] ZoneCensus: tried={tried} noZone={noZone} queued={queued} range=({min})→({max})");
            }
        }

        private void Update()
        {
            if (_toggleTrackingKey.Value.IsDown()) ToggleLosTracking();
            if (_staticHideVolumeKey.Value.IsDown()) ToggleStaticHideVolume();
            if (_probeTeleportKey.Value.IsDown()) ProbeTeleport();
            if (_probeRefreshKey.Value.IsDown()) ProbeRefresh();
            if (_boardVolumeCensusKey.Value.IsDown()) LogBoardHideVolumeCensus();
            if (_boardVolumePurgeKey.Value.IsDown()) PurgeBoardHideVolumes();
            if (_gmBlockToggleKey.Value.IsDown()) ToggleAutoHideGmBlock();
            if (_gmBlockCensusKey.Value.IsDown()) LogGmBlockCensus();
            if (_gmBlockMemoryKey.Value.IsDown())
                SetGmBlockFlags(f => f ^ GM_FLAG_REMEMBER, "toggled seen-terrain memory");
            if (_gmBlockFogResetKey.Value.IsDown())
                SetGmBlockFlags(f => (f & ~GM_FLAG_RESET_GEN_MASK)
                    | ((((f & GM_FLAG_RESET_GEN_MASK) >> GM_FLAG_RESET_GEN_SHIFT) + 1) << GM_FLAG_RESET_GEN_SHIFT) & GM_FLAG_RESET_GEN_MASK,
                    "bumped fog reset counter");

            PollModeGmBlock();

            if (!_autoStartDone && _autoStartTracking.Value && !_losTrackingActive
                && BoardSessionManager.Board != null)
            {
                _autoStartDone = true;
                Logger.LogInfo("[AutoHide] AutoStartTracking enabled — starting LoS tracking.");
                ToggleLosTracking();
            }

            // The player-mode switch takes a few frames to settle; a refresh requested
            // before then is dropped and the first LoS pass never runs.
            if (_pendingInitialRefresh > 0 && --_pendingInitialRefresh == 0)
                ForceLosRefresh();

            if (_losTrackingActive)
            {
                PollBoardChanges();
                ProcessFogResults();
            }
        }

        private void LogBoardHideVolumeCensus()
        {
            try
            {
                var board = BoardSessionManager.Board;
                if (board == null) { Logger.LogInfo("[AutoHide] [Census] No board."); return; }

                var field = typeof(Board).GetField("_hideVolumes",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) { Logger.LogWarning("[AutoHide] [Census] Board._hideVolumes not found."); return; }

                var volumes = (NativeList<HideVolume>)field.GetValue(board);
                var bySize = new Dictionary<string, int>();
                for (int i = 0; i < volumes.Length; i++)
                {
                    var s = volumes[i].Bounds.size;
                    string key = $"{s.x:F1}x{s.y:F1}x{s.z:F1}";
                    bySize.TryGetValue(key, out var n);
                    bySize[key] = n + 1;
                }

                Logger.LogInfo($"[AutoHide] [Census] Board carries {volumes.Length} hide volume(s), {bySize.Count} distinct size(s)");
                foreach (var kv in bySize)
                    Logger.LogInfo($"[AutoHide] [Census]   size {kv.Key} x{kv.Value}");
                for (int i = 0; i < Math.Min(5, volumes.Length); i++)
                    Logger.LogInfo($"[AutoHide] [Census]   sample[{i}] bounds={volumes[i].Bounds} active={volumes[i].IsActive} hidePlaceables={volumes[i].HidePlaceables}");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [Census] {ex}"); }
        }

        // AtmosphereBlock.Content is declared, serialized and synced, but nothing in TaleSpire
        // ever reads or writes it — the placer leaves it zeroed. That makes it 16 bytes of free
        // board-persisted storage: three words of signature so we can recognise our own blocks,
        // and one word of mode flags.
        private const uint GM_BLOCK_SIG_X = 0xA07041DEu;
        private const uint GM_BLOCK_SIG_Y = 0x8C3F5B12u;
        private const uint GM_BLOCK_SIG_Z = 0x1F4E7A93u;

        // Flags word layout: bits 0-7 are toggles, 8-11 the fog-reset generation, 12-31 spare.
        // A "forget seen terrain" request is an event, not state, so it rides as a counter:
        // clients act on a *change*, which makes it idempotent and correct for a client that
        // joins after the GM pressed it. Four bits is ample — aliasing needs a client to be
        // away for exactly 16 resets, and costs it one stale fog memory if it ever happens.
        private const uint GM_FLAG_LOS = 1u << 0;
        private const uint GM_FLAG_REMEMBER = 1u << 1;
        private const int GM_FLAG_RESET_GEN_SHIFT = 8;
        private const uint GM_FLAG_RESET_GEN_MASK = 0xFu << GM_FLAG_RESET_GEN_SHIFT;

        // Set while a GM block dictates the mode, so the board's setting wins over the local one.
        private static bool? _boardRememberOverride;
        private bool _boardDrivenTracking;
        private bool _haveResetGeneration;
        private uint _lastResetGeneration;
        private float _nextGmBlockPoll;

        private static bool RememberSeenTerrain =>
            _boardRememberOverride ?? _rememberSeenTerrain.Value;

        private static bool IsAutoHideBlock(GmBlock block, out AtmosphereBlock atmos, out uint flags)
        {
            atmos = block as AtmosphereBlock;
            flags = 0;
            if (atmos == null) return false;
            var d = atmos.Content.Data;
            if (d.x != GM_BLOCK_SIG_X || d.y != GM_BLOCK_SIG_Y || d.z != GM_BLOCK_SIG_Z)
                return false;
            flags = d.w;
            return true;
        }

        private static Dictionary<NGuid, GmBlock> GetBoardGmBlocks(Board board)
        {
            var field = typeof(Board).GetField("_gmBlocks",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(board) as Dictionary<NGuid, GmBlock>;
        }

        private static NGuid MakeContent(uint flags) =>
            new NGuid(new uint4(GM_BLOCK_SIG_X, GM_BLOCK_SIG_Y, GM_BLOCK_SIG_Z, flags));

        private static bool TryGetAutoHideBlock(out AtmosphereBlock block, out uint flags)
        {
            block = null;
            flags = 0;
            var blocks = GetBoardGmBlocks(BoardSessionManager.Board);
            if (blocks == null) return false;
            foreach (var kv in blocks)
                if (IsAutoHideBlock(kv.Value, out block, out flags)) return true;
            block = null;
            return false;
        }

        // The block's presence is the board setting: every client that has the plugin reads it
        // straight off the synced data model, so no side-channel is needed to distribute it.
        private void PollModeGmBlock()
        {
            if (Time.time < _nextGmBlockPoll) return;
            _nextGmBlockPoll = Time.time + 0.5f;

            if (BoardSessionManager.Board == null) return;

            bool found = TryGetAutoHideBlock(out _, out var flags);
            _boardRememberOverride = found ? (bool?)((flags & GM_FLAG_REMEMBER) != 0) : null;

            if (!found)
            {
                _haveResetGeneration = false;
            }
            else
            {
                uint gen = (flags & GM_FLAG_RESET_GEN_MASK) >> GM_FLAG_RESET_GEN_SHIFT;
                if (!_haveResetGeneration)
                {
                    // First sighting only records where the counter stands — a client that
                    // joins later must not replay a reset the party already went through.
                    _haveResetGeneration = true;
                    _lastResetGeneration = gen;
                }
                else if (gen != _lastResetGeneration)
                {
                    _lastResetGeneration = gen;
                    Logger.LogInfo($"[AutoHide] [GmBlock] Reset counter now {gen} — forgetting seen terrain.");
                    ForgetSeenTerrain();
                }
            }

            // A GM in GM mode is meant to see everything, so the board setting only binds
            // clients that are actually playing.
            bool shouldTrack = found && (flags & GM_FLAG_LOS) != 0 && !LocalClient.IsInGmMode;
            if (shouldTrack == _boardDrivenTracking) return;

            _boardDrivenTracking = shouldTrack;
            Logger.LogInfo($"[AutoHide] [GmBlock] Board {(shouldTrack ? "requires" : "releases")} LoS mode.");
            SetLosTracking(shouldTrack, switchClientMode: false);
        }

        private void ForgetSeenTerrain()
        {
            if (!_losTrackingActive) return;
            RemoveAllZoneHideVolumes();
            ForceLosRefresh();
        }

        private void SetGmBlockFlags(Func<uint, uint> mutate, string what)
        {
            try
            {
                var board = BoardSessionManager.Board;
                if (board == null) { Logger.LogInfo("[AutoHide] [GmBlock] No board."); return; }
                if (!TryGetAutoHideBlock(out var block, out var flags))
                { Logger.LogInfo("[AutoHide] [GmBlock] No AutoHide block on this board."); return; }

                uint next = mutate(flags);
                board.SetGmBlockState(new AtmosphereBlock
                {
                    Id = block.Id,
                    Position = block.Position,
                    Content = MakeContent(next),
                    Data = block.Data,
                });
                Logger.LogInfo($"[AutoHide] [GmBlock] {what}: flags 0x{flags:X8} -> 0x{next:X8}");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [GmBlock] {ex}"); }
        }

        private void ToggleAutoHideGmBlock()
        {
            try
            {
                var board = BoardSessionManager.Board;
                if (board == null) { Logger.LogInfo("[AutoHide] [GmBlock] No board."); return; }

                var blocks = GetBoardGmBlocks(board);
                if (blocks == null) { Logger.LogWarning("[AutoHide] [GmBlock] Board._gmBlocks not found."); return; }

                foreach (var kv in new List<KeyValuePair<NGuid, GmBlock>>(blocks))
                {
                    if (!IsAutoHideBlock(kv.Value, out _, out _)) continue;
                    board.RemoveGmBlock(kv.Key);
                    Logger.LogInfo($"[AutoHide] [GmBlock] Removed existing AutoHide block {kv.Key}");
                    return;
                }

                if (!CreaturePresenter.TryGetAsset(LocalClient.SelectedCreatureId, out var asset) || asset == null)
                { Logger.LogInfo("[AutoHide] [GmBlock] No selected creature to place at."); return; }

                var pos = asset.transform.position;
                var block = new AtmosphereBlock
                {
                    Id = new NGuid(System.Guid.NewGuid()),
                    Position = new float3(pos.x, pos.y, pos.z),
                    Content = MakeContent(GM_FLAG_LOS | (_rememberSeenTerrain.Value ? GM_FLAG_REMEMBER : 0u)),
                    // Seed from the live atmosphere so triggering the block is a visual no-op.
                    Data = AtmosphereManager.Instance.GetAtmosphere(),
                };
                board.AddGmBlock(block);
                Logger.LogInfo($"[AutoHide] [GmBlock] Placed AutoHide block {block.Id} at {block.Position}");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [GmBlock] {ex}"); }
        }

        private void LogGmBlockCensus()
        {
            try
            {
                var board = BoardSessionManager.Board;
                if (board == null) { Logger.LogInfo("[AutoHide] [GmCensus] No board."); return; }

                var blocks = GetBoardGmBlocks(board);
                if (blocks == null) { Logger.LogWarning("[AutoHide] [GmCensus] Board._gmBlocks not found."); return; }

                Logger.LogInfo($"[AutoHide] [GmCensus] Board carries {blocks.Count} GM block(s)");
                foreach (var kv in blocks)
                {
                    bool ours = IsAutoHideBlock(kv.Value, out var atmos, out var flags);
                    string content = atmos != null ? atmos.Content.Data.ToString() : "n/a";
                    Logger.LogInfo($"[AutoHide] [GmCensus]   {kv.Value.GetType().Name} id={kv.Key} "
                        + $"pos={kv.Value.GetPosition()} ours={ours} flags=0x{flags:X8} content={content}");
                }

                LogGmBlockPresentation();
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [GmCensus] {ex}"); }
        }

        // Whether a GM block is visible to a player is decided by the Unity layer its presentation
        // object sits on versus the camera's culling mask — neither is visible in decompiled C#.
        private void LogGmBlockPresentation()
        {
            try
            {
                var mgr = GmBlockManager.Instance;
                var field = typeof(GmBlockManager).GetField("_blocksBases",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var bases = field?.GetValue(mgr) as System.Collections.IEnumerable;
                if (bases == null) { Logger.LogWarning("[AutoHide] [GmVis] _blocksBases not found."); return; }

                int n = 0;
                foreach (var b in bases)
                {
                    var comp = b as Component;
                    if (comp == null) continue;
                    var go = comp.gameObject;
                    Logger.LogInfo($"[AutoHide] [GmVis]   base[{n++}] name={go.name} active={go.activeInHierarchy} "
                        + $"layer={go.layer}({LayerMask.LayerToName(go.layer)}) pos={comp.transform.position}");
                    foreach (var r in comp.GetComponentsInChildren<Renderer>(true))
                        Logger.LogInfo($"[AutoHide] [GmVis]     renderer {r.name} enabled={r.enabled} "
                            + $"activeInHierarchy={r.gameObject.activeInHierarchy} layer={r.gameObject.layer}({LayerMask.LayerToName(r.gameObject.layer)})");
                }
                if (n == 0) Logger.LogInfo("[AutoHide] [GmVis]   no presentation objects");

                var cam = Camera.main;
                if (cam != null)
                    Logger.LogInfo($"[AutoHide] [GmVis] Camera.main={cam.name} cullingMask=0x{cam.cullingMask:X8}");
                foreach (var c in Camera.allCameras)
                    Logger.LogInfo($"[AutoHide] [GmVis]   camera {c.name} enabled={c.enabled} cullingMask=0x{c.cullingMask:X8}");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [GmVis] {ex}"); }
        }

        // Recovery for boards polluted by an earlier build of this plugin, which published its
        // LoS boxes through HideVolumeManager and so persisted them as board ops.
        private void PurgeBoardHideVolumes()
        {
            try
            {
                var board = BoardSessionManager.Board;
                if (board == null) { Logger.LogInfo("[AutoHide] [Purge] No board."); return; }

                var field = typeof(Board).GetField("_hideVolumes",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) { Logger.LogWarning("[AutoHide] [Purge] Board._hideVolumes not found."); return; }

                var volumes = (NativeList<HideVolume>)field.GetValue(board);
                var ids = new List<NGuid>(volumes.Length);
                for (int i = 0; i < volumes.Length; i++)
                {
                    var v = volumes[i];
                    ids.Add(v.Id);
                    Logger.LogInfo($"[AutoHide] [Purge] dump[{i}] id={v.Id} center={v.Bounds.center} size={v.Bounds.size} state(active={v.IsActive},placeables={v.HidePlaceables},creatures={v.HideCreature},lights={v.HideLights}) version={v.Version}");
                }

                foreach (var id in ids) board.RemoveHideVolume(id);
                Logger.LogInfo($"[AutoHide] [Purge] Removed {ids.Count} board hide volume(s). Bounds dumped above.");
            }
            catch (Exception ex) { Logger.LogWarning($"[AutoHide] [Purge] {ex}"); }
        }

        private void ProcessFogResults()
        {
            List<short3> dirty;
            lock (_zoneMasks)
            {
                if (_dirtyZones.Count == 0) return;
                dirty = new List<short3>(_dirtyZones);
                _dirtyZones.Clear();
            }

            var board = BoardSessionManager.Board;
            if (board == null) return;

            int boxTotal = 0;
            foreach (var coord in dirty)
            {
                if (!board.TryGetZone(coord, out var zone)) continue;

                lock (_zoneMasks)
                {
                    if (!_zoneMasks.TryGetValue(coord, out var buf)) continue;
                    Array.Copy(buf, _maskScratch, MASK_LENGTH);
                }

                boxTotal += RebuildZoneHideVolumes(coord, zone, _maskScratch);
            }

            if (_rebuildLogCount < 12)
            {
                _rebuildLogCount++;
                _log?.LogInfo($"[AutoHide] Rebuild: {dirty.Count} zone(s) → {boxTotal} hide box(es)");
            }
        }

        // Turns one zone's per-cell visibility mask into a small set of axis-aligned HideVolumes.
        // These go straight into the zone's local volume list rather than through
        // HideVolumeManager, whose Add/Remove push networked, board-persisted ops.
        private int RebuildZoneHideVolumes(short3 coord, Zone zone, ushort[] mask)
        {
            for (int y = 0; y < ZONE_CELLS; y++)
            {
                for (int z = 0; z < ZONE_CELLS; z++)
                {
                    ushort row = mask[MaskIndexYZ(y, z)];
                    for (int x = 0; x < ZONE_CELLS; x++)
                        _cellFogged[CellIndex(x, y, z)] = (row & (1 << x)) != 0;
                }
            }

            // A placeable is hidden if its bounds touch ANY volume, so a box that merely abuts a
            // visible cell would wrongly hide the tile standing in it. Eroding the fogged set by
            // one cell keeps the boxes clear of the visibility boundary.
            Array.Copy(_cellFogged, _cellScratch, _cellFogged.Length);
            for (int y = 0; y < ZONE_CELLS; y++)
                for (int z = 0; z < ZONE_CELLS; z++)
                    for (int x = 0; x < ZONE_CELLS; x++)
                        if (_cellScratch[CellIndex(x, y, z)] && HasVisibleNeighbour(x, y, z))
                            _cellFogged[CellIndex(x, y, z)] = false;

            var previous = _zoneHideVolumes.TryGetValue(coord, out var list) ? list : (_zoneHideVolumes[coord] = new List<HideVolume>());
            foreach (var old in previous)
            {
                try { zone.RemoveHideVolume(in old); } catch { }
            }
            previous.Clear();

            var origin = new Vector3(zone.Origin.x, zone.Origin.y, zone.Origin.z);

            for (int y = 0; y < ZONE_CELLS; y++)
                for (int z = 0; z < ZONE_CELLS; z++)
                    for (int x = 0; x < ZONE_CELLS; x++)
                    {
                        if (!_cellFogged[CellIndex(x, y, z)]) continue;

                        int w = 1;
                        while (x + w < ZONE_CELLS && _cellFogged[CellIndex(x + w, y, z)]) w++;

                        int d = 1;
                        while (z + d < ZONE_CELLS && IsRunFogged(x, w, y, z + d)) d++;

                        int h = 1;
                        while (y + h < ZONE_CELLS && IsSlabFogged(x, w, y + h, z, d)) h++;

                        for (int yy = y; yy < y + h; yy++)
                            for (int zz = z; zz < z + d; zz++)
                                for (int xx = x; xx < x + w; xx++)
                                    _cellFogged[CellIndex(xx, yy, zz)] = false;

                        var size = new Vector3(w, h, d);
                        var min = origin + new Vector3(x, y, z);
                        var bounds = new Bounds(min + size * 0.5f, size);

                        try
                        {
                            var hv = new HideVolume(NGuid.GenerateNewGuid(), bounds, HIDE_VOLUME_STATE, 1u);
                            zone.SetHideVolume(in hv, false);
                            previous.Add(hv);
                        }
                        catch (Exception ex)
                        {
                            _log?.LogWarning($"[AutoHide] SetHideVolume {coord} {bounds}: {ex.Message}");
                        }
                    }

            return previous.Count;
        }

        private static bool HasVisibleNeighbour(int x, int y, int z)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= ZONE_CELLS || ny >= ZONE_CELLS || nz >= ZONE_CELLS)
                            continue;
                        if (!_cellScratch[CellIndex(nx, ny, nz)]) return true;
                    }
            return false;
        }

        private static int CellIndex(int x, int y, int z) => (y * ZONE_CELLS + z) * ZONE_CELLS + x;

        private static bool IsRunFogged(int x, int w, int y, int z)
        {
            for (int i = 0; i < w; i++)
                if (!_cellFogged[CellIndex(x + i, y, z)]) return false;
            return true;
        }

        private static bool IsSlabFogged(int x, int w, int y, int z, int d)
        {
            for (int j = 0; j < d; j++)
                if (!IsRunFogged(x, w, y, z + j)) return false;
            return true;
        }

        private static void RemoveAllZoneHideVolumes()
        {
            var board = BoardSessionManager.Board;
            int removed = 0;
            foreach (var kv in _zoneHideVolumes)
            {
                if (board == null || !board.TryGetZone(kv.Key, out var zone)) continue;
                foreach (var hv in kv.Value)
                {
                    try { zone.RemoveHideVolume(in hv); removed++; } catch { }
                }
            }
            _zoneHideVolumes.Clear();
            lock (_zoneMasks)
            {
                _zoneMasks.Clear();
                _dirtyZones.Clear();
            }
            _log?.LogInfo($"[AutoHide] [LoS] Removed {removed} hide volumes.");
        }

    }
}
