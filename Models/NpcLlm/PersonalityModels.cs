namespace StardewMod.Models;

public enum NpcPersonalitySource
{
    Fallback = 0,
    File = 1
}

public sealed class NpcPersonalitySection
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool Recognized { get; set; }
}

public sealed class NpcPersonalityProfile
{
    public string NpcName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public NpcPersonalitySource Source { get; set; } = NpcPersonalitySource.Fallback;

    public string FilePath { get; set; } = string.Empty;

    public string RawMarkdown { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Gender { get; set; } = string.Empty;

    public string SpeechStyle { get; set; } = string.Empty;

    public string WorkStyle { get; set; } = string.Empty;

    public string EntertainmentStyle { get; set; } = string.Empty;

    public string Hobbies { get; set; } = string.Empty;

    public string Dislikes { get; set; } = string.Empty;

    public string Likes { get; set; } = string.Empty;

    public string Secrets { get; set; } = string.Empty;

    public string ThinkingStyle { get; set; } = string.Empty;

    public List<NpcPersonalitySection> Sections { get; set; } = new();
}
