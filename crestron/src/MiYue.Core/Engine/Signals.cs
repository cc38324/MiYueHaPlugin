namespace MiYue.Core.Engine
{
    /// <summary>
    /// Signal index contract between the C# engine and "MiYue Player v1.0.usp".
    /// The SIMPL+ module declares the SAME numbers as #DEFINE_CONSTANT (tests/SignalContractTests
    /// checks both files agree) - change them together or not at all.
    /// </summary>
    public static class PlayerSignals
    {
        // ---- digital inputs (MiYuePlayer.Digital(index, value)) -----------------------------
        // Pulses act on the rising edge; VolUp/VolDown/TtsToGroup are levels.
        public const ushort DiPlay = 1;
        public const ushort DiPause = 2;
        public const ushort DiPlayPause = 3;
        public const ushort DiStop = 4;
        public const ushort DiNext = 5;
        public const ushort DiPrevious = 6;
        public const ushort DiVolUp = 7;
        public const ushort DiVolDown = 8;
        public const ushort DiMuteOn = 9;
        public const ushort DiMuteOff = 10;
        public const ushort DiMuteToggle = 11;
        public const ushort DiModeNormal = 12;
        public const ushort DiModeRepeatAll = 13;
        public const ushort DiModeRepeatOne = 14;
        public const ushort DiModeShuffle = 15;
        public const ushort DiModeCycle = 16;
        public const ushort DiSourceMusic = 17;
        public const ushort DiSourceSpdif = 18;
        public const ushort DiSourceBluetooth = 19;
        public const ushort DiSourceAux = 20;
        public const ushort DiInputSpdifOn = 21;
        public const ushort DiInputSpdifOff = 22;
        public const ushort DiInputBluetoothOn = 23;
        public const ushort DiInputBluetoothOff = 24;
        public const ushort DiInputAuxOn = 25;
        public const ushort DiInputAuxOff = 26;
        public const ushort DiGroupBecomeMaster = 27;
        public const ushort DiGroupLeave = 28;
        public const ushort DiGroupDissolve = 29;
        public const ushort DiRefresh = 30;
        public const ushort DiReconnect = 31;
        public const ushort DiTtsToGroup = 32;
        /// <summary>Scene[1..16] = 101..116 (execute the n-th enabled device scene).</summary>
        public const ushort DiSceneBase = 100;
        public const int SceneSlots = 16;

        // ---- analog inputs (MiYuePlayer.Analog(index, value)) --------------------------------
        public const ushort AiVolumeSet = 1;        // 0..100
        public const ushort AiVolumeSetRaw = 2;     // 0..65535
        public const ushort AiSeekSeconds = 3;      // seek within the current track
        public const ushort AiTtsVolume = 4;        // 1..100, 0 = current volume (or 30)

        // ---- serial inputs (MiYuePlayer.Serial(index, value)) --------------------------------
        public const ushort SiTtsText = 1;
        public const ushort SiPlayUrl = 2;
        public const ushort SiGroupJoinMaster = 3;  // IP of the master this player joins as a slave
        public const ushort SiGroupAddSlave = 4;    // IP of a player to add to this player's group
        public const ushort SiSceneExecute = 5;     // scene id, name (cmdName) or trigger code (cmd)

        // ---- digital outputs --------------------------------------------------------------------
        public const ushort DoOnline = 1;
        public const ushort DoPlaying = 2;
        public const ushort DoPaused = 3;
        public const ushort DoStopped = 4;
        public const ushort DoMuted = 5;
        public const ushort DoModeNormal = 6;
        public const ushort DoModeRepeatAll = 7;
        public const ushort DoModeRepeatOne = 8;
        public const ushort DoModeShuffle = 9;
        public const ushort DoCanSkip = 10;
        public const ushort DoSourceMusic = 11;
        public const ushort DoSourceSpdif = 12;
        public const ushort DoSourceBluetooth = 13;
        public const ushort DoSourceAux = 14;
        public const ushort DoHasSpdif = 15;
        public const ushort DoHasBluetooth = 16;
        public const ushort DoHasAux = 17;
        public const ushort DoGroupMaster = 18;
        public const ushort DoGroupSlave = 19;
        public const ushort DoGrouped = 20;
        public const ushort DoGroupBusy = 21;

        // ---- analog outputs ---------------------------------------------------------------------
        public const ushort AoVolume = 1;           // 0..100
        public const ushort AoVolumeRaw = 2;        // 0..65535
        public const ushort AoPosition = 3;         // seconds
        public const ushort AoDuration = 4;         // seconds
        public const ushort AoProgressRaw = 5;      // 0..65535
        public const ushort AoQueueIndex = 6;       // 1-based, 0 = none / external source
        public const ushort AoQueueTotal = 7;
        public const ushort AoPort = 8;
        public const ushort AoSceneCount = 9;
        public const ushort AoGroupMemberCount = 10;
        public const ushort AoBluetoothStatus = 11;

        // ---- serial outputs ---------------------------------------------------------------------
        public const ushort SoTitle = 1;
        public const ushort SoArtist = 2;
        public const ushort SoAlbum = 3;
        public const ushort SoCoverUrl = 4;
        public const ushort SoTransportState = 5;
        public const ushort SoPlayMode = 6;
        public const ushort SoSource = 7;
        public const ushort SoPositionText = 8;
        public const ushort SoDurationText = 9;
        public const ushort SoDeviceName = 10;
        public const ushort SoModel = 11;
        public const ushort SoFirmware = 12;
        public const ushort SoUdn = 13;
        public const ushort SoStatus = 14;
        public const ushort SoLastError = 15;
        public const ushort SoGroupRole = 16;
        public const ushort SoGroupMasterIp = 17;
        public const ushort SoGroupId = 18;
        public const ushort SoGroupMembers = 19;
        /// <summary>Scene_Name[1..16] = 101..116.</summary>
        public const ushort SoSceneNameBase = 100;
    }

    /// <summary>Signal index contract between the browse engine and "MiYue Browser v1.0.usp".</summary>
    public static class BrowserSignals
    {
        public const int MaxPageSize = 20;

        // ---- digital inputs -----------------------------------------------------------------------
        public const ushort DiListLiked = 1;
        public const ushort DiListSonglists = 2;
        public const ushort DiListBoards = 3;
        public const ushort DiListRadios = 4;
        public const ushort DiListQueue = 5;
        public const ushort DiListScenes = 6;
        public const ushort DiPageNext = 7;
        public const ushort DiPagePrev = 8;
        public const ushort DiPageFirst = 9;
        public const ushort DiBack = 10;
        public const ushort DiPlayAll = 11;
        public const ushort DiRefresh = 12;
        /// <summary>Item_Select[1..20] = 101..120 (item slot on the current page).</summary>
        public const ushort DiItemBase = 100;

        // ---- analog inputs ------------------------------------------------------------------------
        public const ushort AiItemClicked = 1;      // Smart Graphics "Item Clicked" (1-based slot on page)
        public const ushort AiGotoPage = 2;         // 1-based page number

        // ---- digital outputs ----------------------------------------------------------------------
        public const ushort DoBusy = 1;
        public const ushort DoCanBack = 2;
        public const ushort DoHasPrev = 3;
        public const ushort DoHasNext = 4;
        public const ushort DoPlayerFound = 5;
        /// <summary>Item_Is_Current[1..20] = 101..120 (queue list: the track now playing).</summary>
        public const ushort DoItemCurrentBase = 100;

        // ---- analog outputs -----------------------------------------------------------------------
        public const ushort AoPage = 1;             // 1-based
        public const ushort AoPageCount = 2;
        public const ushort AoItemsOnPage = 3;      // wire to the list's "Set Number of Items"
        public const ushort AoTotalItems = 4;

        // ---- serial outputs -----------------------------------------------------------------------
        public const ushort SoListTitle = 1;
        public const ushort SoStatus = 2;
        /// <summary>Item_Text[1..20] = 101..120, Item_Sub[1..20] = 201..220, Item_Icon[1..20] = 301..320.</summary>
        public const ushort SoItemTextBase = 100;
        public const ushort SoItemSubBase = 200;
        public const ushort SoItemIconBase = 300;
    }
}
