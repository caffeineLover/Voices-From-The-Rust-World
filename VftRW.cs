using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace VoicesFromTheRustWorld;

public sealed class VoicesFromTheRustWorldModSystem : ModSystem
{
    private const string ClientConfigFileName = "voicesfromtherustworld-client.json";
    private const string ConfigLibConfigFileName = "voicesfromtherustworld-configlib.yaml";
    private const string DebugBookItemCode = "game:lore-book-aged-orange";
    private const string HarmonyId = "voicesfromtherustworld.booknarration";
    private const float DefaultNarrationVolume = 1.0f;
    private const float DefaultDuckingPercent = 50.0f;
    private const float MaxNarrationVolume = 8.0f;
    private const float MaxDuckingPercent = 100.0f;
    private const int WatcherIntervalMs = 250;

    private static VoicesFromTheRustWorldModSystem? activeClientSystem;

    private ICoreClientAPI? capi;
    private Harmony? harmony;
    private VfrwClientConfig clientConfig = new();
    private ILoadedSound? currentNarrationSound;
    private NarratorPack? currentNarratorPack;
    private NarrationEntryDefinition? currentNarrationEntry;
    private string currentNarrationDescription = "";
    private bool currentNarrationStartedFromBook;
    private long narrationStateWatcherId;
    private long bookCloseWatcherId;
    private VfrwSoundLevels? duckedSoundLevels;

