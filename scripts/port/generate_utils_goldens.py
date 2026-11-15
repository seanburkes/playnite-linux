#!/usr/bin/env python3
"""Run the frozen v10 helper bodies on .NET 11; never compile the port into the oracle.

Invocation: python3 scripts/port/generate_utils_goldens.py --output /tmp/opencode/utils-v10.json
Only --output is written in the repository if explicitly requested. Build scratch is outside it.
The dependency shims provide deterministic clock/localization/settings and WPF's UnsetValue;
they do not implement any helper under test. This is source-derived evidence, not a claim
to have run the Windows/.NET Framework application or its Windows globalization tables.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "legacy/source"
FILES = ["Playnite/Common/Extensions/StringExtensions.cs", "Playnite/Common/Extensions/Enums.cs",
         "Playnite/DateTimes.cs", "Playnite/Converters/NullableDateToStringConverter.cs",
         "Playnite/Converters/PlayTimeToStringConverter.cs"]


def cases():
    result = []

    def add(op, *args, culture="en-US"):
        result.append(dict(op=op, args=list(args), culture=culture))

    for value in [None, "", " ", "\t\n", "x"]:
        add("md5", value)
        add("empty", value)
        add("whitespace", value)
        add("trademarks", value, "")
        add("normalize", value)
        add("http", value)
        add("uri", value)
        add("url", value)
        add("any", value, "[]")
    add("md5", "é😀")
    for a, b in [("file.gz.gz", ".gz"), ("file.GZ", ".gz"), ("file", ""), ("", ""),
                 ("file", "x"), (None, "x"), ("file", None)]:
        add("trim", a, b, "Ordinal")
        add("trim", a, b, "OrdinalIgnoreCase")
    for value in ["Game™®©", "™", "A[B](C)", "[Only]", "(Only)", "Game, The", "Game, the",
                  "  game.__:  sub ’title’ [tag] (edition)  ", "[a] (b)"]:
        add("normalize", value)
        add("trademarks", value, "$&!")
    for a, b in [("AB", "Alpha Beta Gamma"), ("AB", "Alpha\tBeta"), ("AB", "Alpha-Beta"),
                 ("A", "Alpha"), ("A!", "Alpha Beta"), (None, "Alpha Beta"), ("AB", None),
                 ("AB", "alpha beta"), ("AG", "Alpha Beta Gamma")]:
        add("acronym", a, b)
    for a, b in [(None, "x"), (None, None), ("", ""), ("abc", "B"), ("abc", None)]:
        add("contains", a, b)
    add("any", "abc", None)
    for value in ["https://", "HTTP://x", "https://not a host", "ftp://host/file", " https://host",
                  "/path a/é?q=a b&x=+%2F#frag", "a+b", "a%20b", "https://例.test/é"]:
        add("http", value)
        add("uri", value)
        add("url", value)
    for a, b, c in [("aAa", "a", "A"), ("aaa", "aa", "x"), ("abc", "b", None),
                     ("", None, "x"), ("", "", "x"), ("abc", "", "x"), (None, "a", "x"),
                     ("abc", None, "x"), ("abc", "z", "x")]:
        add("replace", a, b, c)
    for a, b in [("Café-Game", "cafe game"), ("Istanbul", "istanbul"), ("", ""),
                 (None, "x"), ("x", None), ("ab", "b")]:
        add("invariant", a, b)
    for a, b in [("", ""), ("", "abc"), ("abc", ""), ("hello", "HELLO"), ("kitten", "sitting"),
                 ("kiwi", "banana"), ("MARTHA", "MARHTA"), ("ABCD", "ACBD"), (None, "x"), ("x", None)]:
        add("levenshtein", a, b)
        add("jaro", a, b, "0.7")
    add("jaro", "MARTHA", "MARHTA", "1")
    for culture in ["en-US", "fr-FR", "tr-TR"]:
        add("format", "{0:N2}", "1234.5", culture=culture)
        add("format", "{", "1", culture=culture)
    for value in [None, "", " ", "2024-02-29", "2023-02-29", "2024-2-29", "2024-02-29 ",
                  "0001-01-01", "9999-12-31", "10000-01-01", "2024-13-01", "nonsense"]:
        add("parseDate", value, "yyyy-MM-dd")
    add("parseDate", "31 décembre 2024", "dd MMMM yyyy", culture="fr-FR")
    add("parseDate", "31 December 2024", "dd MMMM yyyy", culture="fr-FR")
    add("parseDate", "12/31/2024", "MM/dd/yyyy", culture="fr-FR")
    for culture in ["en-US", "fr-FR", "tr-TR"]:
        for day in [15, 14, 13, 9, 8, 16]:
            add("date", f"2024-05-{day:02}T12:30:00", "2024-05-15", None, "true", culture=culture)
        for fmt in [None, "", "D", "yyyy-MM-dd", "%", "Q"]:
            add("date", "2024-05-15", "2024-05-15", fmt, "false", culture=culture)
    # The explicit UmAlQura calendar keeps the out-of-calendar fallback stable across ICU
    # versions; the ambient ar-SA default calendar differs between ICU builds.
    add("date", "0001-01-01", "2024-05-15", "d", "false", culture="ar-SA-umalqura")
    for culture in ["en-US", "fr-FR"]:
        for value in [None, "0", "1", "59", "60", "3599", "3600", "86399", "86400", "90061", "18446744073709551615"]:
            for days in ["false", "true"]:
                add("playtime", value, days, culture=culture)
    for value in ["None", "One", "Two", "Both", "One, Two", "Four", "One, Four", "5", "8", "7", "-1", "one", "",
                  "  One  ", "2147483648", "Unknown"]:
        add("enum", value)
    return result


SHIMS = r'''
using System;
using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using System.Collections.Generic;
public static class Localization
{
    public static string Get(string key) => key switch {
        "LOCPlayedNone" => "none", "LOCPlayedNoneString" => "unused",
        "LOCPlayedSeconds" => "{0:N0} seconds", "LOCPlayedMinutes" => "{0:N0} minutes",
        "LOCPlayedHours" => "{0:N0} hours {1:N0} minutes",
        "LOCPlayedDays" => "{0:N0} days {1:N0} hours {2:N0} minutes", _ => "localized:" + key };
}
namespace Playnite.SDK { public static class ResourceProvider { public static string GetString(string key) => Localization.Get(key); } }
namespace Playnite.SDK.Models { public class ReleaseDate { public int Year; public int? Month; public int? Day; public DateTime Date; } }
namespace Playnite.Common { public static class Constants { public static string ParsePattern; public static string DateUiFormat => ParsePattern ?? CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern; public const string DefaultPartialReleaseDateTimeFormat = "y"; } }
namespace Playnite {
    public class DateFormattingOptions { public bool PastWeekRelativeFormat; public string Format; }
    public class ReleaseDateFormattingOptions : DateFormattingOptions { public string PartialFormat; }
    public static class LOC {
        public const string Today="LOCToday", Yesterday="LOCYesterday", Sunday="LOCSunday", Monday="LOCMonday", Tuesday="LOCTuesday", Wednesday="LOCWednesday", Thursday="LOCThursday", Friday="LOCFriday", Saturday="LOCSaturday";
    }
}
namespace System { public static class LocalizedShim { public static string GetLocalized(this string value) => Localization.Get(value); } }
public static class DependencyProperty { public static readonly object UnsetValue = new(); }
namespace Playnite.Converters { public abstract class MarkupExtension { public abstract object ProvideValue(IServiceProvider provider); } public interface IValueConverter {} }
[Flags] public enum Sample { None=0, [Description("literal")] One=1, [Description("LOCtwo")] Two=2, Both=3, Four=4 }
public sealed class FixedTime : Playnite.DateTimes.IDateTimes { public DateTime Now {get;set;} public DateTime Today => Now; }
public sealed class Case { public string op {get;set;} public string[] args {get;set;} public string culture {get;set;} public string expected {get;set;} }
public static class Program
{
    // Pin the out-of-calendar fallback independently of which default calendar an ICU build
    // assigns to ar-SA; the legacy body reads only the supplied culture.
    public static CultureInfo MakeCulture(string name)
    {
        if (name == "ar-SA-umalqura")
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
            culture.DateTimeFormat.Calendar = new UmAlQuraCalendar();
            return culture;
        }
        return CultureInfo.GetCultureInfo(name);
    }
    public static void Main(string[] args)
    {
        var cases = JsonSerializer.Deserialize<List<Case>>(System.IO.File.ReadAllText(args[0]));
        foreach(var item in cases) {
            CultureInfo.CurrentCulture = MakeCulture(item.culture);
            try { item.expected = Run(item); } catch(Exception e) { item.expected = "!" + e.GetType().Name; }
        }
        System.IO.File.WriteAllText(args[1], JsonSerializer.Serialize(cases, new JsonSerializerOptions {WriteIndented=true}));
    }
    private static string Run(Case c)
    {
        var a = c.args;
        switch(c.op) {
            case "md5": return StringExtensions.MD5(a[0]);
            case "empty": return StringExtensions.IsNullOrEmpty(a[0]).ToString();
            case "whitespace": return StringExtensions.IsNullOrWhiteSpace(a[0]).ToString();
            case "trademarks": return StringExtensions.RemoveTrademarks(a[0],a[1]);
            case "normalize": return StringExtensions.NormalizeGameName(a[0]);
            case "trim": return StringExtensions.TrimEndString(a[0],a[1],Enum.Parse<StringComparison>(a[2]));
            case "acronym": return StringExtensions.IsStartOfStringAcronym(a[0],a[1]).ToString();
            case "contains": return StringExtensions.Contains(a[0],a[1],StringComparison.OrdinalIgnoreCase).ToString();
            case "any": return StringExtensions.ContainsAny(a[0],a[1]?.ToCharArray()).ToString();
            case "http": return StringExtensions.IsHttpUrl(a[0]).ToString();
            case "uri": return StringExtensions.IsUri(a[0]).ToString();
            case "url": return StringExtensions.UrlEncode(a[0]);
            case "replace": return StringExtensions.Replace(a[0],a[1],a[2],StringComparison.OrdinalIgnoreCase);
            case "invariant": return StringExtensions.ContainsInvariantCulture(a[0],a[1],CompareOptions.IgnoreCase|CompareOptions.IgnoreNonSpace|CompareOptions.IgnoreSymbols).ToString();
            case "levenshtein": return StringExtensions.GetLevenshteinDistanceIgnoreCase(a[0],a[1]).ToString(CultureInfo.InvariantCulture);
            case "jaro": return StringExtensions.GetJaroWinklerSimilarityIgnoreCase(a[0],a[1],double.Parse(a[2],CultureInfo.InvariantCulture)).ToString("R",CultureInfo.InvariantCulture);
            case "format": return StringExtensions.Format(a[0],decimal.Parse(a[1],CultureInfo.InvariantCulture));
            case "enum": return EnumExtensions.GetDescription(Enum.Parse<Sample>(a[0]));
            case "playtime": return (string)new Playnite.Converters.PlayTimeToStringConverter().Convert(a[0]==null ? null : (object)ulong.Parse(a[0],CultureInfo.InvariantCulture),null,bool.Parse(a[1]),null);
            case "date":
                using(Playnite.DateTimes.UseCustomDates(new FixedTime {Now=DateTime.Parse(a[1],CultureInfo.InvariantCulture)}))
                    return Playnite.DateTimes.ToDisplayString(DateTime.Parse(a[0],CultureInfo.InvariantCulture),new Playnite.DateFormattingOptions {Format=a[2], PastWeekRelativeFormat=bool.Parse(a[3])});
            case "parseDate":
                // The legacy converter obtains its pattern from Constants; supply the case's pattern.
                Playnite.Common.Constants.ParsePattern = a[1];
                try {
                    var parsed = new Playnite.Converters.NullableDateToStringConverter().ConvertBack(a[0],null,null,null);
                    return parsed == null ? "empty" : ReferenceEquals(parsed,DependencyProperty.UnsetValue) ? "invalid" : ((DateTime)parsed).ToString("O",CultureInfo.InvariantCulture);
                } finally { Playnite.Common.Constants.ParsePattern = null; }
            default: throw new ArgumentException(c.op);
        }
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="utils-oracle-", dir="/tmp/opencode") as temp:
        scratch = Path(temp)
        links = "".join(f'<Compile Include="{SOURCE / name}" />' for name in FILES[:3])
        (scratch / "Oracle.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><EnableDefaultCompileItems>true</EnableDefaultCompileItems><Nullable>disable</Nullable></PropertyGroup><ItemGroup>' + links + '</ItemGroup></Project>')
        # Only the playtime converter needs its WPF using directives removed; the body is unchanged.
        playtime = (SOURCE / FILES[4]).read_text()
        playtime = "\n".join(line for line in playtime.splitlines() if not line.startswith("using System.Windows"))
        (scratch / "PlayTimeToStringConverter.cs").write_text(playtime)
        nullable = (SOURCE / FILES[3]).read_text()
        start = nullable.index("    public class NullableDateToStringConverter")
        end = nullable.index("    public class ReleaseDateToStringConverter")
        # The first converter class is complete, including the original ConvertBack body.
        (scratch / "NullableDateToStringConverter.cs").write_text(
            "using System; using System.Globalization; namespace Playnite.Converters {\n" + nullable[start:end] + "\n}")
        (scratch / "Program.cs").write_text(SHIMS)
        (scratch / "inputs.json").write_text(json.dumps(cases(), ensure_ascii=False))
        subprocess.run(["dotnet", "run", "--project", str(scratch / "Oracle.csproj"), "--", str(scratch / "inputs.json"), str(scratch / "results.json")], check=True)
        document = dict(provenance={
            "invocation": "python3 scripts/port/generate_utils_goldens.py --output /tmp/opencode/utils-v10.json",
            "runtime": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
            "rule": "Frozen v10 source on .NET 11/ICU; deterministic localization and clock shims. Original NullableDateToStringConverter.ConvertBack with case-supplied Constants.DateUiFormat; WPF empty/unset mapped to empty/invalid.",
            "sources": {name: hashlib.sha256((SOURCE / name).read_bytes()).hexdigest() for name in FILES}},
            cases=json.loads((scratch / "results.json").read_text()))
        args.output.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n")
        print(f"Wrote {len(document['cases'])} v10-derived cases to {args.output}")


if __name__ == "__main__":
    main()
