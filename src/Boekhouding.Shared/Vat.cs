namespace Boekhouding.Shared;

/// <summary>
/// Btw-rekenregels. Verkoopprijzen in de webshop zijn altijd INCLUSIEF btw.
/// </summary>
public static class Vat
{
    public const decimal StandardRate = 0.21m;

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Btw die vervat zit in een bedrag incl. btw (bv. 121 → 21).</summary>
    public static decimal FromInclusive(decimal grossAmount, decimal rate = StandardRate)
        => Round(grossAmount * rate / (1 + rate));

    /// <summary>Btw bovenop een bedrag excl. btw (bv. 100 → 21).</summary>
    public static decimal OnExclusive(decimal netAmount, decimal rate = StandardRate)
        => Round(netAmount * rate);

    /// <summary>
    /// Margeregeling (per stuk): de winstmarge is een bedrag INCLUSIEF btw.
    /// Verkoop 100, aankoop 80 → marge 20 → btw = 20 × 21/121 = 3,47.
    /// Een negatieve marge levert 0 btw op (en mag niet verrekend worden met andere stukken).
    /// </summary>
    public static decimal OnMargin(decimal salePriceIncl, decimal purchaseCost)
    {
        var margin = salePriceIncl - purchaseCost;
        return margin <= 0 ? 0m : FromInclusive(margin);
    }
}
