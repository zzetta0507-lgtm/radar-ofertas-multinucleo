using System.Text.RegularExpressions;

namespace RadarOfertasMultinucleo.Domain;

public static class GameTypeClassifier
{
    private static readonly Regex OtherTitleRegex = new(
        """\b(soundtrack|artbook|demo)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DlcTitleRegex = new(
        """\b(DLC|expansion|season\s+pass)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GameWithDlcTitleRegex = new(
        """\b(GOTY|game\s+of\s+the\s+year|deluxe|complete|definitive|gold\s+edition|ultimate|bundle)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GameType Classify(string title, IReadOnlyList<string> knownDlcTitles)
    {
        if (OtherTitleRegex.IsMatch(title))
        {
            return GameType.Other;
        }

        if (DlcTitleRegex.IsMatch(title) ||
            knownDlcTitles.Any(keyword =>
                keyword.Length > 0 && title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return GameType.Dlc;
        }

        return GameWithDlcTitleRegex.IsMatch(title)
            ? GameType.GameWithDlc
            : GameType.Game;
    }
}
