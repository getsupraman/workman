namespace Workman.Core.Rendering;

/// <summary>
/// C343 — the one wording of a confirmation by a person, on every text surface. It never reads Passed, ✓ or the
/// name of a runner: a person's word is not a run, and a reader must never mistake one for the other.
/// </summary>
public static class Confirmed
{
    public static string Text(string? person, string? quote)
        => $"Confirmed by {Glyph.Flat(person)}: “{Glyph.Flat(quote)}”";
}
