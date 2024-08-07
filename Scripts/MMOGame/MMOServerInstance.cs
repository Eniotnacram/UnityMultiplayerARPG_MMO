﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.IO;
using LiteNetLibManager;
using UnityEngine;
using Newtonsoft.Json;
using UnityEngine.Serialization;
using Cysharp.Threading.Tasks;

namespace MultiplayerARPG.MMO
{
    [RequireComponent(typeof(LogGUI))]
    [DefaultExecutionOrder(DefaultExecutionOrders.MMO_SERVER_INSTANCE)]
    public class MMOServerInstance : MonoBehaviour
    {
        public static MMOServerInstance Singleton { get; protected set; }
        public static Dictionary<string, object> Configs { get; protected set; } = new Dictionary<string, object>();

        [Header("Server Components")]
        [SerializeField]
        private CentralNetworkManager centralNetworkManager = null;
        [SerializeField]
        private MapSpawnNetworkManager mapSpawnNetworkManager = null;
        [SerializeField]
        private MapNetworkManager mapNetworkManager = null;
        [SerializeField]
        private DatabaseNetworkManager databaseNetworkManager = null;
        [SerializeField]
        [Tooltip("Use custom database client or not, if yes, it won't use `databaseNetworkManager` for network management")]
        private bool useCustomDatabaseClient = false;
        [SerializeField]
        [Tooltip("Which game object has a custom database client attached")]
        private GameObject customDatabaseClientSource = null;

        [Header("Settings")]
        [SerializeField]
        private bool useWebSocket = false;
        [SerializeField]
        private bool webSocketSecure = false;
        [SerializeField]
        private string webSocketCertPath = string.Empty;
        [SerializeField]
        private string webSocketCertPassword = string.Empty;

        public CentralNetworkManager CentralNetworkManager
        {
            get
            {
                if (centralNetworkManager == null)
                    centralNetworkManager = GetComponentInChildren<CentralNetworkManager>();
                if (centralNetworkManager == null)
                    centralNetworkManager = FindObjectOfType<CentralNetworkManager>();
                return centralNetworkManager;
            }
        }
        public MapSpawnNetworkManager MapSpawnNetworkManager
        {
            get
            {
                if (mapSpawnNetworkManager == null)
                    mapSpawnNetworkManager = GetComponentInChildren<MapSpawnNetworkManager>();
                if (mapSpawnNetworkManager == null)
                    mapSpawnNetworkManager = FindObjectOfType<MapSpawnNetworkManager>();
                return mapSpawnNetworkManager;
            }
        }
        public MapNetworkManager MapNetworkManager
        {
            get
            {
                if (mapNetworkManager == null)
                    mapNetworkManager = GetComponentInChildren<MapNetworkManager>();
                if (mapNetworkManager == null)
                    mapNetworkManager = FindObjectOfType<MapNetworkManager>();
                return mapNetworkManager;
            }
        }
        public IDatabaseClient DatabaseClient
        {
            get
            {
                if (!useCustomDatabaseClient)
                    return databaseNetworkManager;
                else
                    return _customDatabaseClient;
            }
        }
        public IChatProfanityDetector ChatProfanityDetector { get; private set; }
        public bool UseWebSocket { get { return useWebSocket; } }
        public bool WebSocketSecure { get { return webSocketSecure; } }
        public string WebSocketCertificateFilePath { get { return webSocketCertPath; } }
        public string WebSocketCertificatePassword { get { return webSocketCertPassword; } }

        private LogGUI _cacheLogGUI;
        public LogGUI CacheLogGUI
        {
            get
            {
                if (_cacheLogGUI == null)
                    _cacheLogGUI = GetComponent<LogGUI>();
                return _cacheLogGUI;
            }
        }

        public string LogFileName { get; set; }

