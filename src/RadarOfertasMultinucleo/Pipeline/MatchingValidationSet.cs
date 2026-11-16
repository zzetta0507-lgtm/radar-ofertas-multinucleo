namespace RadarOfertasMultinucleo.Pipeline;

public sealed record MatchingValidationPair(string Left, string Right, bool ExpectedMatch);

public sealed record MatchingValidationReport(
    int PairCount,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    double Precision,
    double Recall,
    IReadOnlyList<MatchingValidationError> MisclassifiedPairs);

public sealed record MatchingValidationError(
    string Left,
    string Right,
    bool ExpectedMatch,
    bool PredictedMatch,
    double Similarity);

public static class MatchingValidationSet
{
    public static IReadOnlyList<MatchingValidationPair> Pairs { get; } =
    [
        new("Project Aurora", "Project: Aurora", true),
        new("Echoes of Orion II", "Echoes of Orion 2", true),
        new("Neon Harbor Remastered", "Neon Harbor", true),
        new("Café del Sol", "Cafe del Sol", true),
        new("Skybound — Definitive Edition", "Skybound", true),
        new("Iron Horizon Deluxe Edition", "Iron Horizon", true),
        new("Legends of Aster GOTY", "Legends of Aster", true),
        new("Midnight Circuit for PlayStation 5", "Midnight Circuit", true),
        new("Quiet Valley (PC)", "Quiet Valley", true),
        new("Tales: The First Dawn", "Tales The First Dawn", true),
        new("Silver Coast Ultimate Edition", "Silver Coast", true),
        new("Crystal Engine Complete Edition", "Crystal Engine", true),
        new("Garden of Moons III", "Garden of Moons 3", true),
        new("Warden's Oath: Enhanced Edition", "Wardens Oath", true),
        new("Beyond the Pines Steam", "Beyond the Pines", true),
        new("Ash: Ember", "Ash Ember", true),
        new("Nébula: Sector IX", "Nebula Sector 9", true),
        new("Chronicles of Vale — Remaster", "Chronicles of Vale", true),
        new("Brightwater: Game of the Year Edition", "Brightwater", true),
        new("The Long Night PS4", "The Long Night", true),
        new("Redwood Story, The", "Redwood Story The", true),
        new("Clockwork Harbor: Deluxe", "Clockwork Harbor", true),
        new("Winterfall Nintendo Switch", "Winterfall", true),
        new("Tidebreaker: Definitive", "Tidebreaker", true),
        new("Starforge Ultimate", "Starforge", true),
        new("The Glass Kingdom (PC)", "The Glass Kingdom", true),
        new("Alba: A Story Adventure", "Alba A Story Adventure", true),
        new("Frostline Collection Edition", "Frostline Collection", true),
        new("Crown of Ashes VI", "Crown of Ashes 6", true),
        new("Valley of Kings Remaster", "Valley of Kings", true),
        new("Aether Gate II", "Aether Gate III", false),
        new("Blue Lantern 2", "Blue Lantern 3", false),
        new("Crimson Archive IV", "Crimson Archive V", false),
        new("Distant Harbor 7", "Distant Harbor 8", false),
        new("Eternal Orchard VI", "Eternal Orchard VII", false),
        new("Fallen Comet IX", "Fallen Comet X", false),
        new("Golden Compass 2", "Golden Compass 4", false),
        new("Hidden Meridian III", "Hidden Meridian IV", false),
        new("Ivory Tower V", "Ivory Tower VI", false),
        new("Jade Horizon VIII", "Jade Horizon IX", false),
        new("Kingdom of Embers 3", "Kingdom of Embers 5", false),
        new("Last Signal II", "Last Signal IV", false),
        new("Moonlit Passage 6", "Moonlit Passage 7", false),
        new("Northern Crown IV", "Northern Crown VI", false),
        new("Obsidian Trail 8", "Obsidian Trail 9", false),
        new("Paper Kingdom V", "Paper Kingdom VII", false),
        new("Quiet Machine III", "Quiet Machine V", false),
        new("River of Stars 2", "River of Stars 6", false),
        new("Silent Meridian VII", "Silent Meridian VIII", false),
        new("Thunder Vale 4", "Thunder Vale 5", false),
        new("Umbral Coast II", "Umbral Coast V", false),
        new("Violet Engine 3", "Violet Engine 8", false),
        new("Wildwood Saga VI", "Wildwood Saga IX", false),
        new("Xenon Frontier IV", "Xenon Frontier VII", false),
        new("Yellow Sky 5", "Yellow Sky 8", false),
        new("Zenith Valley II", "Zenith Valley VI", false),
        new("Ashen Harbor 3", "Ashen Harbor 7", false),
        new("Bright Crown V", "Bright Crown IX", false),
        new("Cinder Road 4", "Cinder Road 9", false),
        new("Dawn Protocol II", "Dawn Protocol VIII", false),
        new("Ember Archive 6", "Ember Archive X", false)
    ];
}
