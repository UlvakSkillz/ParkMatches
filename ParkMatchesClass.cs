using HarmonyLib;
using MelonLoader;
using Photon.Pun;
using RUMBLE.Environment;
using RUMBLE.Environment.Park;
using RUMBLE.Interactions.InteractionBase;
using RUMBLE.Managers;
using RUMBLE.Players;
using RUMBLE.Players.Subsystems;
using System;
using System.Media;
using System.Threading;
using UnityEngine;

public static class Patch
{
    static HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("_ParkMatchesClass");

    public static void Start()
    {
        harmony.Patch(AccessTools.Method(typeof(ParkInstance), "RPC_CleanScene"), new HarmonyMethod(typeof(Patch), nameof(RPCCleanScenePostfix)));
    }

    private static void RPCCleanScenePostfix(ParkInstance __instance)
    {
        if (ParkMatches.ParkMatchesClass.instance.matchBufferTimer <= DateTime.Now) {
        Il2CppSystem.Collections.Generic.List<Player> playerList = ParkMatches.ParkMatchesClass.instance.playerManager.AllPlayers;
            if ((playerList.Count >= 2) && (!ParkMatches.ParkMatchesClass.instance.fightStarted))
            {
                int lowestActor = 256;
                //for all players, get lowest actor number for clients
                foreach (Player tempPlayer in playerList)
                {
                    if (ParkMatches.ParkMatchesClass.instance.localPlayer == tempPlayer)
                    {
                        ParkMatches.ParkMatchesClass.instance.clientActorNumber = tempPlayer.Data.GeneralData.ActorNo;
                    }
                    //if host found
                    if (tempPlayer.Data.GeneralData.ActorNo == 1)
                    {
                        ParkMatches.ParkMatchesClass.instance.hostPlayer = tempPlayer;
                    }
                    //if lowestActor not set yet
                    else if (lowestActor == 256)
                    {
                        //set lowestActor
                        lowestActor = tempPlayer.Data.GeneralData.ActorNo;
                    }
                    //if tempPlayer isn't host and is lower than currently found lowestActor
                    else if (tempPlayer.Data.GeneralData.ActorNo < lowestActor)
                    {
                        //set lowestActor
                        lowestActor = tempPlayer.Data.GeneralData.ActorNo;
                    }
                }
                MelonLogger.Msg($"Found Lowest Actor: {lowestActor} | Player's: {ParkMatches.ParkMatchesClass.instance.localPlayer.Data.GeneralData.ActorNo}");
                //if host or lowest actor number
                if ((ParkMatches.ParkMatchesClass.instance.isHost) || (ParkMatches.ParkMatchesClass.instance.localPlayer.Data.GeneralData.ActorNo == lowestActor))
                {
                    MelonLogger.Msg("Player Included");
                    //start match
                    ParkMatches.ParkMatchesClass.instance.StartFight();
                }
                else
                {
                    MelonLogger.Msg("Player Excluded");
                }
            }
        }
    }
}

namespace ParkMatches
{
    public class ParkMatchesClass : MelonMod
    {
        //variables
        public static ParkMatchesClass instance;
        private InteractionButton parkResetSceneButton;
        public PlayerManager playerManager;
        private PlayerResetSystem localResetSystem;
        private ParkMatchCounter parkMatchCounter;
        public DateTime matchBufferTimer;
        //variables that dont change
        private Vector3 hostRespawn = new Vector3(-8.65f, -5.5f, -5.75f);
        private Quaternion hostRotation = Quaternion.Euler(0, 40, 0);
        private Vector3 clientRespawn = new Vector3(2, -5.5f, 8);
        private Quaternion clientRotation = Quaternion.Euler(0, 220, 0);
        //variables that change
        private bool sceneChanged;
        private string currentScene = "";
        public bool fightStarted = false;
        private bool atRoundStart = false;
        //player variables
        public bool isHost = true;
        private int hostPoints, clientPoints;
        private PlayerHealth localPlayerHealth, otherPlayerHealth;
        public Player hostPlayer, clientPlayer;
        public short clientActorNumber;
        public Player localPlayer;
        //death timer variables
        private DateTime localDeathTimer, otherDeathTimer, redeathPreventionTimer;
        private bool localDeathTimerActive = false;
        private bool otherDeathTimerActive = false;
        private bool waitedAnUpdate = false;
        //sound variables
        private string[] FilePaths = new string[6];
        private static Thread[] threads = new Thread[3];
        private static bool[] threadActive = new bool[threads.Length];