        [Header("Running In Editor")]
        public bool startCentralOnAwake;
        public bool startMapSpawnOnAwake;
        public bool startDatabaseOnAwake;
        public bool startMapOnAwake;
        public BaseMapInfo startingMap;
        public int databaseOptionIndex;
        [FormerlySerializedAs("databaseDisableCacheReading")]
        public bool disableDatabaseCaching;

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private List<string> _spawningMaps;
        private List<SpawnAllocateMapByNameData> _spawningAllocateMaps;
        private string _startingMapId;
        private bool _startingCentralServer;
        private bool _startingMapSpawnServer;
        private bool _startingMapServer;
        private bool _startingDatabaseServer;
#endif
        private IDatabaseClient _customDatabaseClient;

        private void Awake()
        {
            if (Singleton != null)
            {
                Destroy(gameObject);
                return;
            }
            DontDestroyOnLoad(gameObject);
            Singleton = this;
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            Application.wantsToQuit += Application_wantsToQuit;
            GameInstance.OnGameDataLoadedEvent += OnGameDataLoaded;
#endif

            // Always accept SSL
            ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback((sender, certificate, chain, policyErrors) => { return true; });

            // Setup custom database client
            if (customDatabaseClientSource == null)
                customDatabaseClientSource = gameObject;
            _customDatabaseClient = customDatabaseClientSource.GetComponent<IDatabaseClient>();
            ChatProfanityDetector = GetComponentInChildren<IChatProfanityDetector>();
            if (ChatProfanityDetector == null)
                ChatProfanityDetector = gameObject.AddComponent<DisabledChatProfanityDetector>();

            CacheLogGUI.enabled = false;
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!Application.isEditor)
            {
                // Json file read
                bool configFileFound = false;
                string configFolder = "./Config";
                string configFilePath = configFolder + "/serverConfig.json";
                Configs.Clear();
                Logging.Log(ToString(), "Reading config file from " + configFilePath);
                if (File.Exists(configFilePath))
                {
                    // Read config file
                    Logging.Log(ToString(), "Found config file");
                    string dataAsJson = File.ReadAllText(configFilePath);
                    Configs = JsonConvert.DeserializeObject<Dictionary<string, object>>(dataAsJson);
                    configFileFound = true;
                }

                // Prepare data
                string[] args = Environment.GetCommandLineArgs();

                // Android fix
                if (args == null)
                    args = new string[0];

                // Database option index
                bool useCustomDatabaseClient = this.useCustomDatabaseClient = false;
                if (_customDatabaseClient != null && _customDatabaseClient as UnityEngine.Object != null)
                {
                    if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_USE_CUSTOM_DATABASE_CLIENT, out useCustomDatabaseClient, this.useCustomDatabaseClient))
                    {
                        this.useCustomDatabaseClient = useCustomDatabaseClient;
                    }
                    else if (ConfigReader.IsArgsProvided(args, ProcessArguments.CONFIG_USE_CUSTOM_DATABASE_CLIENT))
                    {
                        this.useCustomDatabaseClient = true;
                    }
                }
                Configs[ProcessArguments.CONFIG_USE_CUSTOM_DATABASE_CLIENT] = useCustomDatabaseClient;

                int dbOptionIndex;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_OPTION_INDEX, out dbOptionIndex, 0) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_DATABASE_OPTION_INDEX, out dbOptionIndex, 0))
                {
                    if (!useCustomDatabaseClient)
                        databaseNetworkManager.SetDatabaseByOptionIndex(dbOptionIndex);
                }
                Configs[ProcessArguments.CONFIG_DATABASE_OPTION_INDEX] = dbOptionIndex;

                // Database disable cache reading or not?
                bool disableDatabaseCaching = this.disableDatabaseCaching = false;
                // Old config key
#pragma warning disable CS0618 // Type or member is obsolete
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_DATABASE_DISABLE_CACHE_READING, out disableDatabaseCaching, this.disableDatabaseCaching))
                {
                    this.disableDatabaseCaching = disableDatabaseCaching;
                }
                else if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_DATABASE_DISABLE_CACHE_READING))
                {
                    this.disableDatabaseCaching = true;
                }
