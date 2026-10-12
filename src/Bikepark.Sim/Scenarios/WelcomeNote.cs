namespace Bikepark.Sim.Scenarios;

/// <summary>
/// The scenario's welcome (content): what the player reads when the game starts (the HUD shows it while the world is
/// still at tick 0). Presentation only, it never affects the sim. <c>{park}</c> in the title or text is replaced by the
/// park's name. <see cref="FocusTrail"/> names a trail whose end the "Show me" button points the camera at.
/// </summary>
public sealed class WelcomeNote
{
    public string Title { get; set; } = "";

    /// <summary>Paragraphs, shown in order.</summary>
    public List<string> Paragraphs { get; set; } = [];

    /// <summary>A trail to show (its end), or null.</summary>
    public string? FocusTrail { get; set; }

    /// <summary>The title or a paragraph with <c>{park}</c> replaced.</summary>
    public static string Fill(string text, string parkName) => text.Replace("{park}", parkName, StringComparison.Ordinal);

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Title)) yield return "welcome: title is required";
        if (Paragraphs.Count == 0 || Paragraphs.Any(string.IsNullOrWhiteSpace)) yield return "welcome: paragraphs must not be empty";
        if (FocusTrail is { } trail && string.IsNullOrWhiteSpace(trail)) yield return "welcome: focusTrail must name a trail";
    }
}