    private readonly List<NarratorPack> narratorPacks = new();
    private readonly Dictionary<string, VfrwLoreAsset> loreAssets = new(StringComparer.OrdinalIgnoreCase);

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Client)
        {
            return;
        }

        LoadNarratorPacks(api);
        LoadLoreAssets(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        activeClientSystem = this;

        LoadClientConfig(api);
        TryRegisterConfigLib(api);
        PatchBookOpen(api);

        api.ChatCommands
            .Create("vfrw")
            .WithDescription("Voices from the Rust World commands")
            .BeginSubCommand("packs")
            .WithDescription("List discovered narrator packs")
            .HandleWith(OnListPacksCommand)
            .EndSubCommand()
            .BeginSubCommand("books")
            .WithDescription("List known lore book codes")
            .HandleWith(OnListBooksCommand)
            .EndSubCommand()
            .BeginSubCommand("book")
            .WithDescription("Give yourself a lore book for debugging")
            .WithExamples(".vfrw book whinging", ".vfrw book whinging 1")
            .IgnoreAdditionalArgs()
            .HandleWith(OnBookCommand)
            .EndSubCommand()
            .BeginSubCommand("play")
            .WithDescription("Play a narration clip by lore code")
            .WithExamples(".vfrw play whinging", ".vfrw play whinging 1")
            .IgnoreAdditionalArgs()
            .HandleWith(OnPlayNarrationCommand)
            .EndSubCommand()
            .BeginSubCommand("stop")
            .WithDescription("Stop the currently playing narration")
            .HandleWith(OnStopCommand)
            .EndSubCommand()
            .BeginSubCommand("volume")
            .WithDescription("Get or set the narration volume multiplier")
            .WithExamples(".vfrw volume", ".vfrw volume 2.5")
            .IgnoreAdditionalArgs()
            .HandleWith(OnVolumeCommand)
            .EndSubCommand()
            .BeginSubCommand("ducking")
            .WithDescription("Get or set how much other sound categories are lowered during narration")
            .WithExamples(".vfrw ducking", ".vfrw ducking 50", ".vfrw ducking off")
            .IgnoreAdditionalArgs()
            .HandleWith(OnDuckingCommand)
            .EndSubCommand()
            .BeginSubCommand("autoplay")
            .WithDescription("Get or set whether narration starts when a matching lore book opens")
            .WithExamples(".vfrw autoplay", ".vfrw autoplay on", ".vfrw autoplay off")
            .IgnoreAdditionalArgs()
            .HandleWith(OnAutoPlayCommand)
            .EndSubCommand();
    }

    public override void Dispose()
    {
        StopCurrentNarration();

        if (harmony is not null)
        {
            harmony.UnpatchAll(HarmonyId);
            harmony = null;
        }

        if (ReferenceEquals(activeClientSystem, this))
        {
            activeClientSystem = null;
        }
    }

    private void LoadClientConfig(ICoreClientAPI api)
    {
        try
        {
            VfrwClientConfig? loadedConfig = api.LoadModConfig<VfrwClientConfig>(ClientConfigFileName);
            clientConfig = loadedConfig ?? new VfrwClientConfig();
        }
        catch (Exception exception)
        {
            api.Logger.Warning("Could not load Voices from the Rust World client config. Using defaults. Error: {0}", exception.Message);
            clientConfig = new VfrwClientConfig();
        }

        SaveClientConfig(api);
    }

    private void SaveClientConfig(ICoreClientAPI api)
    {
        SanitizeClientConfig();
        api.StoreModConfig(clientConfig, ClientConfigFileName);
    }

    private void SanitizeClientConfig()
    {
        clientConfig.NarrationVolume = SanitizeVolume(clientConfig.NarrationVolume, DefaultNarrationVolume);
        clientConfig.OtherSoundDuckingPercent = SanitizePercent(clientConfig.OtherSoundDuckingPercent, DefaultDuckingPercent);
    }

    private void TryRegisterConfigLib(ICoreClientAPI api)
    {
        try
        {
            ModSystem? configLib = api.ModLoader.GetModSystem("ConfigLib.ConfigLibModSystem");
            if (configLib is null)
            {
                return;
            }

            MethodInfo? registerMethod = configLib.GetType().GetMethod(
                "RegisterCustomManagedConfig",
                new[]
                {
                    typeof(string),
                    typeof(object),
                    typeof(string),
                    typeof(Action),
                    typeof(Action<string>),
                    typeof(Action)
                }
            );

            if (registerMethod is null)
            {
                api.Logger.Warning("ConfigLib is enabled, but RegisterCustomManagedConfig was not found. Voices from the Rust World will use its normal JSON config only.");
                return;
            }

            registerMethod.Invoke(
                configLib,
                new object[]
                {
                    "voicesfromtherustworld",
                    clientConfig,
                    ConfigLibConfigFileName,
                    (Action)(() => OnExternalConfigChanged(api)),
                    (Action<string>)(_ => OnExternalConfigChanged(api)),
                    (Action)(() => OnExternalConfigChanged(api))
                }
            );

            OnExternalConfigChanged(api);
            api.Logger.Notification("Voices from the Rust World registered client settings with ConfigLib.");
        }
        catch (Exception exception)
        {
            api.Logger.Warning("Could not register Voices from the Rust World settings with ConfigLib. Error: {0}", exception.GetBaseException().Message);
        }
    }

    private void OnExternalConfigChanged(ICoreClientAPI api)
    {
        SaveClientConfig(api);
        ReapplyDuckingIfNarrationIsPlaying();
        UpdateCurrentNarrationVolume();
    }

    private void PatchBookOpen(ICoreClientAPI api)
    {
        try
        {
            Type? itemBookType = AccessTools.TypeByName("Vintagestory.GameContent.ItemBook");
            if (itemBookType is null)
            {
                api.Logger.Warning("Could not find Vintagestory.GameContent.ItemBook. Book-open narration will be disabled.");
                return;
            }

            MethodInfo? targetMethod = AccessTools.Method(
                itemBookType,
                "OnHeldInteractStart",
                new[]
                {
                    typeof(ItemSlot),
                    typeof(EntityAgent),
                    typeof(BlockSelection),
                    typeof(EntitySelection),
                    typeof(bool),
                    typeof(EnumHandHandling).MakeByRefType()
                }
            );

            MethodInfo? postfixMethod = AccessTools.Method(typeof(VoicesFromTheRustWorldModSystem), nameof(AfterBookHeldInteractStart));
            if (targetMethod is null || postfixMethod is null)
            {
                api.Logger.Warning("Could not patch ItemBook.OnHeldInteractStart. Book-open narration will be disabled.");
                return;
            }

            harmony = new Harmony(HarmonyId);
            harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfixMethod));
            api.Logger.Notification("Voices from the Rust World enabled book-open narration hook.");
        }
        catch (Exception exception)
        {
            api.Logger.Warning("Could not patch book opening for Voices from the Rust World. Error: {0}", exception.GetBaseException().Message);
        }
    }





    private static void AfterBookHeldInteractStart(
        ItemSlot slot,
        EntityAgent byEntity,
        BlockSelection blockSel,
        EntitySelection entitySel,
        bool firstEvent,
        ref EnumHandHandling handling
    )
    {
        activeClientSystem?.TryAutoPlayBookNarration(slot, byEntity, firstEvent, handling);
    }





    private void TryAutoPlayBookNarration(ItemSlot slot, EntityAgent byEntity, bool firstEvent, EnumHandHandling handling)
    {
        if (capi is null || !clientConfig.AutoPlayOnBookOpen || !firstEvent || handling != EnumHandHandling.PreventDefault)
            return;

        if (slot.Itemstack?.Attributes is null)
            return;

        if (capi.World.Player?.Entity is not null && byEntity.EntityId != capi.World.Player.Entity.EntityId)
            return;

        if (!slot.Itemstack.Attributes.HasAttribute("text") && !slot.Itemstack.Attributes.HasAttribute("textCodes"))
            return;

        string? loreCode = slot.Itemstack.Attributes.GetString("discoveryCode", null);
        if (string.IsNullOrWhiteSpace(loreCode))
            return;
        

        int piece = GetFirstBookPiece(slot.Itemstack);
        if (!TryStartNarration(loreCode, piece, true, out string message) && !message.StartsWith("No narrator pack", StringComparison.Ordinal))
            capi.ShowChatMessage(message);
    }





    // Discovers narrator-pack metadata from loaded assets and keeps valid packs in memory.
	// Each pack defines which lore pieces it can narrate and which sound assets to play.
    private void LoadNarratorPacks(ICoreAPI api)
    {
        narratorPacks.Clear();

        Dictionary<AssetLocation, NarratorPackDefinition> assets = api.Assets.GetMany<NarratorPackDefinition>(
            api.Logger,
            "config/voicesfromtherustworld/narrators/",
            null
        );

        foreach ((AssetLocation source, NarratorPackDefinition definition) in assets.OrderBy(asset => asset.Key.Domain).ThenBy(asset => asset.Key.Path))
        {
            if (string.IsNullOrWhiteSpace(definition.Code))
            {
                api.Logger.Warning("Skipping narrator pack {0}: missing code.", source);
                continue;
            }

            narratorPacks.Add(new NarratorPack(source, definition));
        }

        api.Logger.Notification("Voices from the Rust World discovered {0} narrator pack(s).", narratorPacks.Count);
    }




	// Discovers Vintage Story lore-book metadata from loaded assets and indexes it by lore code.
	// This lets commands and autoplay validate lore entries and map piece numbers to book text.
    private void LoadLoreAssets(ICoreAPI api)
    {
        loreAssets.Clear();

        Dictionary<AssetLocation, VfrwLoreAsset> assets = api.Assets.GetMany<VfrwLoreAsset>(
            api.Logger,
            "config/lore/",
            null
        );

        foreach ((AssetLocation source, VfrwLoreAsset asset) in assets.OrderBy(asset => asset.Key.Domain).ThenBy(asset => asset.Key.Path))
        {
            if (string.IsNullOrWhiteSpace(asset.Code))
            {
                api.Logger.Warning("Skipping lore asset {0}: missing code.", source);
                continue;
            }

            loreAssets[asset.Code] = asset;
        }

        api.Logger.Notification("Voices from the Rust World discovered {0} lore book(s).", loreAssets.Count);
    }





    private TextCommandResult OnListPacksCommand(TextCommandCallingArgs args)
    {
        if (narratorPacks.Count == 0)
            return TextCommandResult.Success("No narrator packs discovered.");

        string packs = string.Join(", ", narratorPacks.Select(pack => $"{pack.Definition.Name} ({pack.Definition.Code})"));
        return TextCommandResult.Success($"Narrator packs: {packs}");
    }





    private TextCommandResult OnListBooksCommand(TextCommandCallingArgs args)
    {
        if (loreAssets.Count == 0)
            return TextCommandResult.Success("No lore books discovered.");

        string loreCodes = string.Join(", ", loreAssets.Values.OrderBy(asset => asset.Code).Select(asset => asset.Code));
        return TextCommandResult.Success($"Lore books: {loreCodes}");
    }





    private TextCommandResult OnBookCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        string? loreCode = args.RawArgs.PopWord(null);
        if (string.IsNullOrWhiteSpace(loreCode))
        {
            return TextCommandResult.Error("Usage: .vfrw book <lorecode> [piece]. Example: .vfrw book whinging 1");
        }

        if (!loreAssets.TryGetValue(loreCode, out VfrwLoreAsset? loreAsset))
        {
            return TextCommandResult.Error($"Unknown lore code '{loreCode}'. Run .vfrw books to list known lore codes.");
        }

        if (loreAsset.Pieces.Length == 0)
        {
            return TextCommandResult.Error($"Lore code '{loreAsset.Code}' has no pieces.");
        }

        List<int> chapterIds;
        if (args.RawArgs.Length == 0)
        {
            chapterIds = Enumerable.Range(0, loreAsset.Pieces.Length).ToList();
        }
        else
        {
            string? pieceArg = args.RawArgs.PopWord(null);
            if (string.Equals(pieceArg, "all", StringComparison.OrdinalIgnoreCase))
            {
                chapterIds = Enumerable.Range(0, loreAsset.Pieces.Length).ToList();
            }
            else if (int.TryParse(pieceArg, NumberStyles.None, CultureInfo.InvariantCulture, out int pieceNumber))
            {
                if (pieceNumber < 1 || pieceNumber > loreAsset.Pieces.Length)
                    return TextCommandResult.Error($"Piece must be between 1 and {loreAsset.Pieces.Length} for lore code '{loreAsset.Code}'.");

                chapterIds = new List<int> { pieceNumber - 1 };
            }
            else
            {
                return TextCommandResult.Error("Piece must be a one-based number or 'all'. Example: .vfrw book whinging 1");
            }
        }

        string giveItemCommand = BuildGiveLoreBookCommand(loreAsset, chapterIds);
        capi.SendChatMessage(giveItemCommand);

        string pieceDescription = chapterIds.Count == loreAsset.Pieces.Length
            ? "all pieces"
            : $"piece {chapterIds[0] + 1}";

        return TextCommandResult.Success($"Sent /giveitem for lore '{loreAsset.Code}' ({pieceDescription}). Requires permission to use /giveitem.");
    }



    
    
    // Handles ".vfrw play <lorecode> [piece]":  Validate the requested lore entry/piece, start playback, then return
	// the appropriate chat command result.
    private TextCommandResult OnPlayNarrationCommand(TextCommandCallingArgs args)
    {
        string? loreCode = args.RawArgs.PopWord(null);
        if (string.IsNullOrWhiteSpace(loreCode))
            return TextCommandResult.Error("Usage: .vfrw play <lorecode> [piece]. Example: .vfrw play whinging 1");

        if (!loreAssets.TryGetValue(loreCode, out VfrwLoreAsset? loreAsset))
            return TextCommandResult.Error($"Unknown lore code '{loreCode}'. Run .vfrw books to list known lore codes.");

        if (loreAsset.Pieces.Length == 0)
            return TextCommandResult.Error($"Lore code '{loreAsset.Code}' has no pieces.");

        int piece = 1;
        if (args.RawArgs.Length != 0)
        {
            string? pieceArg = args.RawArgs.PopWord(null);
            if (!int.TryParse(pieceArg, NumberStyles.None, CultureInfo.InvariantCulture, out piece))
                return TextCommandResult.Error("Piece must be a one-based number. Example: .vfrw play whinging 1");
        }

        // Ensure that piece is between 1 and the number of pieces (inclusive) for that bit of lore.  Some lore only has
        // one piece, like Whinging.  Some lore have multiple pieces.
        if (piece < 1 || piece > loreAsset.Pieces.Length)
            return TextCommandResult.Error($"Piece must be between 1 and {loreAsset.Pieces.Length} for lore code '{loreAsset.Code}'.");

        return TryStartNarration(loreAsset.Code, piece, false, out string message)
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error(message);
    }

    private TextCommandResult OnStopCommand(TextCommandCallingArgs args)
    {
        if (currentNarrationSound is null || currentNarrationSound.IsDisposed || currentNarrationSound.HasStopped)
        {
            StopCurrentNarration();
            return TextCommandResult.Success("No narration is currently playing.");
        }

        string stoppedDescription = string.IsNullOrWhiteSpace(currentNarrationDescription)
            ? "current narration"
            : currentNarrationDescription;

        StopCurrentNarration();
        return TextCommandResult.Success($"Stopped {stoppedDescription}.");
    }

    private TextCommandResult OnVolumeCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        if (args.RawArgs.Length == 0)
        {
            return TextCommandResult.Success($"Narration volume is {FormatVolume(clientConfig.NarrationVolume)}x. Use .vfrw volume <0-8> to change it.");
        }

        float? requestedVolume = args.RawArgs.PopFloat(null);
        if (requestedVolume is null)
        {
            return TextCommandResult.Error("Usage: .vfrw volume <0-8>");
        }

        clientConfig.NarrationVolume = SanitizeVolume(requestedVolume.Value, DefaultNarrationVolume);
        SaveClientConfig(capi);
        UpdateCurrentNarrationVolume();

        return TextCommandResult.Success($"Narration volume set to {FormatVolume(clientConfig.NarrationVolume)}x.");
    }

    private TextCommandResult OnDuckingCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        if (args.RawArgs.Length == 0)
        {
            return TextCommandResult.Success(GetDuckingStatus());
        }

        string? duckingArg = args.RawArgs.PopWord(null);
        if (string.Equals(duckingArg, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(duckingArg, "disable", StringComparison.OrdinalIgnoreCase))
        {
            clientConfig.OtherSoundDuckingPercent = 0.0f;
        }
        else if (string.Equals(duckingArg, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(duckingArg, "enable", StringComparison.OrdinalIgnoreCase))
        {
            clientConfig.OtherSoundDuckingPercent = DefaultDuckingPercent;
        }
        else if (TryParsePercent(duckingArg, out float requestedPercent))
        {
            clientConfig.OtherSoundDuckingPercent = SanitizePercent(requestedPercent, DefaultDuckingPercent);
        }
        else
        {
            return TextCommandResult.Error("Usage: .vfrw ducking <0-100|on|off>");
        }

        SaveClientConfig(capi);
        ReapplyDuckingIfNarrationIsPlaying();

        return TextCommandResult.Success(GetDuckingStatus());
    }

    private TextCommandResult OnAutoPlayCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        if (args.RawArgs.Length == 0)
        {
            return TextCommandResult.Success($"Book-open autoplay is {(clientConfig.AutoPlayOnBookOpen ? "on" : "off")}.");
        }

        string? toggleArg = args.RawArgs.PopWord(null);
        if (!TryParseToggle(toggleArg, out bool enabled))
        {
            return TextCommandResult.Error("Usage: .vfrw autoplay <on|off>");
        }

        clientConfig.AutoPlayOnBookOpen = enabled;
        SaveClientConfig(capi);

        return TextCommandResult.Success($"Book-open autoplay is now {(clientConfig.AutoPlayOnBookOpen ? "on" : "off")}.");
    }

    private bool TryStartNarration(string loreCode, int piece, bool startedFromBook, out string message)
    {
        if (capi is null)
        {
            message = "Client API is not available.";
            return false;
        }

        if (!TryGetNarrationSound(loreCode, piece, out AssetLocation? soundLocation, out NarratorPack? narratorPack, out NarrationEntryDefinition? narrationEntry))
        {
            message = $"No narrator pack provides {loreCode} piece {piece}.";
            return false;
        }

        ILoadedSound? sound = capi.World.LoadSound(new SoundParams
        {
            Location = soundLocation,
            SoundType = EnumSoundType.Sound,
            DisposeOnFinish = false,
            RelativePosition = true,
            ShouldLoop = false,
            Volume = 1.0f
        });

        if (sound is null)
        {
            message = $"Narration asset was not found: {soundLocation}";
            return false;
        }

        StopCurrentNarration();

        currentNarrationSound = sound;
        currentNarratorPack = narratorPack;
        currentNarrationEntry = narrationEntry;
        currentNarrationDescription = $"{loreCode} piece {piece}";
        currentNarrationStartedFromBook = startedFromBook;

        float narrationVolume = UpdateCurrentNarrationVolume();
        ApplySoundDucking();

        sound.Start();
        StartNarrationStateWatcher();

        if (startedFromBook)
        {
            StartBookCloseWatcher();
        }

        string packName = string.IsNullOrWhiteSpace(narratorPack?.Definition.Name)
            ? narratorPack?.Definition.Code ?? "unknown pack"
            : narratorPack.Definition.Name;

        message = $"Playing {loreCode} piece {piece} from {packName} at {FormatVolume(narrationVolume)}x.";
        return true;
    }

    private bool TryGetNarrationSound(
        string loreCode,
        int piece,
        out AssetLocation? soundLocation,
        out NarratorPack? narratorPack,
        out NarrationEntryDefinition? narrationEntry
    )
    {
        foreach (NarratorPack pack in narratorPacks)
        {
            NarrationEntryDefinition? entry = pack.Definition.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.LoreCode, loreCode, StringComparison.OrdinalIgnoreCase)
                && candidate.Piece == piece
                && !string.IsNullOrWhiteSpace(candidate.Sound)
            );

            if (entry is null)
            {
                continue;
            }

            soundLocation = new AssetLocation(entry.Sound);
            narratorPack = pack;
            narrationEntry = entry;
            return true;
        }

        soundLocation = null;
        narratorPack = null;
        narrationEntry = null;
        return false;
    }

    private void StopCurrentNarration()
    {
        StopNarrationStateWatcher();
        StopBookCloseWatcher();

        ILoadedSound? sound = currentNarrationSound;
        currentNarrationSound = null;
        currentNarratorPack = null;
        currentNarrationEntry = null;
        currentNarrationDescription = "";
        currentNarrationStartedFromBook = false;

        if (sound is not null && !sound.IsDisposed)
        {
            if (!sound.HasStopped)
            {
                sound.Stop();
            }

            sound.Dispose();
        }

        RestoreSoundDucking();
    }

    private float UpdateCurrentNarrationVolume()
    {
        float narrationVolume = GetEffectiveNarrationVolume(currentNarratorPack, currentNarrationEntry);
        if (currentNarrationSound is not null && !currentNarrationSound.IsDisposed && !currentNarrationSound.HasStopped)
        {
            currentNarrationSound.SetVolume(narrationVolume);
        }

        return narrationVolume;
    }

    private void StartNarrationStateWatcher()
    {
        if (capi is null || narrationStateWatcherId != 0)
        {
            return;
        }

        narrationStateWatcherId = capi.Event.RegisterGameTickListener(OnNarrationStateTick, WatcherIntervalMs);
    }

    private void StopNarrationStateWatcher()
    {
        if (capi is null || narrationStateWatcherId == 0)
        {
            narrationStateWatcherId = 0;
            return;
        }

        capi.Event.UnregisterGameTickListener(narrationStateWatcherId);
        narrationStateWatcherId = 0;
    }

    private void OnNarrationStateTick(float dt)
    {
        if (currentNarrationSound is null || currentNarrationSound.IsDisposed || currentNarrationSound.HasStopped)
        {
            StopCurrentNarration();
        }
    }

    private void StartBookCloseWatcher()
    {
        if (capi is null || bookCloseWatcherId != 0)
        {
            return;
        }

        bookCloseWatcherId = capi.Event.RegisterGameTickListener(OnBookCloseTick, WatcherIntervalMs);
    }

    private void StopBookCloseWatcher()
    {
        if (capi is null || bookCloseWatcherId == 0)
        {
            bookCloseWatcherId = 0;
            return;
        }

        capi.Event.UnregisterGameTickListener(bookCloseWatcherId);
        bookCloseWatcherId = 0;
    }

    private void OnBookCloseTick(float dt)
    {
        if (capi is null || !currentNarrationStartedFromBook)
        {
            StopBookCloseWatcher();
            return;
        }

        bool readonlyBookIsOpen = capi.Gui.OpenedGuis.Any(dialog =>
            dialog.IsOpened()
            && string.Equals(dialog.GetType().FullName, "Vintagestory.GameContent.GuiDialogReadonlyBook", StringComparison.Ordinal)
        );

        if (!readonlyBookIsOpen)
        {
            StopCurrentNarration();
        }
    }

    private void ApplySoundDucking()
    {
        if (duckedSoundLevels is not null || clientConfig.OtherSoundDuckingPercent <= 0.0f)
        {
            return;
        }

        duckedSoundLevels = VfrwSoundLevels.Capture(clientConfig.DuckGeneralSoundCategory);

        float keepMultiplier = Math.Clamp(1.0f - (clientConfig.OtherSoundDuckingPercent / 100.0f), 0.0f, 1.0f);
        if (duckedSoundLevels.DuckedGeneralSound)
        {
            ClientSettings.SoundLevel = ScaleSoundLevel(duckedSoundLevels.Sound, keepMultiplier);
        }

        ClientSettings.EntitySoundLevel = ScaleSoundLevel(duckedSoundLevels.Entity, keepMultiplier);
        ClientSettings.AmbientSoundLevel = ScaleSoundLevel(duckedSoundLevels.Ambient, keepMultiplier);
        ClientSettings.WeatherSoundLevel = ScaleSoundLevel(duckedSoundLevels.Weather, keepMultiplier);
        ClientSettings.MusicLevel = ScaleSoundLevel(duckedSoundLevels.Music, keepMultiplier);
    }

    private void RestoreSoundDucking()
    {
        VfrwSoundLevels? previousLevels = duckedSoundLevels;
        duckedSoundLevels = null;

        if (previousLevels is null)
        {
            return;
        }

        if (previousLevels.DuckedGeneralSound)
        {
            ClientSettings.SoundLevel = previousLevels.Sound;
        }

        ClientSettings.EntitySoundLevel = previousLevels.Entity;
        ClientSettings.AmbientSoundLevel = previousLevels.Ambient;
        ClientSettings.WeatherSoundLevel = previousLevels.Weather;
        ClientSettings.MusicLevel = previousLevels.Music;
    }

    private void ReapplyDuckingIfNarrationIsPlaying()
    {
        if (currentNarrationSound is null || currentNarrationSound.IsDisposed || currentNarrationSound.HasStopped)
        {
            return;
        }

        RestoreSoundDucking();
        ApplySoundDucking();
    }

    private float GetEffectiveNarrationVolume(NarratorPack? narratorPack, NarrationEntryDefinition? narrationEntry)
    {
        float requestedVolume =
            clientConfig.NarrationVolume
            * (narratorPack?.Definition.Volume ?? 1.0f)
            * (narrationEntry?.Volume ?? 1.0f);

        return SanitizeVolume(requestedVolume, DefaultNarrationVolume);
    }

    private string GetDuckingStatus()
    {
        if (clientConfig.OtherSoundDuckingPercent <= 0.0f)
        {
            return "Other-sound ducking is off.";
        }

        string generalSoundNote = clientConfig.DuckGeneralSoundCategory
            ? " General sound effects are included."
            : " General sound effects are not included because narration uses that Vintage Story sound category.";

        return $"Other-sound ducking is {FormatPercent(clientConfig.OtherSoundDuckingPercent)}%.{generalSoundNote}";
    }

    private static int GetFirstBookPiece(ItemStack itemStack)
    {
        if (itemStack.Attributes["chapterIds"] is IntArrayAttribute chapterIds && chapterIds.value.Length > 0)
        {
            return Math.Max(1, chapterIds.value[0] + 1);
        }

        return 1;
    }

    private static float SanitizeVolume(float volume, float defaultVolume)
    {
        if (float.IsNaN(volume) || float.IsInfinity(volume))
        {
            return defaultVolume;
        }

        return Math.Clamp(volume, 0.0f, MaxNarrationVolume);
    }

    private static float SanitizePercent(float percent, float defaultPercent)
    {
        if (float.IsNaN(percent) || float.IsInfinity(percent))
        {
            return defaultPercent;
        }

        return Math.Clamp(percent, 0.0f, MaxDuckingPercent);
    }

    private static int ScaleSoundLevel(int level, float multiplier)
    {
        return Math.Clamp((int)MathF.Round(level * multiplier), 0, 100);
    }

    private static bool TryParsePercent(string? value, out float percent)
    {
        percent = 0.0f;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim().TrimEnd('%');
        return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
    }

    private static bool TryParseToggle(string? value, out bool enabled)
    {
        enabled = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            return true;
        }

        return false;
    }

    private static string FormatVolume(float volume)
    {
        return volume.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatPercent(float percent)
    {
        return percent.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string BuildGiveLoreBookCommand(VfrwLoreAsset loreAsset, IReadOnlyCollection<int> chapterIds)
    {
        string chapterIdsJson = string.Join(",", chapterIds);
        string textCodesJson = string.Join(",", chapterIds.Select(chapterId => JsonSerializer.Serialize(loreAsset.Pieces[chapterId])));

        string attributesJson =
            "{"
            + "\"discoveryCode\":" + JsonSerializer.Serialize(loreAsset.Code)
            + ",\"chapterIds\":[" + chapterIdsJson + "]"
            + ",\"textCodes\":[" + textCodesJson + "]"
            + ",\"titleCode\":" + JsonSerializer.Serialize(loreAsset.Title)
            + "}";

        return $"/giveitem {DebugBookItemCode} 1 s[] {attributesJson}";
    }

    private sealed class VfrwSoundLevels
    {
        private VfrwSoundLevels(bool duckedGeneralSound)
        {
            DuckedGeneralSound = duckedGeneralSound;
            Sound = ClientSettings.SoundLevel;
            Entity = ClientSettings.EntitySoundLevel;
            Ambient = ClientSettings.AmbientSoundLevel;
            Weather = ClientSettings.WeatherSoundLevel;
            Music = ClientSettings.MusicLevel;
        }

        public bool DuckedGeneralSound { get; }

        public int Sound { get; }

        public int Entity { get; }

        public int Ambient { get; }

        public int Weather { get; }

        public int Music { get; }

        public static VfrwSoundLevels Capture(bool duckedGeneralSound)
        {
            return new VfrwSoundLevels(duckedGeneralSound);
        }
    }
}

public sealed class VfrwLoreAsset
{
    private string[] pieces = Array.Empty<string>();

    public string Code { get; set; } = "";

    public string Title { get; set; } = "";

    [AllowNull]
    public string[] Pieces
    {
        get => pieces;
        set => pieces = value ?? Array.Empty<string>();
    }

    public string Category { get; set; } = "";
}

public sealed class NarratorPack
{
    public NarratorPack(AssetLocation source, NarratorPackDefinition definition)
    {
        Source = source;
        Definition = definition;
    }

    public AssetLocation Source { get; }

    public NarratorPackDefinition Definition { get; }
}

public sealed class NarratorPackDefinition
{
    private List<NarrationEntryDefinition> entries = new();

    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string[] Authors { get; set; } = Array.Empty<string>();

    public string Language { get; set; } = "en";

    public string Format { get; set; } = "ogg-vorbis";

    public float Volume { get; set; } = 1.0f;

    [AllowNull]
    public List<NarrationEntryDefinition> Entries
    {
        get => entries;
        set => entries = value ?? new List<NarrationEntryDefinition>();
    }
}

public sealed class NarrationEntryDefinition
{
    public string LoreCode { get; set; } = "";

    public int Piece { get; set; }

    public string Sound { get; set; } = "";

    public float Volume { get; set; } = 1.0f;
}

public sealed class VfrwClientConfig
{
    public bool AutoPlayOnBookOpen = true;

    public float NarrationVolume = 1.0f;

    public float OtherSoundDuckingPercent = 50.0f;

    public bool DuckGeneralSoundCategory = false;
}