#pragma warning restore CS0618 // Type or member is obsolete
                // New config key
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_DISABLE_DATABASE_CACHING, out disableDatabaseCaching, this.disableDatabaseCaching))
                {
                    this.disableDatabaseCaching = disableDatabaseCaching;
                }
                else if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_DISABLE_DATABASE_CACHING))
                {
                    this.disableDatabaseCaching = true;
                }
                Configs[ProcessArguments.CONFIG_DISABLE_DATABASE_CACHING] = disableDatabaseCaching;

                // Use Websocket or not?
                bool useWebSocket = this.useWebSocket = false;
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_USE_WEB_SOCKET, out useWebSocket, this.useWebSocket))
                {
                    this.useWebSocket = useWebSocket;
                }
                else if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_USE_WEB_SOCKET))
                {
                    this.useWebSocket = true;
                }
                Configs[ProcessArguments.CONFIG_USE_WEB_SOCKET] = useWebSocket;

                // Is websocket running in secure mode or not?
                bool webSocketSecure = this.webSocketSecure = false;
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_WEB_SOCKET_SECURE, out webSocketSecure, this.webSocketSecure))
                {
                    this.webSocketSecure = webSocketSecure;
                }
                else if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_WEB_SOCKET_SECURE))
                {
                    this.webSocketSecure = true;
                }
                Configs[ProcessArguments.CONFIG_WEB_SOCKET_SECURE] = webSocketSecure;

                // Where is the certification file path?
                string webSocketCertPath;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_WEB_SOCKET_CERT_PATH, out webSocketCertPath, this.webSocketCertPath) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_WEB_SOCKET_CERT_PATH, out webSocketCertPath, this.webSocketCertPath))
                {
                    this.webSocketCertPath = webSocketCertPath;
                }
                Configs[ProcessArguments.CONFIG_WEB_SOCKET_CERT_PATH] = webSocketCertPath;

                // What is the certification password?
                string webSocketCertPassword;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_WEB_SOCKET_CERT_PASSWORD, out webSocketCertPassword, this.webSocketCertPassword) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_WEB_SOCKET_CERT_PASSWORD, out webSocketCertPassword, this.webSocketCertPassword))
                {
                    this.webSocketCertPassword = webSocketCertPassword;
                }
                Configs[ProcessArguments.CONFIG_WEB_SOCKET_CERT_PASSWORD] = webSocketCertPassword;

                // Central network address
                string centralNetworkAddress;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CENTRAL_ADDRESS, out centralNetworkAddress, MapNetworkManager.clusterServerAddress) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CENTRAL_ADDRESS, out centralNetworkAddress, MapNetworkManager.clusterServerAddress))
                {
                    MapNetworkManager.clusterServerAddress = centralNetworkAddress;
                }
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CENTRAL_ADDRESS, out centralNetworkAddress, MapSpawnNetworkManager.clusterServerAddress) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CENTRAL_ADDRESS, out centralNetworkAddress, MapSpawnNetworkManager.clusterServerAddress))
                {
                    MapSpawnNetworkManager.clusterServerAddress = centralNetworkAddress;
                }
                Configs[ProcessArguments.CONFIG_CENTRAL_ADDRESS] = centralNetworkAddress;

                // Central network port
                int centralNetworkPort;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CENTRAL_PORT, out centralNetworkPort, CentralNetworkManager.networkPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CENTRAL_PORT, out centralNetworkPort, CentralNetworkManager.networkPort))
                {
                    CentralNetworkManager.networkPort = centralNetworkPort;
                }
                Configs[ProcessArguments.CONFIG_CENTRAL_PORT] = centralNetworkPort;

                // Central max connections
                int centralMaxConnections;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CENTRAL_MAX_CONNECTIONS, out centralMaxConnections, CentralNetworkManager.maxConnections) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CENTRAL_MAX_CONNECTIONS, out centralMaxConnections, CentralNetworkManager.maxConnections))
                {
                    CentralNetworkManager.maxConnections = centralMaxConnections;
                }
                Configs[ProcessArguments.CONFIG_CENTRAL_MAX_CONNECTIONS] = centralMaxConnections;

                // Central map spawn timeout (milliseconds)
                int mapSpawnMillisecondsTimeout;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_SPAWN_MILLISECONDS_TIMEOUT, out mapSpawnMillisecondsTimeout, CentralNetworkManager.mapSpawnMillisecondsTimeout) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_MAP_SPAWN_MILLISECONDS_TIMEOUT, out mapSpawnMillisecondsTimeout, CentralNetworkManager.mapSpawnMillisecondsTimeout))
                {
                    CentralNetworkManager.mapSpawnMillisecondsTimeout = mapSpawnMillisecondsTimeout;
                }
                Configs[ProcessArguments.CONFIG_MAP_SPAWN_MILLISECONDS_TIMEOUT] = mapSpawnMillisecondsTimeout;

                // Central - default channels max connections
                int defaultChannelMaxConnections;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DEFAULT_CHANNEL_MAX_CONNECTIONS, out defaultChannelMaxConnections, CentralNetworkManager.defaultChannelMaxConnections) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_DEFAULT_CHANNEL_MAX_CONNECTIONS, out defaultChannelMaxConnections, CentralNetworkManager.defaultChannelMaxConnections))
                {
                    CentralNetworkManager.defaultChannelMaxConnections = defaultChannelMaxConnections;
                }
                Configs[ProcessArguments.CONFIG_DEFAULT_CHANNEL_MAX_CONNECTIONS] = defaultChannelMaxConnections;

                // Central - channels
                List<ChannelData> channels;
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CHANNELS, out channels, CentralNetworkManager.channels))
                {
                    CentralNetworkManager.channels = channels;
                }
                Configs[ProcessArguments.CONFIG_CHANNELS] = channels;

                // Central->Cluster network port
                int clusterNetworkPort;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CLUSTER_PORT, out clusterNetworkPort, MapNetworkManager.clusterServerPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CLUSTER_PORT, out clusterNetworkPort, MapNetworkManager.clusterServerPort))
                {
                    MapNetworkManager.clusterServerPort = clusterNetworkPort;
                }
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CLUSTER_PORT, out clusterNetworkPort, MapSpawnNetworkManager.clusterServerPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CLUSTER_PORT, out clusterNetworkPort, MapSpawnNetworkManager.clusterServerPort))
                {
                    MapSpawnNetworkManager.clusterServerPort = clusterNetworkPort;
                }
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CLUSTER_PORT, out clusterNetworkPort, CentralNetworkManager.clusterServerPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_CLUSTER_PORT, out clusterNetworkPort, CentralNetworkManager.clusterServerPort))
                {
                    CentralNetworkManager.clusterServerPort = clusterNetworkPort;
                }
                Configs[ProcessArguments.CONFIG_CLUSTER_PORT] = clusterNetworkPort;

                // Machine network address, will be set to map spawn / map / chat
                string machineNetworkAddress;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MACHINE_ADDRESS, out machineNetworkAddress, MapSpawnNetworkManager.machineAddress) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_MACHINE_ADDRESS, out machineNetworkAddress, MapSpawnNetworkManager.machineAddress))
                {
                    MapSpawnNetworkManager.machineAddress = machineNetworkAddress;
                }
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MACHINE_ADDRESS, out machineNetworkAddress, MapNetworkManager.machineAddress) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_MACHINE_ADDRESS, out machineNetworkAddress, MapNetworkManager.machineAddress))
                {
                    MapNetworkManager.machineAddress = machineNetworkAddress;
                }
                Configs[ProcessArguments.CONFIG_MACHINE_ADDRESS] = machineNetworkAddress;

                // Map spawn network port
                int mapSpawnNetworkPort;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_SPAWN_PORT, out mapSpawnNetworkPort, MapSpawnNetworkManager.networkPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_MAP_SPAWN_PORT, out mapSpawnNetworkPort, MapSpawnNetworkManager.networkPort))
                {
                    MapSpawnNetworkManager.networkPort = mapSpawnNetworkPort;
                }
                Configs[ProcessArguments.CONFIG_MAP_SPAWN_PORT] = mapSpawnNetworkPort;

                // Map spawn exe path
                string spawnExePath;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_EXE_PATH, out spawnExePath, MapSpawnNetworkManager.exePath) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_SPAWN_EXE_PATH, out spawnExePath, MapSpawnNetworkManager.exePath))
                {
                    MapSpawnNetworkManager.exePath = spawnExePath;
                }
                if (!File.Exists(spawnExePath))
                {
                    spawnExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    MapSpawnNetworkManager.exePath = spawnExePath;
                }
                Configs[ProcessArguments.CONFIG_SPAWN_EXE_PATH] = spawnExePath;

                // Map spawn in batch mode
                bool notSpawnInBatchMode = MapSpawnNetworkManager.notSpawnInBatchMode = false;
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_NOT_SPAWN_IN_BATCH_MODE, out notSpawnInBatchMode, MapSpawnNetworkManager.notSpawnInBatchMode))
                {
                    MapSpawnNetworkManager.notSpawnInBatchMode = notSpawnInBatchMode;
                }
                else if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_NOT_SPAWN_IN_BATCH_MODE))
                {
                    MapSpawnNetworkManager.notSpawnInBatchMode = true;
                }
                Configs[ProcessArguments.CONFIG_NOT_SPAWN_IN_BATCH_MODE] = notSpawnInBatchMode;

                // Map spawn start port
                int spawnStartPort;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_START_PORT, out spawnStartPort, MapSpawnNetworkManager.startPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_SPAWN_START_PORT, out spawnStartPort, MapSpawnNetworkManager.startPort))
                {
                    MapSpawnNetworkManager.startPort = spawnStartPort;
                }
                Configs[ProcessArguments.CONFIG_SPAWN_START_PORT] = spawnStartPort;

                // Spawn channels
                List<string> spawnChannels;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_CHANNELS, out spawnChannels, MapSpawnNetworkManager.spawningChannelIds) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_SPAWN_CHANNELS, out spawnChannels, MapSpawnNetworkManager.spawningChannelIds))
                {
                    MapSpawnNetworkManager.spawningChannelIds = spawnChannels;
                }
                Configs[ProcessArguments.CONFIG_SPAWN_CHANNELS] = spawnChannels;

                // Spawn maps
                List<string> defaultSpawnMaps = new List<string>();
                foreach (BaseMapInfo mapInfo in MapSpawnNetworkManager.spawningMaps)
                {
                    if (mapInfo != null)
                        defaultSpawnMaps.Add(mapInfo.Id);
                }
                List<string> spawnMaps;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_SPAWN_MAPS, out spawnMaps, defaultSpawnMaps) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_SPAWN_MAPS, out spawnMaps, defaultSpawnMaps))
                {
                    _spawningMaps = spawnMaps;
                }
                Configs[ProcessArguments.CONFIG_SPAWN_MAPS] = spawnMaps;

                // Spawn allocate maps
                List<SpawnAllocateMapByNameData> defaultSpawnAllocateMaps = new List<SpawnAllocateMapByNameData>();
                foreach (SpawnAllocateMapData spawnAllocateMap in MapSpawnNetworkManager.spawningAllocateMaps)
                {
                    if (spawnAllocateMap.mapInfo != null)
                    {
                        defaultSpawnAllocateMaps.Add(new SpawnAllocateMapByNameData()
                        {
                            mapName = spawnAllocateMap.mapInfo.Id,
                            allocateAmount = spawnAllocateMap.allocateAmount,
                        });
                    }
                }
                List<SpawnAllocateMapByNameData> spawnAllocateMaps;
                if (ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_SPAWN_ALLOCATE_MAPS, out spawnAllocateMaps, defaultSpawnAllocateMaps))
                {
                    _spawningAllocateMaps = spawnAllocateMaps;
                }
                Configs[ProcessArguments.CONFIG_SPAWN_ALLOCATE_MAPS] = spawnAllocateMaps;

                // Map network port
                int mapNetworkPort;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_PORT, out mapNetworkPort, MapNetworkManager.networkPort) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_MAP_PORT, out mapNetworkPort, MapNetworkManager.networkPort))
                {
                    MapNetworkManager.networkPort = mapNetworkPort;
                }
                Configs[ProcessArguments.CONFIG_MAP_PORT] = mapNetworkPort;

                // Map max connections
                int mapMaxConnections;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_MAX_CONNECTIONS, out mapMaxConnections, MapNetworkManager.maxConnections) ||
                    ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_MAP_MAX_CONNECTIONS, out mapMaxConnections, MapNetworkManager.maxConnections))
                {
                    MapNetworkManager.maxConnections = mapMaxConnections;
                }
                Configs[ProcessArguments.CONFIG_MAP_MAX_CONNECTIONS] = mapMaxConnections;

                // Map scene name
                string mapName = string.Empty;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_MAP_NAME, out mapName, string.Empty))
                {
                    _startingMapId = mapName;
                }

                // Channel Id
                string channelId = string.Empty;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_CHANNEL_ID, out channelId, string.Empty))
                {
                    MapNetworkManager.ChannelId = channelId;
                }

                // Instance Id
                string instanceId = string.Empty;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_ID, out instanceId, string.Empty))
                {
                    MapNetworkManager.MapInstanceId = instanceId;
                }

                // Instance Warp Position
                float instancePosX, instancePosY, instancePosZ;
                if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_POSITION_X, out instancePosX, 0f) &&
                    ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_POSITION_Y, out instancePosY, 0f) &&
                    ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_POSITION_Z, out instancePosZ, 0f))
                {
                    MapNetworkManager.MapInstanceWarpToPosition = new Vector3(instancePosX, instancePosY, instancePosZ);
                }

                // Instance Warp Override Rotation, Instance Warp Rotation
                MapNetworkManager.MapInstanceWarpOverrideRotation = ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_INSTANCE_OVERRIDE_ROTATION);
                float instanceRotX, instanceRotY, instanceRotZ;
                if (MapNetworkManager.MapInstanceWarpOverrideRotation &&
                    ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_ROTATION_X, out instanceRotX, 0f) &&
                    ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_ROTATION_Y, out instanceRotY, 0f) &&
                    ConfigReader.ReadArgs(args, ProcessArguments.ARG_INSTANCE_ROTATION_Z, out instanceRotZ, 0f))
                {
                    MapNetworkManager.MapInstanceWarpToRotation = new Vector3(instanceRotX, instanceRotY, instanceRotZ);
                }

                // Allocate map server
                bool isAllocate = false;
                if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_ALLOCATE))
                {
                    MapNetworkManager.IsAllocate = true;
                    isAllocate = true;
                }

                if (!useCustomDatabaseClient)
                {
                    // Database network address
                    string databaseNetworkAddress;
                    if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_ADDRESS, out databaseNetworkAddress, databaseNetworkManager.networkAddress) ||
                        ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_DATABASE_ADDRESS, out databaseNetworkAddress, databaseNetworkManager.networkAddress))
                    {
                        databaseNetworkManager.networkAddress = databaseNetworkAddress;
                    }
                    Configs[ProcessArguments.CONFIG_DATABASE_ADDRESS] = databaseNetworkAddress;

                    // Database network port
                    int databaseNetworkPort;
                    if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_DATABASE_PORT, out databaseNetworkPort, databaseNetworkManager.networkPort) ||
                        ConfigReader.ReadConfigs(Configs, ProcessArguments.CONFIG_DATABASE_PORT, out databaseNetworkPort, databaseNetworkManager.networkPort))
                    {
                        databaseNetworkManager.networkPort = databaseNetworkPort;
                    }
                    Configs[ProcessArguments.CONFIG_DATABASE_PORT] = databaseNetworkPort;
                }

                LogFileName = "Log";
                bool startLog = false;

                if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_DATABASE_SERVER))
                {
                    if (!string.IsNullOrEmpty(LogFileName))
                        LogFileName += "_";
                    LogFileName += "Database";
                    startLog = true;
                    _startingDatabaseServer = true;
                }

                if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_CENTRAL_SERVER))
                {
                    if (!string.IsNullOrEmpty(LogFileName))
                        LogFileName += "_";
                    LogFileName += "Central";
                    startLog = true;
                    _startingCentralServer = true;
                }

                if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_MAP_SPAWN_SERVER))
                {
                    if (!string.IsNullOrEmpty(LogFileName))
                        LogFileName += "_";
                    LogFileName += "MapSpawn";
                    startLog = true;
                    _startingMapSpawnServer = true;
                }

                if (ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_MAP_SERVER))
                {
                    if (!string.IsNullOrEmpty(LogFileName))
                        LogFileName += "_";
                    LogFileName += $"Map({mapName})-Channel({channelId})-Allocate({isAllocate})-Instance({instanceId})";
                    startLog = true;
                    _startingMapServer = true;
                }

                if (_startingDatabaseServer || _startingCentralServer || _startingMapSpawnServer || _startingMapServer)
                {
                    if (!configFileFound)
                    {
                        // Write config file
                        Logging.Log(ToString(), "Not found config file, creating a new one");
                        if (!Directory.Exists(configFolder))
                            Directory.CreateDirectory(configFolder);
                        File.WriteAllText(configFilePath, JsonConvert.SerializeObject(Configs, Formatting.Indented));
                    }
                }

                if (startLog)
                    EnableLogger(LogFileName);
            }
            else
            {
                if (!useCustomDatabaseClient)
                    databaseNetworkManager.SetDatabaseByOptionIndex(databaseOptionIndex);

                if (startDatabaseOnAwake)
                    _startingDatabaseServer = true;

                if (startCentralOnAwake)
                    _startingCentralServer = true;

                if (startMapSpawnOnAwake)
                    _startingMapSpawnServer = true;

                if (startMapOnAwake)
                {
                    // If run map-server, don't load home scene (home scene load in `Game Instance`)
                    _startingMapId = startingMap.Id;
                    _startingMapServer = true;
                }
            }
