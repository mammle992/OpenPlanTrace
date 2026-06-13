namespace OpenPlanTrace;

public static class VisualAiCategoryMapper
{
    private static readonly IReadOnlyDictionary<ObjectCategory, string[]> Hints =
        new Dictionary<ObjectCategory, string[]>
        {
            [ObjectCategory.Stair] = new[] { "stair", "stairs", "staircase", "trapp", "step" },
            [ObjectCategory.Elevator] = new[] { "elevator", "lift", "heis" },
            [ObjectCategory.Column] = new[] { "column", "pillar", "post" },
            [ObjectCategory.Shaft] = new[] { "shaft", "riser" },
            [ObjectCategory.PlumbingFixture] = new[] { "toilet", "wc", "sink", "lav", "basin", "shower", "bath", "urinal" },
            [ObjectCategory.ElectricalDevice] = new[] { "electrical", "outlet", "socket", "switch", "panel", "panelboard", "mccb" },
            [ObjectCategory.Lighting] = new[] { "light", "lighting", "lamp", "fixture", "luminaire", "downlight" },
            [ObjectCategory.HVACEquipment] = new[] { "hvac", "ahu", "vav", "vent", "diffuser", "duct", "fan", "grille", "damper" },
            [ObjectCategory.FireSafety] = new[] { "fire", "sprinkler", "alarm", "extinguisher", "hose", "hydrant", "smoke" },
            [ObjectCategory.Equipment] = new[] { "equipment", "machine", "pump", "valve", "tank", "compressor", "motor", "process", "vessel" },
            [ObjectCategory.Vehicle] = new[] { "vehicle", "car", "auto", "automobile", "parking", "garage", "garasje", "bil", "parkering" },
            [ObjectCategory.Furniture] = new[] { "desk", "chair", "table", "sofa", "bed", "cabinet", "furniture", "island" },
            [ObjectCategory.Structural] = new[] { "beam", "brace", "steel", "concrete", "structural" },
            [ObjectCategory.Fixture] = new[] { "fixture", "casework", "kitchen", "counter", "appliance" }
        };

    public static ObjectCategory MapLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return ObjectCategory.Unknown;
        }

        var normalized = Normalize(label);
        foreach (var (category, hints) in Hints)
        {
            if (hints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                return category;
            }
        }

        return ObjectCategory.Unknown;
    }

    public static ObjectCandidateKind KindFor(ObjectCategory category) =>
        category switch
        {
            ObjectCategory.TextLabel => ObjectCandidateKind.TextLabel,
            ObjectCategory.Furniture => ObjectCandidateKind.Furniture,
            ObjectCategory.Vehicle => ObjectCandidateKind.Vehicle,
            ObjectCategory.Stair => ObjectCandidateKind.Stair,
            ObjectCategory.GenericSymbol
                or ObjectCategory.Elevator
                or ObjectCategory.Column
                or ObjectCategory.Shaft
                or ObjectCategory.Equipment
                or ObjectCategory.ElectricalDevice
                or ObjectCategory.Lighting
                or ObjectCategory.HVACEquipment
                or ObjectCategory.FireSafety
                or ObjectCategory.Structural => ObjectCandidateKind.Symbol,
            ObjectCategory.Fixture
                or ObjectCategory.PlumbingFixture => ObjectCandidateKind.Fixture,
            _ => ObjectCandidateKind.Unknown
        };

    private static string Normalize(string value) =>
        value.Trim().Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
}
