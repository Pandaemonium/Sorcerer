using Godot;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Godot;

/// <summary>Non-blocking first-run guidance for a selected Gemini provider with no key.</summary>
internal static class GeminiSetupNotice
{
    public static void ShowIfNeeded(Node parent, string? provider)
    {
        if (SessionHost.GeminiSetupNoticeShown
            || !GeminiApiKeySetup.IsGeminiProvider(provider)
            || GeminiApiKeySetup.Check().Available)
        {
            return;
        }

        SessionHost.GeminiSetupNoticeShown = true;
        var dialog = new AcceptDialog
        {
            Title = "Set up Gemini for Sorcerer",
            DialogText = GeminiApiKeySetup.SetupInstructions(),
            Exclusive = true,
        };
        parent.AddChild(dialog);
        dialog.GetOkButton().Text = "Continue without Gemini";
        var keyButton = dialog.AddButton("Get a Gemini API key", right: true);
        keyButton.Pressed += () => OS.ShellOpen(GeminiApiKeySetup.AiStudioUrl);
        dialog.PopupCentered(new Vector2I(720, 480));
    }
}