#endif
        }

        private void OnDestroy()
        {
            Application.wantsToQuit -= Application_wantsToQuit;
        }

        private bool Application_wantsToQuit()
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (MapNetworkManager != null && MapNetworkManager.IsServer && !MapNetworkManager.ReadyToQuit)
            {
                Logging.Log("[MapNetworkManager] still proceeding before quit.");
                MapNetworkManager.ProceedBeforeQuit();
                return false;
            }
            if (databaseNetworkManager != null && databaseNetworkManager.IsServer && !databaseNetworkManager.ReadyToQuit)
            {
                Logging.Log("[DatabaseNetworkManager] still proceeding before quit.");
                databaseNetworkManager.ProceedBeforeQuit();
                return false;
            }
#endif
            return true;
        }

        public void EnableLogger(string fileName)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            CacheLogGUI.SetupLogger(fileName);
            CacheLogGUI.enabled = true;
#endif
        }

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void OnGameDataLoaded()
        {
            GameInstance.LoadHomeScenePreventions[nameof(MMOServerInstance)] = false;

            if (_startingCentralServer ||
                _startingMapSpawnServer ||
                _startingMapServer)
            {
                // Allow to test in editor, so still load home scene in editor
                GameInstance.LoadHomeScenePreventions[nameof(MMOServerInstance)] = !Application.isEditor || _startingMapServer;
            }

            StartServers();
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private async void StartServers()
        {
            // Wait a frame to make sure it will prepare servers' transports properly
            await UniTask.NextFrame();

            // Prepare guild data for guild database manager
            databaseNetworkManager.DatabaseCache = disableDatabaseCaching ? new DisabledDatabaseCache() : new LocalDatabaseCache();
            DatabaseNetworkManager.GuildMemberRoles = GameInstance.Singleton.SocialSystemSetting.GuildMemberRoles;
            DatabaseNetworkManager.GuildExpTree = GameInstance.Singleton.SocialSystemSetting.GuildExpTable.expTree;

            if (_startingDatabaseServer)
            {
                // Start database manager server
                StartDatabaseManagerServer();
            }

            if (_startingCentralServer)
            {
                // Start central server
                StartCentralServer();
            }

            if (_startingMapSpawnServer)
            {
                // Start map spawn server
                if (_spawningMaps != null && _spawningMaps.Count > 0)
                {
                    MapSpawnNetworkManager.spawningMaps = new List<BaseMapInfo>();
                    foreach (string spawningMapId in _spawningMaps)
                    {
                        if (!GameInstance.MapInfos.TryGetValue(spawningMapId, out BaseMapInfo tempMapInfo))
                            continue;
                        MapSpawnNetworkManager.spawningMaps.Add(tempMapInfo);
                    }
                }
                if (_spawningAllocateMaps != null && _spawningAllocateMaps.Count > 0)
                {
                    MapSpawnNetworkManager.spawningAllocateMaps = new List<SpawnAllocateMapData>();
                    foreach (SpawnAllocateMapByNameData spawningMap in _spawningAllocateMaps)
                    {
                        if (!GameInstance.MapInfos.TryGetValue(spawningMap.mapName, out BaseMapInfo tempMapInfo))
                            continue;
                        MapSpawnNetworkManager.spawningAllocateMaps.Add(new SpawnAllocateMapData()
                        {
                            mapInfo = tempMapInfo,
                            allocateAmount = spawningMap.allocateAmount,
                        });
                    }
                }
                StartMapSpawnServer();
            }

            if (_startingMapServer)
            {
                // Start map server
                BaseMapInfo tempMapInfo;
                if (!string.IsNullOrEmpty(_startingMapId) && GameInstance.MapInfos.TryGetValue(_startingMapId, out tempMapInfo))
                {
                    MapNetworkManager.Assets.addressableOnlineScene = tempMapInfo.AddressableScene;
#if !EXCLUDE_PREFAB_REFS
                    MapNetworkManager.Assets.onlineScene = tempMapInfo.Scene;
#endif
                    MapNetworkManager.SetMapInfo(tempMapInfo);
                }
                StartMapServer();
            }

            if (_startingCentralServer ||
                _startingMapSpawnServer ||
                _startingMapServer)
            {
                // Start database manager client, it will connect to database manager server
                // To request database functions
                StartDatabaseManagerClient();
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        #region Server functions
        public void StartCentralServer()
        {
            CentralNetworkManager.useWebSocket = UseWebSocket;
            CentralNetworkManager.webSocketSecure = WebSocketSecure;
            CentralNetworkManager.webSocketCertificateFilePath = WebSocketCertificateFilePath;
            CentralNetworkManager.webSocketCertificatePassword = WebSocketCertificatePassword;
            CentralNetworkManager.DatabaseClient = DatabaseClient;
            CentralNetworkManager.DataManager = new CentralServerDataManager();
            CentralNetworkManager.StartServer();
        }

        public void StartMapSpawnServer()
        {
            MapSpawnNetworkManager.StartServer();
        }

        public void StartMapServer()
        {
            MapNetworkManager.useWebSocket = UseWebSocket;
            MapNetworkManager.webSocketSecure = WebSocketSecure;
            MapNetworkManager.webSocketCertificateFilePath = WebSocketCertificateFilePath;
            MapNetworkManager.webSocketCertificatePassword = WebSocketCertificatePassword;
            MapNetworkManager.StartServer();
        }

        public void StartDatabaseManagerServer()
        {
            if (!useCustomDatabaseClient)
                databaseNetworkManager.StartServer();
        }

        public void StartDatabaseManagerClient()
        {
            if (!useCustomDatabaseClient)
                databaseNetworkManager.StartClient();
        }
        #endregion
#endif
    }
}