        public override void OnInitializeMelon()
        {
            instance = this;
            matchBufferTimer = DateTime.Now;
            Patch.Start();
            //set File Paths
            FilePaths[0] = @"UserData\ParkMatches\DingDing.wav";
            FilePaths[1] = @"UserData\ParkMatches\YouWin.wav";
            FilePaths[2] = @"UserData\ParkMatches\YouLose.wav";
            FilePaths[3] = @"UserData\ParkMatches\ExitArena.wav";
            FilePaths[4] = @"UserData\ParkMatches\EnterArena.wav";
            FilePaths[5] = @"UserData\ParkMatches\NewRoundStart.wav";
            bool filesFound = true;
            //check if each file exists
            for (int i = 0; i < FilePaths.Length; i++)
            {
                if (!System.IO.File.Exists(FilePaths[i]))
                {
                    filesFound = false;
                    MelonLogger.Msg($"{FilePaths[i]} Doesn't Exist!");
                }
            }
            if (filesFound)
            {
                MelonLogger.Msg("Initialized");
            }
            else
            {
                MelonLogger.Msg("Initialized with Sound Files Missing");
            }
        }

        public override void OnFixedUpdate()
        {
            CheckForSceneChange();
            //if fight in progress
            if (fightStarted)
            {
                //if past instant double win/loss prevention
                if (!atRoundStart && (redeathPreventionTimer <= DateTime.Now))
                {
                    CheckHealths();
                    CheckPositions();
                }
                //if instant double win/loss prevention timer ended
                if (atRoundStart && (redeathPreventionTimer <= DateTime.Now))
                {
                    //clean scene
                    parkResetSceneButton.RPC_OnPressed();
                    ResetPlayers();
                    //play RoundStartTimer
                    PlaySoundIfFileExists(FilePaths[5], 0);
                    //end prevention
                    atRoundStart = false;
                }
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            sceneChanged = true;
            currentScene = sceneName;
            //not messing with scene change stuff here to allow retrying initializing variables
        }

        //checks if player is outside the ring and handles things accordingly
        public void CheckPositions()
        {
            //fighting variables
            Vector2 arenaCenter = new Vector2(-3.15f, 0.25f);
            Vector2 playerPoint;
            playerPoint.x = playerManager.localPlayer.Controller.gameObject.transform.GetChild(2).GetChild(13).GetChild(0).gameObject.transform.position.x;
            playerPoint.y = playerManager.localPlayer.Controller.gameObject.transform.GetChild(2).GetChild(13).GetChild(0).gameObject.transform.position.z;
            float distance = Vector2.Distance(playerPoint, arenaCenter);
            //if close enough
            if ((localDeathTimerActive) && (distance < 12))
            {
                PlaySoundIfFileExists(FilePaths[4], 2);
                //stop death timer
                localDeathTimerActive = false;
            }
            //if deathTimer is going
            if (localDeathTimerActive)
            {
                //if deathTimer elapsed
                if (localDeathTimer <= DateTime.Now)
                {
                    //helps with reset desync (helps but doesn't fix)
                    if (waitedAnUpdate)
                    {
                        //kill local player
                        localPlayerHealth.SetHealth(0, playerManager.AllPlayers[0].Data.HealthPoints, true);
                        MelonLogger.Msg($"Host player died from outside arena | Distance: {distance}");
                        localDeathTimerActive = false;
                        waitedAnUpdate = false;
                    }
                    else
                    {
                        waitedAnUpdate = true;
                    }
                }
            }
            //if outside arena but no timer
            else if ((distance >= 12) && (!atRoundStart))
            {
                PlaySoundIfFileExists(FilePaths[3], 1);
                //start death timer
                localDeathTimer = DateTime.Now.AddSeconds(2);
                localDeathTimerActive = true;
            }
            //if host
            if (isHost)
            {
                playerPoint.x = clientPlayer.Controller.gameObject.transform.GetChild(3).GetChild(13).GetChild(0).transform.position.x;
                playerPoint.y = clientPlayer.Controller.gameObject.transform.GetChild(3).GetChild(13).GetChild(0).transform.position.z;
            }
            //if client
            else
            {
                playerPoint.x = hostPlayer.Controller.gameObject.transform.GetChild(3).GetChild(13).GetChild(0).transform.position.x;
                playerPoint.y = hostPlayer.Controller.gameObject.transform.GetChild(3).GetChild(13).GetChild(0).transform.position.z;
            }
            distance = Vector2.Distance(playerPoint, arenaCenter);
            //if close enough
            if ((otherDeathTimerActive) && (distance < 12))
            {
                otherDeathTimerActive = false;
            }
            //if deathTimer is going
            if (otherDeathTimerActive)
            {
                //if deathTimer elapsed
                if (otherDeathTimer <= DateTime.Now)
                {
                    //kill other player
                    otherPlayerHealth.SetHealth(0, playerManager.AllPlayers[1].Data.HealthPoints, true);
                    MelonLogger.Msg($"Client player died from outside arena | Distance: {distance}");
                    otherDeathTimerActive = false;
                }
            }
            //if outside arena but no timer
            else if (distance >= 13)
            {
                //start death timer
                otherDeathTimer = DateTime.Now.AddSeconds(2);
                otherDeathTimerActive = true;
            }
        }

        //checks player healths to see if round/match should end
        public void CheckHealths()
        {
            //listen for health depletion from any player
            if ((hostPlayer.Data.HealthPoints == 0) || (clientPlayer.Data.HealthPoints == 0))
            {
                //add round points (both can tie rounds)
                if (clientPlayer.Data.HealthPoints == 0)
                {
                    MelonLogger.Msg("0 hp On Client");
                    hostPoints++;
                }
                if (hostPlayer.Data.HealthPoints == 0)
                {
                    MelonLogger.Msg("0 hp On Host");
                    clientPoints++;
                }
                //set winner to full health (loser auto heals)
                if (hostPlayer.Data.HealthPoints == 0)
                {
                    //if host
                    if (isHost)
                    {
                        //heal
                        otherPlayerHealth.SetHealth(20, playerManager.AllPlayers[1].Data.HealthPoints, false);
                    }
                    //if client
                    else
                    {
                        //heal
                        localPlayerHealth.SetHealth(20, playerManager.AllPlayers[0].Data.HealthPoints, false);
                    }
                }
                if (clientPlayer.Data.HealthPoints == 0)
                {
                    //if host
                    if (isHost)
                    {
                        //heal
                        localPlayerHealth.SetHealth(20, playerManager.AllPlayers[0].Data.HealthPoints, false);
                    }
                    //if client
                    else
                    {
                        //heal
                        otherPlayerHealth.SetHealth(20, playerManager.AllPlayers[1].Data.HealthPoints, false);
                    }
                }
                //if someone won
                if ((hostPoints == 2) || (clientPoints == 2))
                {
                    //if match ended client win
                    if (clientPoints == 2)
                    {
                        MelonLogger.Msg("Client won");
                        //if host
                        if (isHost)
                        {
                            //play you lose
                            PlaySoundIfFileExists(FilePaths[2], 0);
                        }
                        //if client
                        else
                        {
                            //play you win
                            PlaySoundIfFileExists(FilePaths[1], 0);
                        }
                    }
                    //if match ended host win (client wins match ties)
                    else
                    {
                        MelonLogger.Msg("Host won");
                        //if host
                        if (isHost)
                        {
                            //play you win
                            PlaySoundIfFileExists(FilePaths[1], 0);
                        }
                        //if client
                        else
                        {
                            //play you lose
                            PlaySoundIfFileExists(FilePaths[2], 0);
                        }
                    }
                    //end fight
                    matchBufferTimer = DateTime.Now.AddSeconds(1);
                    fightStarted = false;
                    //reset ring size
                    parkMatchCounter.ringSize = 13;
                    MelonLogger.Msg("Match Over");
                }
                //if nobody won
                else
                {
                    MelonLogger.Msg("New Round");
                    //stops instant double wins
                    redeathPreventionTimer = DateTime.Now.AddSeconds(3);
                    atRoundStart = true;
                    //clean scene
                    parkResetSceneButton.RPC_OnPressed();
                    ResetPlayers();
                }
            }
        }

        //moves players to start points and heals them
        private void ResetPlayers()
        {
            MelonLogger.Msg("Resetting Players");
            //Reset player
            localPlayerHealth.SetHealth(20, playerManager.AllPlayers[0].Data.HealthPoints, false);
            otherPlayerHealth.SetHealth(20, playerManager.AllPlayers[1].Data.HealthPoints, false);
            //if host
            if (isHost)
            {
                //set to light side spawn
                localResetSystem.RPC_RelocatePlayerController(hostRespawn, hostRotation);
            }
            //if client
            else
            {
                //set to dark side spawn
                localResetSystem.RPC_RelocatePlayerController(clientRespawn, clientRotation);
            }
        }

        //checks for scene change on update
        public void CheckForSceneChange()
        {
            //if scene changed
            if (sceneChanged)
            {
                try
                {
                    //if in park
                    if (currentScene == "Park")
                    {
                        //initialize scene variables
                        playerManager = GameObject.Find("Game Instance/Initializable/PlayerManager").GetComponent<PlayerManager>();
                        parkResetSceneButton = GameObject.Find("________________LOGIC__________________ /Heinhouwser products/Parkboard (Park)/Primary Display/Park/Minigame Start button/InteractionButton/Button").GetComponent<InteractionButton>();
                        parkMatchCounter = GameObject.Find("________________LOGIC__________________ /Park Toys/MatchCounter").GetComponent<ParkMatchCounter>();
                        localPlayerHealth = GameObject.Find("Health/Local").transform.parent.GetComponent<PlayerHealth>();
                        localPlayer = playerManager.localPlayer;
                        localResetSystem = playerManager.localPlayer.Controller.gameObject.GetComponent<PlayerResetSystem>();
                        isHost = PhotonNetwork.IsMasterClient;
                        MelonLogger.Msg("Reset Park Button Found");
                    }
                    else
                    {
                        //stop the fight for all other scenes
                        fightStarted = false;
                    }
                    //only runs when initialized successfully
                    sceneChanged = false;
                }
                catch
                {
                    return;
                }
            }
        }

        //setup for the fight
        public void StartFight()
        {
            MelonLogger.Msg("Starting Fight");
            try
            {
                //set match starting variables
                otherPlayerHealth = GameObject.Find("Player Controller(Clone)/Health").GetComponent<PlayerHealth>();
                //set score to 0
                hostPoints = 0;
                clientPoints = 0;
                //if host
                if (isHost)
                {
                    //set spawn points to light side
                    hostPlayer = playerManager.AllPlayers[0];
                    clientPlayer = playerManager.AllPlayers[1];
                }
                //if client
                else
                {
                    //set spawn points to dark side
                    hostPlayer = playerManager.AllPlayers[1];
                    clientPlayer = playerManager.AllPlayers[0];
                }
                //play Match Start sound
                PlaySoundIfFileExists(FilePaths[0], 0);
                //start fight
                redeathPreventionTimer = DateTime.Now.AddSeconds(3);
                atRoundStart = true;
                localDeathTimerActive = false;
                otherDeathTimerActive = false;
                fightStarted = true;
                ResetPlayers();
                //increase ring size
                parkMatchCounter.ringSize = 50;
                MelonLogger.Msg("Match Started");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Error Starting Fight: {e.Message}");
            }
        }

        //Plays the File Sound if it Exists
        private void PlaySoundIfFileExists(string soundFilePath, int threadToPlayOn)
        {
            //Check if the sound file exists
            if (System.IO.File.Exists(soundFilePath))
            {
                //Ensure that only one sound is playing at a time
                if (threadActive[threadToPlayOn])
                {
                    return;
                }
                try
                {
                    //Create a SoundPlayer instance with the specified sound file path
                    using (SoundPlayer player = new SoundPlayer(soundFilePath))
                    {
                        //Set flag to indicate that a sound is currently playing
                        threadActive[threadToPlayOn] = true;
                        //Create a new thread if no thread is active
                        if (threads[threadToPlayOn] == null || !threads[threadToPlayOn].IsAlive)
                        {
                            threads[threadToPlayOn] = new Thread(() =>
                            {
                                //Use PlaySync for synchronous playback
                                player.PlaySync();
                                //Reset flag to indicate that the sound has finished playing
                                threadActive[threadToPlayOn] = false;
                            });
                            //Start the thread
                            threads[threadToPlayOn].Start();
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Msg($"Error playing sound: {ex.Message}");
                }
            }
            else
            {
                MelonLogger.Msg("Sound File Doesn't Exist: " + soundFilePath);
            }
        }
    }
}
