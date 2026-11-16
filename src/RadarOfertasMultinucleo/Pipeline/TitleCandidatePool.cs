using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RadarOfertasMultinucleo.Domain;

namespace RadarOfertasMultinucleo.Pipeline;

public sealed class TitleCandidatePool
{
    private static readonly Regex GamePathRegex = new(
        "^/game/(?<id>[0-9]+)/(?<slug>[^/]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EditionRegex = new(
        """\b(remaster(?:ed)?|remake|definitive|deluxe|goty|complete|ultimate|collection)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DlcOrBundleRegex = new(
        """\b(dlc|expansion|season pass|bundle|pack)\b""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RomanNumeralRegex = new(
        """(?<![\p{L}\d])(?:II|III|IV|V|VI|VII|VIII|IX|X)(?![\p{L}\d])""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private TitleMatchCandidate[] _candidates = Array.Empty<TitleMatchCandidate>();

    public void ReplaceFromSitemapUrls(IEnumerable<string> urls, int maximumCandidates)
    {
        if (maximumCandidates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        _candidates = urls
            .Select(CreateCandidate)
            .Where(candidate => candidate is not null)
            .Cast<TitleMatchCandidate>()
            .DistinctBy(candidate => candidate.GameId)
            .Take(maximumCandidates)
            .ToArray();
    }

    public IReadOnlyList<TitleMatchCandidate> Snapshot() => Volatile.Read(ref _candidates);

    private static TitleMatchCandidate? CreateCandidate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var match = GamePathRegex.Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups["id"].Value;
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            Uri.UnescapeDataString(match.Groups["slug"].Value.Replace('-', ' ')).Trim());
        if (title.Length == 0)
        {
            return null;
        }

        var tags = new List<string>();
        if (EditionRegex.IsMatch(title))
        {
            tags.Add("edicion_o_remaster");
        }

        if (RomanNumeralRegex.IsMatch(title))
        {
            tags.Add("numero_romano");
        }

        if (DlcOrBundleRegex.IsMatch(title))
        {
            tags.Add("dlc_o_bundle");
        }

        return new TitleMatchCandidate(
            StableGuid("opencritic:" + id),
            title,
            tags);
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
