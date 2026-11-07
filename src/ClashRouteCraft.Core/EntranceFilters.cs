using System.Text.Json.Nodes;
using static ClashRouteCraft.Core.Data;

namespace ClashRouteCraft.Core;

public static class EntranceFilters
{
    public const string Key = "entrance_region";
    const string HongKongMacau = @"香港|港|澳门|澳門|🇭🇰|🇲🇴|Hong\s*Kong|Macao|Macau|(?<![a-z])(?:HK|MO)(?![a-z])";
    const string Nearby = @"日本|东京|大阪|台湾|台灣|台北|新加坡|狮城|獅城|韩国|韓國|首尔|首爾|🇯🇵|🇹🇼|🇸🇬|🇰🇷|Japan|Tokyo|Osaka|Taiwan|Taipei|Singapore|Korea|Seoul|(?<![a-z])(?:JP|TW|SG|KR)(?![a-z])";
    const string UnitedStates = @"美国|美國|洛杉矶|洛杉磯|纽约|紐約|西雅图|西雅圖|圣何塞|聖何塞|达拉斯|達拉斯|🇺🇸|United\s*States|Los\s*Angeles|New\s*York|Seattle|San\s*Jose|Dallas|(?<![a-z])(?:US|USA)(?![a-z])";
    public static string Mode(JsonObject selection)
    {
        string mode = S(selection, Key, "custom");
        if (mode is not ("custom" or "nearby" or "nearby-no-hk-mo" or "us")) throw new BuildException("机场入口地区方案无效，请重新选择");
        return mode;
    }
    public static (string Include, string Exclude) Patterns(JsonObject source, JsonObject selection, string defaultInclude, string defaultExclude = "")
    {
        string mode = Mode(selection);
        string include = mode switch { "nearby" => "(?i)(" + HongKongMacau + "|" + Nearby + ")", "nearby-no-hk-mo" => "(?i)(" + Nearby + ")", "us" => "(?i)(" + UnitedStates + ")", _ => S(source, "include", S(selection, "include", defaultInclude)) };
        string exclude = S(source, "exclude", S(selection, "exclude", defaultExclude));
        if (mode == "nearby-no-hk-mo") exclude = "(?i)(" + HongKongMacau + ")" + (exclude.Length > 0 ? "|(?:" + exclude + ")" : "");
        return (include, exclude);
    }
}
