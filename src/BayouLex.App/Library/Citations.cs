using System.Text.RegularExpressions;

namespace BayouLex.App.Library;

/// <summary>A citation found in document text: the characters it spans and the library citation key it names.</summary>
public readonly record struct CitationSpan(int Start, int Length, string Key);

/// <summary>
/// Louisiana citation handling. Library citation keys are the forms legis.la.gov uses
/// ("RS 9:2800", "CC 2315", "CONST 1 2", "HB1"); people and statutes write them differently.
/// </summary>
public static partial class Citations
{
    private sealed record Code(string Prefix, string Short, string Formal);

    // Codes cited by article number, keyed by library prefix.
    private static readonly Dictionary<string, Code> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CC"] = new("CC", "C.C. art.", "La. Civ. Code art."),
        ["CCP"] = new("CCP", "C.C.P. art.", "La. Code Civ. Proc. art."),
        ["CCRP"] = new("CCRP", "C.Cr.P. art.", "La. Code Crim. Proc. art."),
        ["CE"] = new("CE", "C.E. art.", "La. Code Evid. art."),
        ["CHC"] = new("CHC", "Ch.C. art.", "La. Ch. Code art."),
    };

    private const string Number = @"\d+(?:\.\d+)*[A-Z]?(?![\d:])";
    private const string NumberList = Number + @"(?:(?:\s*,\s*(?:and\s+|or\s+)?|\s+(?:and|or|through)\s+)" + Number + ")*";

    // Explicit code references; the "list" group may hold several article numbers.
    private static readonly (Regex Pattern, string Prefix)[] CodePatterns =
    [
        (new Regex(@"\b(?:La\.?\s*)?Civ(?:il|\.)\s*Code(?:\s*Ann\.)?\s*,?\s*[Aa]rt(?:icles?|s?\.)\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CC"),
        (new Regex(@"\bC\.\s?C\.\s*[Aa]rt(?:icles?|s?\.)\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CC"),
        (new Regex(@"\bCode\s+of\s+Civil\s+Procedure\s*,?\s*[Aa]rticles?\s+(?<list>" + NumberList + ")", RegexOptions.Compiled), "CCP"),
        (new Regex(@"\b(?:La\.?\s*)?Code\s+Civ\.\s*Proc\.(?:\s*Ann\.)?\s*[Aa]rts?\.\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CCP"),
        (new Regex(@"\bC\.\s?C\.\s?P\.\s*[Aa]rt(?:icles?|s?\.)\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CCP"),
        (new Regex(@"\bCode\s+of\s+Criminal\s+Procedure\s*,?\s*[Aa]rticles?\s+(?<list>" + NumberList + ")", RegexOptions.Compiled), "CCRP"),
        (new Regex(@"\b(?:La\.?\s*)?Code\s+Crim\.\s*Proc\.(?:\s*Ann\.)?\s*[Aa]rts?\.\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CCRP"),
        (new Regex(@"\bC\.\s?Cr\.\s?P\.\s*[Aa]rt(?:icles?|s?\.)\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CCRP"),
        (new Regex(@"\bCode\s+of\s+Evidence\s*,?\s*[Aa]rticles?\s+(?<list>" + NumberList + ")", RegexOptions.Compiled), "CE"),
        (new Regex(@"\b(?:La\.?\s*)?Code\s+Evid\.(?:\s*Ann\.)?\s*[Aa]rts?\.\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CE"),
        (new Regex(@"\bC\.\s?E\.\s*[Aa]rt(?:icles?|s?\.)\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CE"),
        (new Regex(@"\bChildren'?s\s+Code\s*,?\s*[Aa]rticles?\s+(?<list>" + NumberList + ")", RegexOptions.Compiled), "CHC"),
        (new Regex(@"\b(?:La\.?\s*)?Ch\.\s?(?:C\.|Code)\s*[Aa]rt(?:icles?|s?\.)\s*(?<list>" + NumberList + ")", RegexOptions.Compiled), "CHC"),
    ];

    public static string? CodePrefix(string citation)
    {
        var prefix = citation.Split(' ', 2)[0];
        return Codes.ContainsKey(prefix) ? prefix.ToUpperInvariant() : null;
    }

    /// <summary>Finds citations in a line of text. <paramref name="currentCitation"/> gives context for bare references.</summary>
    public static IReadOnlyList<CitationSpan> Find(string text, string currentCitation)
    {
        var spans = new List<CitationSpan>();

        void AddList(Match match, Func<string, string> keyFor)
        {
            var list = match.Groups["list"];
            foreach (Match number in NumberInListRegex().Matches(list.Value))
            {
                var start = list.Index + number.Index;
                if (!Overlaps(spans, start, number.Length))
                {
                    // The first number carries the "R.S." / "Article" lead-in so the whole citation is clickable.
                    var linkStart = number.Index == 0 ? match.Index : start;
                    spans.Add(new CitationSpan(linkStart, start + number.Length - linkStart, keyFor(number.Value)));
                }
            }
        }

        foreach (Match match in RevisedStatutesRegex().Matches(text))
        {
            var title = match.Groups["title"].Value;
            AddList(match, number => $"RS {title}:{number}");
        }

        foreach (Match match in ConstitutionRegex().Matches(text))
        {
            AddConstitution(spans, match);
        }
        foreach (Match match in ConstitutionArticleSectionRegex().Matches(text))
        {
            AddConstitution(spans, match);
        }

        foreach (var (pattern, prefix) in CodePatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (!Overlaps(spans, match.Index, match.Length))
                {
                    AddList(match, number => $"{prefix} {number}");
                }
            }
        }

        // Bare "Article 2316" inside a code refers to that same code.
        if (CodePrefix(currentCitation) is { } currentCode)
        {
            foreach (Match match in BareArticleRegex().Matches(text))
            {
                if (!Overlaps(spans, match.Index, match.Length))
                {
                    AddList(match, number => $"{currentCode} {number}");
                }
            }
        }

        // "Section 5 of this Article" inside the constitution.
        if (ConstitutionKeyRegex().Match(currentCitation) is { Success: true } current)
        {
            foreach (Match match in SectionOfThisArticleRegex().Matches(text))
            {
                if (!Overlaps(spans, match.Index, match.Length))
                {
                    spans.Add(new CitationSpan(match.Index, match.Length, $"CONST {current.Groups["article"].Value} {match.Groups["section"].Value}"));
                }
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    /// <summary>Turns what someone typed ("rs 9:2800", "art. 2315", "La. Const. art. I, §2", "hb 1") into candidate keys.</summary>
    public static IReadOnlyList<string> ParseInput(string input, string? currentCitation)
    {
        var text = input.Trim();
        var keys = new List<string>();
        if (text.Length == 0)
        {
            return keys;
        }

        keys.Add(text); // Docket numbers and anything already in library form ("95-C-1998", "CC 2315").
        keys.AddRange(Find(text, currentCitation ?? "").Select(span => span.Key));

        if (DirectKeyRegex().Match(text) is { Success: true } direct)
        {
            keys.Add($"{direct.Groups["prefix"].Value.ToUpperInvariant()} {direct.Groups["rest"].Value}");
        }
        if (BareRsRegex().Match(text) is { Success: true } rs)
        {
            keys.Add($"RS {rs.Groups["title"].Value}:{rs.Groups["section"].Value}");
        }
        if (BillRegex().Match(text) is { Success: true } bill)
        {
            keys.Add($"{bill.Groups["kind"].Value.ToUpperInvariant()}{bill.Groups["number"].Value}");
        }
        if (BareArticleInputRegex().Match(text) is { Success: true } article)
        {
            var prefix = CodePrefix(currentCitation ?? "") ?? "CC";
            keys.Add($"{prefix} {article.Groups["number"].Value}");
        }
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The compact label used in the contents tree ("§2800", "Art. 2315").</summary>
    public static string ShortLabel(string citation)
    {
        var (prefix, rest) = Split(citation);
        if (Codes.ContainsKey(prefix))
        {
            return $"Art. {rest}";
        }
        return prefix switch
        {
            "RS" => "§" + rest[(rest.IndexOf(':') + 1)..],
            "CONST" or "CA" => "§" + rest.Split(' ').Last(),
            "HRULE" or "SRULE" or "JRULE" => $"Rule {rest}",
            _ => citation,
        };
    }

    /// <summary>The everyday form shown in headers, tabs, and lists ("R.S. 9:2800", "C.C. art. 2315").</summary>
    public static string Display(string citation)
    {
        var (prefix, rest) = Split(citation);
        if (Codes.TryGetValue(prefix, out var code))
        {
            return $"{code.Short} {rest}";
        }
        return prefix switch
        {
            "RS" => $"R.S. {rest}",
            "CONST" => ConstitutionDisplay("Const.", rest),
            "CA" => ConstitutionDisplay("Const. Anc.", rest),
            "HRULE" => $"House Rule {rest}",
            "SRULE" => $"Senate Rule {rest}",
            "JRULE" => $"Joint Rule {rest}",
            _ when BillKeyRegex().Match(citation) is { Success: true } bill => $"{bill.Groups["kind"].Value} {bill.Groups["number"].Value}",
            _ => citation,
        };
    }

    /// <summary>The form a brief would use, for Copy Citation.</summary>
    public static string Formal(LibraryDocument doc)
    {
        var (prefix, rest) = Split(doc.Citation);
        if (Codes.TryGetValue(prefix, out var code))
        {
            return $"{code.Formal} {rest}";
        }
        return prefix switch
        {
            "RS" => $"La. R.S. {rest}",
            "CONST" => ConstitutionDisplay("La. Const.", rest).Replace("§", "§ "),
            _ when doc.Category == "Louisiana Legislative Bills" => $"{Display(doc.Citation)}, {doc.SessionId}",
            _ when doc.Category.Contains("Supreme Court") => $"{doc.Title.Replace(" (La. ", ", No. " + doc.Citation + " (La. ")}",
            _ => Display(doc.Citation),
        };
    }

    public static string ToRoman(int value)
    {
        if (value <= 0 || value > 3999)
        {
            return value.ToString();
        }
        (int Value, string Numeral)[] table =
            [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var result = new System.Text.StringBuilder();
        foreach (var (amount, numeral) in table)
        {
            while (value >= amount)
            {
                result.Append(numeral);
                value -= amount;
            }
        }
        return result.ToString();
    }

    private static int FromRoman(string numeral)
    {
        var values = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100 };
        var total = 0;
        for (var i = 0; i < numeral.Length; i++)
        {
            var value = values[char.ToUpperInvariant(numeral[i])];
            var next = i + 1 < numeral.Length ? values[char.ToUpperInvariant(numeral[i + 1])] : 0;
            total += value < next ? -value : value;
        }
        return total;
    }

    private static void AddConstitution(List<CitationSpan> spans, Match match)
    {
        if (Overlaps(spans, match.Index, match.Length))
        {
            return;
        }
        var article = match.Groups["article"].Value;
        var number = int.TryParse(article, out var arabic) ? arabic : FromRoman(article);
        spans.Add(new CitationSpan(match.Index, match.Length, $"CONST {number} {match.Groups["section"].Value}"));
    }

    private static string ConstitutionDisplay(string lead, string rest)
    {
        var parts = rest.Split(' ', 2);
        var article = int.TryParse(parts[0], out var number) ? ToRoman(number) : parts[0];
        return parts.Length == 2 ? $"{lead} art. {article}, §{parts[1]}" : $"{lead} art. {article}";
    }

    private static (string Prefix, string Remainder) Split(string citation)
    {
        var parts = citation.Trim().Split(' ', 2);
        return parts.Length == 2 ? (parts[0].ToUpperInvariant(), parts[1]) : (parts[0].ToUpperInvariant(), "");
    }

    private static bool Overlaps(List<CitationSpan> spans, int start, int length)
        => spans.Any(span => start < span.Start + span.Length && span.Start < start + length);

    [GeneratedRegex(@"\b(?:La\.?\s*)?R\.\s?S\.\s*(?<title>\d+[A-Z]?)\s*:\s*(?<list>" + NumberList + ")")]
    private static partial Regex RevisedStatutesRegex();

    [GeneratedRegex(@"\b(?:La\.?\s*)?Const(?:itution|\.)?(?:\s+of\s+1974)?\s*,?\s*[Aa]rt(?:icle|\.)?\s*(?<article>[IVXL]+|\d+)\s*,?\s*(?:§|[Ss]ec(?:tion|\.))\s*(?<section>\d+(?:\.\d+)?)")]
    private static partial Regex ConstitutionRegex();

    [GeneratedRegex(@"\bArticle\s+(?<article>[IVXL]+)\s*,\s*Section\s+(?<section>\d+(?:\.\d+)?)(?:\([A-Za-z0-9]+\))*(?=\s+of\s+(?:the\s+)?(?:Louisiana\s+)?Constitution)")]
    private static partial Regex ConstitutionArticleSectionRegex();

    [GeneratedRegex(@"\b[Aa]rticles?\s+(?<list>" + NumberList + @")(?!\s+of\s+(?:the|this)\s+(?:Constitution|Code\s+of|Louisiana))")]
    private static partial Regex BareArticleRegex();

    [GeneratedRegex(@"^CONST (?<article>\d+) ")]
    private static partial Regex ConstitutionKeyRegex();

    [GeneratedRegex(@"\bSection\s+(?<section>\d+(?:\.\d+)?)\s+of\s+this\s+Article\b")]
    private static partial Regex SectionOfThisArticleRegex();

    [GeneratedRegex(Number)]
    private static partial Regex NumberInListRegex();

    [GeneratedRegex(@"^(?<prefix>RS|CC|CCP|CCRP|CE|CHC|CONST|CA|HRULE|SRULE|JRULE)\s+(?<rest>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DirectKeyRegex();

    [GeneratedRegex(@"^(?:§\s*)?(?<title>\d+[A-Z]?)\s*:\s*(?<section>\d+(?:\.\d+)*[A-Z]?)$")]
    private static partial Regex BareRsRegex();

    [GeneratedRegex(@"^(?<kind>HB|SB|HCR|SCR|HR|SR|HCSR|SCSR)\s*(?<number>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BillRegex();

    [GeneratedRegex(@"^(?<kind>HB|SB|HCR|SCR|HR|SR|HCSR|SCSR)(?<number>\d+)$")]
    private static partial Regex BillKeyRegex();

    [GeneratedRegex(@"^(?:art(?:icle|\.)?\s*)?(?<number>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex BareArticleInputRegex();
}
