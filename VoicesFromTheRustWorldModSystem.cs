using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VoicesFromTheRustWorld;

public sealed class VoicesFromTheRustWorldModSystem : ModSystem
{
    private ICoreClientAPI? capi;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        api.ChatCommands
            .Create("vfrw")
            .WithDescription("Voices from the Rust World commands")
            .BeginSubCommand("test")
            .WithDescription("Play the Whinging test narration clip")
            .HandleWith(OnTestNarrationCommand)
            .EndSubCommand();
    }

    private TextCommandResult OnTestNarrationCommand(TextCommandCallingArgs args)
    {
        if (capi is null)
        {
            return TextCommandResult.Error("Client API is not available.");
        }

        ILoadedSound? sound = capi.World.LoadSound(new SoundParams
        {
            Location = new AssetLocation("voicesfromtherustworld:sounds/narration/test/whinging/piece1"),
            SoundType = EnumSoundType.Sound,
            DisposeOnFinish = true,
            RelativePosition = true,
            ShouldLoop = false,
            Volume = 1.0f
        });

        if (sound is null)
        {
            return TextCommandResult.Error("Test narration was not found. Expected assets/voicesfromtherustworld/sounds/narration/test/whinging/piece1.ogg");
        }

        sound.Start();
        return TextCommandResult.Success("Playing Whinging test narration.");
    }
}
