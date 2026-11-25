// Version 4.0.1
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;
using System.Linq;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("NoSafeZones", "Yaruani", "4.0.1")]
    [Description("Safety First: Uses immutable backup system to prevent data loss accidents.")]
    class NoSafeZones : RustPlugin
    {
        private const string PermAdmin = "nosafezones.admin";
        
        private List<TriggerSafeZone> _disabledSafeZones = new List<TriggerSafeZone>();
        private List<NPCSpawner> _disabledSpawners = new List<NPCSpawner>();
        private readonly List<Collider> _disabledZoneColliders = new List<Collider>();
        private List<Collider> _zoneColliders = new List<Collider>();
        private Scene _zoneQueryScene;
        
        private StoredData _storedData;
        private bool _isEnabled = false;

        private class StoredData
        {
            public string MapSignature; // マップ識別用 (Seed + Size)
            public List<EntitySnapshot> BackupEntities = new List<EntitySnapshot>();
        }

        private class EntitySnapshot
        {
            public string PrefabPath;
            public Vector3 Position;
            public Quaternion Rotation;
            public ulong SkinID;
        }

        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty("Enable Plugin (Master Switch)")]
            public bool Enabled { get; set; } = true;

            [JsonProperty("Remove Safe Zone Triggers")]
            public bool RemoveSafeZoneTriggers { get; set; } = true;

            [JsonProperty("Remove Scientists and Guards")]
            public bool RemoveScientists { get; set; } = true;

            [JsonProperty("Remove Shopkeepers")]
            public bool RemoveShopkeepers { get; set; } = false;

            [JsonProperty("Remove Turrets and SAM Sites")]
            public bool RemoveTurrets { get; set; } = true;

            [JsonProperty("Force Restore Turrets")]
            public bool ForceRestore { get; set; } = true;

            [JsonProperty("Border Tolerance (Meters)")]
            public float BorderTolerance { get; set; } = 5.0f;

            [JsonProperty("Debug Mode")]
            public bool DebugMode { get; set; } = false;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) LoadDefaultConfig();
            }
            catch
            {
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        #endregion

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void LoadData()
        {
            try
            {
                _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("NoSafeZones_Backup");
            }
            catch
            {
                _storedData = new StoredData();
            }

            if (_storedData == null) _storedData = new StoredData();

            // 現在のマップ署名を生成
            string currentMapSignature = $"{ConVar.Server.seed}_{ConVar.Server.worldsize}";

            // マップが変わっていたら（ワイプされたら）、データをリセットして作り直す準備をする
            if (_storedData.MapSignature != currentMapSignature)
            {
                Puts($"[NoSafeZones] New map detected (Old: {_storedData.MapSignature}, New: {currentMapSignature}). Resetting backup data.");
                _storedData.MapSignature = currentMapSignature;
                _storedData.BackupEntities.Clear();
                SaveData();
            }
            else
            {
                Puts($"[NoSafeZones] Backup data loaded: {_storedData.BackupEntities.Count} entities protected.");
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("NoSafeZones_Backup", _storedData);
        }

        private void OnServerInitialized()
        {
            // バックアップがない場合のみ、初回スキャンを実行して保存する (Golden Copy作成)
            if (_storedData.BackupEntities.Count == 0)
            {
                CreateBackup();
            }

            if (_config.Enabled)
            {
                ApplyNoSafeZones();
            }
        }

        private void Unload()
        {
            RestoreSafeZones();
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (!_isEnabled) return;
            
            NextFrame(() => {
                if (!_isEnabled || entity == null || entity.IsDestroyed) return;
                var combatEntity = entity as BaseCombatEntity;
                if (combatEntity == null) return;

                if (IsTargetEntity(combatEntity) && IsInsideAnyZone(combatEntity.transform.position, _zoneColliders, _config.BorderTolerance))
                {
                    combatEntity.baseProtection = null;
                    combatEntity.Kill();
                }
            });
        }

        [ChatCommand("nsz")]
        private void CmdChatNoSafeZones(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin) && !player.IsAdmin) return;

            if (args != null && args.Length > 0)
            {
                string sub = args[0].ToLower();
                if (sub == "debug")
                {
                    _config.DebugMode = !_config.DebugMode;
                    SaveConfig();
                    player.ChatMessage($"Debug: {_config.DebugMode}");
                    return;
                }
                // 手動でバックアップを再作成するコマンド (緊急用)
                if (sub == "resetbackup")
                {
                    _storedData.BackupEntities.Clear();
                    SaveData();
                    CreateBackup();
                    player.ChatMessage($"[NoSafeZones] Backup rebuilt manually. Found {_storedData.BackupEntities.Count} entities.");
                    return;
                }
            }

            if (_isEnabled)
            {
                RestoreSafeZones();
                player.ChatMessage("[NoSafeZones] DISABLED (Restored).");
            }
            else
            {
                ApplyNoSafeZones();
                player.ChatMessage("[NoSafeZones] ENABLED (Applied).");
            }
            SaveConfig();
        }

        // 初回のみ実行されるバックアップ作成処理
        private void CreateBackup()
        {
            Puts("[NoSafeZones] Creating Initial Backup of Safe Zone Turrets...");
            
            var zoneColliders = _isEnabled ? _zoneColliders : GetZoneColliders();
            var allEntities = BaseNetworkable.serverEntities.OfType<BaseCombatEntity>().ToList();
            int count = 0;

            foreach (var entity in allEntities)
            {
                if (entity == null || entity.IsDestroyed) continue;
                if (!IsTargetEntity(entity)) continue;
                if (!IsInsideAnyZone(entity.transform.position, zoneColliders, _config.BorderTolerance)) continue;

                // タレットのみ保存
                if (IsTurret(entity))
                {
                    string path = entity.PrefabName; 
                    if (!string.IsNullOrEmpty(path))
                    {
                        // 重複チェック
                        if (!_storedData.BackupEntities.Any(x => Vector3.Distance(x.Position, entity.transform.position) < 1.0f))
                        {
                            _storedData.BackupEntities.Add(new EntitySnapshot
                            {
                                PrefabPath = path,
                                Position = entity.transform.position,
                                Rotation = entity.transform.rotation,
                                SkinID = entity.skinID
                            });
                            count++;
                        }
                    }
                }
            }

            SaveData();
            Puts($"[NoSafeZones] Backup created successfully. Protected {count} turrets.");
        }

        private void ApplyNoSafeZones()
        {
            if (_isEnabled) return;
            _isEnabled = true;

            _disabledSafeZones.Clear();
            _disabledSpawners.Clear();
            
            int removedNpcs = 0;
            int removedTurrets = 0;
            int disabledSpawners = 0;

            _zoneColliders = GetZoneColliders();

            // 1. Spawners
            if (_config.RemoveScientists)
            {
                var allSpawners = UnityEngine.Object.FindObjectsOfType<NPCSpawner>();
                foreach (var spawner in allSpawners)
                {
                    if (spawner == null || !spawner.enabled) continue;
                    
                    if (IsInsideAnyZone(spawner.transform.position, _zoneColliders, _config.BorderTolerance))
                    {
                        spawner.enabled = false;
                        spawner.CancelInvoke("Spawn");
                        _disabledSpawners.Add(spawner);
                        disabledSpawners++;
                    }
                }
            }

            // 2. Entities Remove
            if (_config.RemoveScientists || _config.RemoveTurrets)
            {
                var allEntities = BaseNetworkable.serverEntities.OfType<BaseCombatEntity>().ToList();
                
                foreach (var entity in allEntities)
                {
                    if (entity == null || entity.IsDestroyed) continue;
                    if (!IsTargetEntity(entity)) continue;
                    if (!IsInsideAnyZone(entity.transform.position, _zoneColliders, _config.BorderTolerance)) continue;

                    entity.baseProtection = null;
                    entity.Kill();
                    
                    if (entity is NPCPlayer) removedNpcs++;
                    else removedTurrets++;
                }
            }

            // 3. Disable Zones
            if (_config.RemoveSafeZoneTriggers)
            {
                var triggers = UnityEngine.Object.FindObjectsOfType<TriggerSafeZone>();
                foreach (var zone in triggers)
                {
                    if (zone != null && zone.enabled)
                    {
                        zone.enabled = false;
                        var col = zone.GetComponent<Collider>();
                        if (col != null && col.enabled)
                        {
                            if (_zoneColliders.Remove(col))
                            {
                                var queryCollider = CopyZoneCollider(col);
                                if (queryCollider != null) _zoneColliders.Add(queryCollider);
                            }
                            col.enabled = false;
                            _disabledZoneColliders.Add(col);
                        }
                        _disabledSafeZones.Add(zone);
                    }
                }
            }

            Puts($"[NoSafeZones] Applied. Spawners: {disabledSpawners}, NPCs: {removedNpcs}, Turrets Removed: {removedTurrets}");
        }

        private void RestoreSafeZones()
        {
            if (!_isEnabled) return;
            _isEnabled = false;

            // Spawners
            foreach (var spawner in _disabledSpawners)
            {
                if (spawner != null) { spawner.enabled = true; spawner.Spawn(); }
            }

            // Restore from Backup
            int restoredCount = 0;
            int forceFixed = 0;
            int occupiedCount = 0;

            if (_storedData != null && _storedData.BackupEntities.Count > 0)
            {
                Puts($"[NoSafeZones] Restoring {_storedData.BackupEntities.Count} entities from BACKUP...");
                var allEntities = BaseNetworkable.serverEntities.OfType<BaseCombatEntity>().ToList();

                foreach (var snapshot in _storedData.BackupEntities)
                {
                    if (string.IsNullOrEmpty(snapshot.PrefabPath)) continue;

                    var nearby = allEntities.Where(e => e != null && !e.IsDestroyed &&
                        (e.transform.position - snapshot.Position).sqrMagnitude < 1f).Take(2).ToList();
                    var existing = nearby.Count == 1 ? nearby[0] : null;
                    if (nearby.Count > 1 || (existing != null && (existing.OwnerID != 0 ||
                        !IsTurret(existing) || existing.PrefabName != snapshot.PrefabPath)))
                    {
                        occupiedCount++;
                        continue;
                    }
                    if (existing != null && !_config.ForceRestore) continue;

                    var entity = GameManager.server.CreateEntity(snapshot.PrefabPath, snapshot.Position, snapshot.Rotation);
                    if (entity == null)
                    {
                        string shortName = System.IO.Path.GetFileNameWithoutExtension(snapshot.PrefabPath);
                        string fixPath = FindCorrectPrefab(shortName);
                        if (!string.IsNullOrEmpty(fixPath))
                            entity = GameManager.server.CreateEntity(fixPath, snapshot.Position, snapshot.Rotation);
                    }
                    if (entity == null) continue;

                    entity.skinID = snapshot.SkinID;
                    entity.Spawn();
                    if (entity == null || entity.IsDestroyed) continue;

                    if (existing != null && !existing.IsDestroyed)
                    {
                        existing.Kill();
                        forceFixed++;
                    }
                    if (entity is BaseCombatEntity combatEntity) allEntities.Add(combatEntity);
                    restoredCount++;

                    if (entity is AutoTurret turret)
                    {
                        turret.SetFlag(BaseEntity.Flags.Reserved8, true);
                        turret.InitiateStartup();
                    }
                    entity.SendNetworkUpdateImmediate();
                }
            }
            
            // Zones
            foreach (var collider in _disabledZoneColliders)
                if (collider != null) collider.enabled = true;
            foreach (var zone in _disabledSafeZones)
                if (zone != null) zone.enabled = true;

            _disabledSafeZones.Clear();
            _disabledSpawners.Clear();
            _disabledZoneColliders.Clear();
            _zoneColliders.Clear();
            if (_zoneQueryScene.IsValid())
            {
                SceneManager.UnloadSceneAsync(_zoneQueryScene);
                _zoneQueryScene = default;
            }

            Puts($"[NoSafeZones] Restored. Success: {restoredCount}, Force Fixed: {forceFixed}, Occupied: {occupiedCount}.");
        }

        // --- Helpers ---

        private List<Collider> GetZoneColliders()
        {
            var colliders = new List<Collider>();
            var zones = UnityEngine.Object.FindObjectsOfType<TriggerSafeZone>();
            foreach (var z in zones)
            {
                if (z != null)
                {
                    var col = z.GetComponent<Collider>();
                    if (col != null && col.enabled) colliders.Add(col);
                }
            }
            return colliders;
        }

        private Collider CopyZoneCollider(Collider source)
        {
            // A disabled collider cannot answer distance queries. Keep its shape in a separate physics scene.
            if (!_zoneQueryScene.IsValid())
            {
                _zoneQueryScene = SceneManager.CreateScene("NoSafeZones " + System.Guid.NewGuid().ToString("N"),
                    new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            }

            var geometry = new GameObject("Safe zone geometry");
            SceneManager.MoveGameObjectToScene(geometry, _zoneQueryScene);
            geometry.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            geometry.transform.localScale = source.transform.lossyScale;

            switch (source)
            {
                case BoxCollider box:
                    var boxCopy = geometry.AddComponent<BoxCollider>();
                    boxCopy.center = box.center;
                    boxCopy.size = box.size;
                    return boxCopy;
                case SphereCollider sphere:
                    var sphereCopy = geometry.AddComponent<SphereCollider>();
                    sphereCopy.center = sphere.center;
                    sphereCopy.radius = sphere.radius;
                    return sphereCopy;
                case CapsuleCollider capsule:
                    var capsuleCopy = geometry.AddComponent<CapsuleCollider>();
                    capsuleCopy.center = capsule.center;
                    capsuleCopy.radius = capsule.radius;
                    capsuleCopy.height = capsule.height;
                    capsuleCopy.direction = capsule.direction;
                    return capsuleCopy;
                case MeshCollider mesh when mesh.convex:
                    var meshCopy = geometry.AddComponent<MeshCollider>();
                    meshCopy.convex = true;
                    meshCopy.cookingOptions = mesh.cookingOptions;
                    meshCopy.sharedMesh = mesh.sharedMesh;
                    return meshCopy;
                default:
                    UnityEngine.Object.Destroy(geometry);
                    return null;
            }
        }

        private static bool IsInsideAnyZone(Vector3 pos, List<Collider> colliders, float tolerance)
        {
            foreach (var collider in colliders)
            {
                if (collider == null || !collider.enabled) continue;
                var bounds = collider.bounds;
                bounds.Expand(tolerance * 2f);
                if (bounds.Contains(pos) && (pos - collider.ClosestPoint(pos)).sqrMagnitude <= tolerance * tolerance)
                    return true;
            }
            return false;
        }

        private bool IsTurret(BaseCombatEntity entity)
        {
            if (entity is AutoTurret || entity is FlameTurret || entity is GunTrap || entity is SamSite) return true;
            if (entity.ShortPrefabName.Contains("sentry")) return true;
            return false;
        }

        private bool IsTargetEntity(BaseCombatEntity entity)
        {
            if (entity == null || entity.IsDestroyed) return false;
            if (entity is BasePlayer player && !player.IsNpc) return false;
            if (entity is BaseVehicle) return false;

            // Player Owned Turrets Protection
            if (IsTurret(entity) && entity.OwnerID != 0) return false;

            string name = entity.ShortPrefabName;
            
            bool isTargetName = name.Contains("peacekeeper") || 
                                name.Contains("bandit_guard") || 
                                name.Contains("sentry");

            bool isProtected = name.Contains("shopkeeper") || 
                               name.Contains("banker") || 
                               name.Contains("missionprovider") || 
                               name.Contains("conversationalist") || 
                               name.Contains("marketplace");

            if (entity is NPCPlayer && _config.RemoveScientists)
            {
                if (isTargetName && !isProtected)
                {
                    if (isProtected && !_config.RemoveShopkeepers) return false;
                    return true;
                }
            }

            if (IsTurret(entity) && _config.RemoveTurrets) return true;

            return false;
        }

        private string FindCorrectPrefab(string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return null;
            foreach (var str in GameManifest.Current.pooledStrings)
            {
                if (str.str.EndsWith($"/{shortName}.prefab")) return str.str;
            }
            return null;
        }

        private void LogDebug(string message)
        {
            if (_config.DebugMode) Puts($"[Debug] {message}");
        }
    }
}
