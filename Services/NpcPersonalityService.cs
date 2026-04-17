using System.Text;
using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

internal sealed class NpcPersonalityService
{
    private readonly IModHelper helper;
    private readonly NpcLlmConsoleLogger logger;

    public NpcPersonalityService(IModHelper helper, NpcLlmConsoleLogger logger)
    {
        this.helper = helper;
        this.logger = logger;
    }

    public NpcPersonalityProfile GetPersonalityProfile(NPC npc)
    {
        string path = this.GetPersonalityPath(npc.Name);
        if (!File.Exists(path))
        {
            this.logger.Debug("Personality", $"人格文件缺失，使用 fallback path={path}", npc.Name);
            return this.BuildFallbackProfile(npc, path);
        }

        try
        {
            string markdown = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                this.logger.Warn("Personality", $"人格文件为空，回退 fallback path={path}", npc.Name);
                return this.BuildFallbackProfile(npc, path);
            }

            NpcPersonalityProfile profile = this.ParseMarkdown(npc, markdown, path);
            this.MergeMissingFields(profile, this.BuildFallbackProfile(npc, path));
            profile.Source = NpcPersonalitySource.File;
            profile.FilePath = path;
            return profile;
        }
        catch (Exception ex)
        {
            this.logger.Warn("Personality", $"读取人格文件失败，回退 fallback：{ex.Message}", npc.Name);
            return this.BuildFallbackProfile(npc, path);
        }
    }

    public string GetPersonalityPath(string npcName)
    {
        return Path.Combine(this.helper.DirectoryPath, "Personality", npcName, $"{npcName}.md");
    }

    private NpcPersonalityProfile ParseMarkdown(NPC npc, string markdown, string path)
    {
        List<NpcPersonalitySection> sections = new();
        string currentTitle = string.Empty;
        StringBuilder buffer = new();
        foreach (string rawLine in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.Trim();
            if (TryParseHeading(trimmed, out string heading))
            {
                FlushSection(sections, ref currentTitle, buffer);
                currentTitle = heading;
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentTitle) && TryParseInlineField(trimmed, out string inlineTitle, out string inlineValue))
            {
                sections.Add(new NpcPersonalitySection
                {
                    Key = NormalizeSectionKey(inlineTitle),
                    Title = inlineTitle,
                    Content = inlineValue,
                    Recognized = !string.IsNullOrWhiteSpace(NormalizeSectionKey(inlineTitle))
                });
                continue;
            }

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }

            buffer.Append(line);
        }

        FlushSection(sections, ref currentTitle, buffer);
        if (sections.Count == 0)
        {
            return this.BuildFallbackProfile(npc, path);
        }

        NpcPersonalityProfile profile = new()
        {
            NpcName = npc.Name,
            DisplayName = npc.displayName,
            Source = NpcPersonalitySource.File,
            FilePath = path,
            RawMarkdown = markdown,
            Sections = sections
        };
        foreach (NpcPersonalitySection section in sections)
        {
            string key = NormalizeSectionKey(section.Title);
            string content = section.Content.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            switch (key)
            {
                case "name":
                    profile.Name = content;
                    break;
                case "gender":
                    profile.Gender = content;
                    break;
                case "speech_style":
                    profile.SpeechStyle = content;
                    break;
                case "work_style":
                    profile.WorkStyle = content;
                    break;
                case "entertainment_style":
                    profile.EntertainmentStyle = content;
                    break;
                case "hobbies":
                    profile.Hobbies = content;
                    break;
                case "dislikes":
                    profile.Dislikes = content;
                    break;
                case "likes":
                    profile.Likes = content;
                    break;
                case "secrets":
                    profile.Secrets = content;
                    break;
                case "thinking_style":
                    profile.ThinkingStyle = content;
                    break;
            }
        }

        return profile;
    }

    private NpcPersonalityProfile BuildFallbackProfile(NPC npc, string path)
    {
        string gender = npc.Gender.ToString();
        string speechStyle = this.DescribeSpeechStyle(npc);
        string workStyle = this.DescribeWorkStyle(npc);
        string entertainmentStyle = this.DescribeEntertainmentStyle(npc);
        string hobbies = this.DescribeHobbies(npc);
        string dislikes = "没有手写人格文件时，不要擅自编造极端厌恶对象；只根据已知记忆、事实和现场事件表达保留态度。";
        string likes = this.DescribeLikes(npc);
        string secrets = "未提供手写人格文件时，不要虚构重大秘密；只有记忆、事实或现场上下文明示时才可提及。";
        string thinkingStyle = this.DescribeThinkingStyle(npc);
        NpcPersonalityProfile profile = new()
        {
            NpcName = npc.Name,
            DisplayName = npc.displayName,
            Source = NpcPersonalitySource.Fallback,
            FilePath = path,
            Name = npc.displayName,
            Gender = gender,
            SpeechStyle = speechStyle,
            WorkStyle = workStyle,
            EntertainmentStyle = entertainmentStyle,
            Hobbies = hobbies,
            Dislikes = dislikes,
            Likes = likes,
            Secrets = secrets,
            ThinkingStyle = thinkingStyle
        };
        profile.Sections = new List<NpcPersonalitySection>
        {
            CreateSection("名字", "name", profile.Name),
            CreateSection("性别", "gender", profile.Gender),
            CreateSection("说话方式", "speech_style", profile.SpeechStyle),
            CreateSection("做事方式", "work_style", profile.WorkStyle),
            CreateSection("娱乐方式", "entertainment_style", profile.EntertainmentStyle),
            CreateSection("爱好", "hobbies", profile.Hobbies),
            CreateSection("讨厌", "dislikes", profile.Dislikes),
            CreateSection("喜欢", "likes", profile.Likes),
            CreateSection("秘密", "secrets", profile.Secrets),
            CreateSection("思考方式", "thinking_style", profile.ThinkingStyle)
        };
        profile.RawMarkdown = string.Join(
            "\n\n",
            profile.Sections.Select(section => $"## {section.Title}\n{section.Content}".Trim()));
        return profile;
    }

    private void MergeMissingFields(NpcPersonalityProfile profile, NpcPersonalityProfile fallback)
    {
        profile.Name = Prefer(profile.Name, fallback.Name);
        profile.Gender = Prefer(profile.Gender, fallback.Gender);
        profile.SpeechStyle = Prefer(profile.SpeechStyle, fallback.SpeechStyle);
        profile.WorkStyle = Prefer(profile.WorkStyle, fallback.WorkStyle);
        profile.EntertainmentStyle = Prefer(profile.EntertainmentStyle, fallback.EntertainmentStyle);
        profile.Hobbies = Prefer(profile.Hobbies, fallback.Hobbies);
        profile.Dislikes = Prefer(profile.Dislikes, fallback.Dislikes);
        profile.Likes = Prefer(profile.Likes, fallback.Likes);
        profile.Secrets = Prefer(profile.Secrets, fallback.Secrets);
        profile.ThinkingStyle = Prefer(profile.ThinkingStyle, fallback.ThinkingStyle);
        if (profile.Sections.Count == 0)
        {
            profile.Sections = fallback.Sections;
        }
    }

    private static void FlushSection(List<NpcPersonalitySection> sections, ref string currentTitle, StringBuilder buffer)
    {
        if (string.IsNullOrWhiteSpace(currentTitle))
        {
            buffer.Clear();
            return;
        }

        string content = buffer.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(content))
        {
            string key = NormalizeSectionKey(currentTitle);
            sections.Add(new NpcPersonalitySection
            {
                Key = key,
                Title = currentTitle,
                Content = content,
                Recognized = !string.IsNullOrWhiteSpace(key)
            });
        }

        currentTitle = string.Empty;
        buffer.Clear();
    }

    private static bool TryParseHeading(string line, out string heading)
    {
        heading = string.Empty;
        if (!line.StartsWith('#'))
        {
            return false;
        }

        heading = line.TrimStart('#').Trim().Trim('：', ':');
        return !string.IsNullOrWhiteSpace(heading);
    }

    private static bool TryParseInlineField(string line, out string title, out string value)
    {
        title = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int separatorIndex = line.IndexOf('：');
        if (separatorIndex < 0)
        {
            separatorIndex = line.IndexOf(':');
        }

        if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
        {
            return false;
        }

        title = line[..separatorIndex].Trim();
        value = line[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(value);
    }

    private static string NormalizeSectionKey(string title)
    {
        return title.Trim().ToLowerInvariant() switch
        {
            "名字" or "名称" or "name" => "name",
            "性别" or "gender" => "gender",
            "说话方式" or "说话风格" or "speech" or "speech_style" => "speech_style",
            "做事方式" or "做事风格" or "工作方式" or "work_style" => "work_style",
            "娱乐方式" or "娱乐风格" or "entertainment_style" => "entertainment_style",
            "爱好" or "hobbies" or "hobby" => "hobbies",
            "讨厌" or "不喜欢" or "dislikes" or "dislike" => "dislikes",
            "喜欢" or "likes" or "like" => "likes",
            "秘密" or "secret" or "secrets" => "secrets",
            "思考方式" or "思考风格" or "thinking_style" or "thinking" => "thinking_style",
            _ => string.Empty
        };
    }

    private static NpcPersonalitySection CreateSection(string title, string key, string content)
    {
        return new NpcPersonalitySection
        {
            Title = title,
            Key = key,
            Content = content,
            Recognized = true
        };
    }

    private static string Prefer(string primary, string fallback)
    {
        return string.IsNullOrWhiteSpace(primary) ? fallback : primary.Trim();
    }

    private string DescribeSpeechStyle(NPC npc)
    {
        string manners = npc.Manners switch
        {
            0 => "说话通常偏随和直接",
            1 => "说话通常礼貌克制",
            2 => "说话常带一点锋利和防备",
            _ => "说话方式以当前关系和情境为准"
        };
        string anxiety = npc.SocialAnxiety >= 2
            ? "，在陌生或敏感话题上会更谨慎，不愿多说"
            : "，面对熟悉话题时通常愿意正常交流";
        return $"{manners}{anxiety}。";
    }

    private string DescribeWorkStyle(NPC npc)
    {
        string baselineMap = string.IsNullOrWhiteSpace(npc.DefaultMap) ? "村子" : npc.DefaultMap;
        return $"平时会优先按照当天日程、地点职责和生活秩序行动，通常不会为了小事随意打断自己在 {baselineMap} 的安排。";
    }

    private string DescribeEntertainmentStyle(NPC npc)
    {
        return npc.Optimism >= 1
            ? "休闲时更愿意做让自己放松或心情变好的事情，整体偏向轻松一些的娱乐。"
            : "休闲方式偏安静克制，不会主动把自己放进太吵闹或太张扬的场合。";
    }

    private string DescribeHobbies(NPC npc)
    {
        if (!string.IsNullOrWhiteSpace(npc.loveInterest))
        {
            return $"会关注与 {npc.loveInterest}、日常生活和个人节奏相关的事情；若没有更多记忆，不要凭空细化。";
        }

        return "围绕自己的日常生活、熟悉地点和既有社交关系安排活动；若没有记忆依据，不要凭空编造具体兴趣清单。";
    }

    private string DescribeLikes(NPC npc)
    {
        string marriageState = npc.isMarried() ? "已婚生活的稳定感" : "熟悉的人际关系和日常节奏";
        return $"通常会偏好 {marriageState}、尊重自己安排的人，以及不会无端打断自己节奏的互动。";
    }

    private string DescribeThinkingStyle(NPC npc)
    {
        string optimism = npc.Optimism switch
        {
            >= 2 => "思考时偏乐观，愿意接受合理的新提议",
            <= -1 => "思考时偏保守，会先考虑风险和麻烦",
            _ => "思考时相对务实，会先判断眼前安排是否会被打乱"
        };
        string anxiety = npc.SocialAnxiety >= 2
            ? "；在不喜欢的人或不确定的话题前，往往会减少思考和额外查询。"
            : "；若情境明确，通常愿意做基本判断。";
        return $"{optimism}{anxiety}";
    }
}
