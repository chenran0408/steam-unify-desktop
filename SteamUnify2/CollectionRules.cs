using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamUnify;

internal sealed class CollectionRules
{
    // Labels that describe production, player count, or a modifier stay searchable tags.
    internal static string? Map(string label) => label switch
    {
        "动作" or "射击" or "平台跳跃" => "动作与射击",
        "角色扮演" => "角色扮演",
        "冒险" or "视觉小说" or "解谜" => "剧情与解谜",
        "策略" or "模拟经营" or "卡牌" => "策略与经营",
        "沙盒生存" => "生存与沙盒",
        "竞速" or "体育" => "竞速与体育",
        "音乐节奏" or "休闲" => "休闲",
        _ when Suggestions.Groups.Contains(label) => label,
        _ => null
    };

    private readonly Dictionary<string, Dictionary<string, double>> tagWeights;
    private readonly Dictionary<string, Dictionary<string, double>> genreWeights;
    private readonly Dictionary<string, string[]> facets;
    private readonly Dictionary<string, double> caps;
    private readonly Dictionary<string, int> minimums;
    private readonly HashSet<string> inFacet;
    private readonly Dictionary<string, Regex[]> descriptionWords;
    private readonly Dictionary<string, Regex[]> titleWords;

    internal CollectionRules(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        T Read<T>(string field) => root.GetProperty(field).Deserialize<T>()!;
        tagWeights = new(Read<Dictionary<string, Dictionary<string, double>>>("tag_weights"), StringComparer.OrdinalIgnoreCase);
        genreWeights = Read<Dictionary<string, Dictionary<string, double>>>("genre_weights");
        facets = Read<Dictionary<string, string[]>>("tag_facets");
        caps = Read<Dictionary<string, double>>("facet_caps");
        minimums = Read<Dictionary<string, int>>("facet_min_tags");
        inFacet = facets.Values.SelectMany(x => x).ToHashSet(StringComparer.OrdinalIgnoreCase);
        descriptionWords = MakeKeywords(Read<Dictionary<string, string[]>>("description_keywords"));
        titleWords = MakeKeywords(Read<Dictionary<string, string[]>>("name_keywords"));
    }

    private static Dictionary<string, Regex[]> MakeKeywords(Dictionary<string,string[]> fields) =>
        fields.ToDictionary(pair => pair.Key, pair => pair.Value.Select(word =>
        {
            string escaped = Regex.Escape(word);
            string pattern = word.Any(ch => ch >= '\u3400' && ch <= '\u9fff') ? escaped : $@"(?<![a-z0-9]){escaped}(?![a-z0-9])";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }).ToArray());

    internal string Classify(CacheGame game, HashSet<string> tags, string? description)
    {
        var scores = new Dictionary<string,double>();
        void Add(string label, double score)
        {
            if (Map(label) is string group)
                scores[group] = scores.GetValueOrDefault(group) + score;
        }

        foreach (int genre in game.Genres.Distinct())
            if (genreWeights.TryGetValue(genre.ToString(), out var weights))
                foreach (var (label, weight) in weights)
                    Add(label, weight);

        foreach (var (facet, related) in facets)
        {
            var matched = related.Where(tags.Contains).ToArray();
            if (matched.Length < minimums.GetValueOrDefault(facet, 1))
                continue;
            var weights = matched.Where(tagWeights.ContainsKey).SelectMany(t => tagWeights[t]).GroupBy(pair => pair.Key);
            foreach (var signal in weights)
            {
                var values = signal.Select(value => value.Value).OrderByDescending(x => x).Take(2).ToArray();
                double score = values[0] + (values.Length > 1 ? values[1] / 2 : 0);
                Add(signal.Key, Math.Min(score, caps.GetValueOrDefault(facet, 6)));
            }
        }

        foreach (string tag in tags.Where(t => !inFacet.Contains(t)))
            if (tagWeights.TryGetValue(tag, out var weights))
                foreach (var (label, weight) in weights)
                    Add(label, weight);

        void AddKeywords(string? text, Dictionary<string,Regex[]> patterns, double unit)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var (label, words) in patterns)
                Add(label, Math.Min(2, words.Count(pattern => pattern.IsMatch(text))) * unit);
        }
        AddKeywords(description, descriptionWords, 1.5);
        AddKeywords(game.Name, titleWords, 0.75);
        var best = scores.OrderByDescending(pair => pair.Value).Take(2).ToArray();
        if (best.Length == 0 || best[0].Value < 2 ||
            (best.Length == 2 && best[0].Value - best[1].Value < 0.5))
            return "";
        return best[0].Key;
    }
}
