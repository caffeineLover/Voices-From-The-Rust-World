using System.Globalization;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VoicesFromTheRustWorld;

public sealed class VoicesFromTheRustWorldModSystem : ModSystem
{
    private const string ClientConfigFileName = "voicesfromtherustworld-client.json";
    private const string DebugBookItemCode = "game:lore-book-aged-orange";
    private const float DefaultNarrationVolume = 1.0f;
    private const float MaxNarrationVolume = 4.0f;

    private ICoreClientAPI? capi;
    private VfrwClientConfig clientConfig = new();
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
        LoadClientConfig(api);

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
            .BeginSubCommand("whinging")
            .WithDescription("Play the Whinging narration clip")
            .HandleWith(OnWhingingNarrationCommand)
            .EndSubCommand()
            .BeginSubCommand("volume")
            .WithDescription("Get or set the narration volume multiplier")
            .WithExamples(".vfrw volume", ".vfrw volume 1.5")
            .IgnoreAdditionalArgs()
            .HandleWith(OnVolumeCommand)
            .EndSubCommand();
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

        clientConfig.NarrationVolume = SanitizeVolume(clientConfig.NarrationVolume, DefaultNarrationVolume);
        api.StoreModConfig(clientConfig, ClientConfigFileName);
    }

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

            definition.Entries ??= new List<NarrationEntryDefinition>();
            narratorPacks.Add(new NarratorPack(source, definition));
        }

        api.Logger.Notification("Voices from the Rust World discovered {0} narrator pack(s).", narratorPacks.Count);
    }

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

            asset.Pieces ??= Array.Empty<string>();
            loreAssets[asset.Code] = asset;
        }

        api.Logger.Notification("Voices from the Rust World discovered {0} lore book(s).", loreAssets.Count);
    }

    private TextCommandResult OnListPacksCommand(TextCommandCallingArgs args)
    {
        if (narratorPacks.Count == 0)
        {
            return TextCommandResult.Success("No narrator packs discovered.");
        }

        string packs = string.Join(", ", narratorPacks.Select(pack => $"{pack.Definition.Name} ({pack.Definition.Code})"));
        return TextCommandResult.Success($"Narrator packs: {packs}");
    }

    private TextCommandResult OnListBooksCommand(TextCommandCallingArgs args)
    {
        if (loreAssets.Count == 0)
        {
            return TextCommandResult.Success("No lore books discovered.");
        }

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
                {
                    return TextCommandResult.Error($"Piece must be between 1 and {loreAsset.Pieces.Length} for lore code '{loreAsset.Code}'.");
                }

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

    private TextCommandResult OnWhingingNarrationCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        if (!TryGetNarrationSound("whinging", 1, out AssetLocation? soundLocation, out NarratorPack? narratorPack, out NarrationEntryDefinition? narrationEntry))
        {
            return TextCommandResult.Error("No narrator pack provides whinging piece 1.");
        }

        ILoadedSound? sound = capi.World.LoadSound(new SoundParams
        {
            Location = soundLocation,
            SoundType = EnumSoundType.Sound,
            DisposeOnFinish = true,
            RelativePosition = true,
            ShouldLoop = false,
            Volume = 1.0f
        });

        if (sound is null)
        {
            return TextCommandResult.Error($"Whinging narration asset was not found: {soundLocation}");
        }

        float narrationVolume = GetEffectiveNarrationVolume(narratorPack, narrationEntry);
        sound.SetVolume(narrationVolume);
        sound.Start();
        string packName = string.IsNullOrWhiteSpace(narratorPack?.Definition.Name)
            ? narratorPack?.Definition.Code ?? "unknown pack"
            : narratorPack.Definition.Name;
        return TextCommandResult.Success($"Playing Whinging narration from {packName} at {FormatVolume(narrationVolume)}x.");
    }

    private TextCommandResult OnVolumeCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        if (args.RawArgs.Length == 0)
        {
            return TextCommandResult.Success($"Narration volume is {FormatVolume(clientConfig.NarrationVolume)}x. Use .vfrw volume <0-4> to change it.");
        }

        float? requestedVolume = args.RawArgs.PopFloat(null);
        if (requestedVolume is null)
        {
            return TextCommandResult.Error("Usage: .vfrw volume <0-4>");
        }

        clientConfig.NarrationVolume = SanitizeVolume(requestedVolume.Value, DefaultNarrationVolume);
        capi.StoreModConfig(clientConfig, ClientConfigFileName);

        return TextCommandResult.Success($"Narration volume set to {FormatVolume(clientConfig.NarrationVolume)}x.");
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

    private float GetEffectiveNarrationVolume(NarratorPack? narratorPack, NarrationEntryDefinition? narrationEntry)
    {
        float requestedVolume =
            clientConfig.NarrationVolume
            * (narratorPack?.Definition.Volume ?? 1.0f)
            * (narrationEntry?.Volume ?? 1.0f);

        return SanitizeVolume(requestedVolume, DefaultNarrationVolume);
    }

    private static float SanitizeVolume(float volume, float defaultVolume)
    {
        if (float.IsNaN(volume) || float.IsInfinity(volume))
        {
            return defaultVolume;
        }

        return Math.Clamp(volume, 0.0f, MaxNarrationVolume);
    }

    private static string FormatVolume(float volume)
    {
        return volume.ToString("0.##", CultureInfo.InvariantCulture);
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
}

public sealed class VfrwLoreAsset
{
    public string Code { get; set; } = "";

    public string Title { get; set; } = "";

    public string[] Pieces { get; set; } = Array.Empty<string>();

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
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string[] Authors { get; set; } = Array.Empty<string>();

    public string Language { get; set; } = "en";

    public string Format { get; set; } = "ogg-vorbis";

    public float Volume { get; set; } = 1.0f;

    public List<NarrationEntryDefinition> Entries { get; set; } = new();
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
    public float NarrationVolume = 1.0f;
}
