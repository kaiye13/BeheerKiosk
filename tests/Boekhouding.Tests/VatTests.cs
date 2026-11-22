using Boekhouding.Shared;

namespace Boekhouding.Tests;

public class VatTests
{
    [Fact]
    public void Margin_vat_is_21_over_121_of_the_margin()
    {
        // Voorbeeld uit de vereisten: verkoop 100, aankoop 80 → marge 20 → btw 3,47 (niet 4,20).
        Assert.Equal(3.47m, Vat.OnMargin(100m, 80m));
    }

    [Fact]
    public void Negative_margin_gives_no_vat()
    {
        Assert.Equal(0m, Vat.OnMargin(50m, 80m));
        Assert.Equal(0m, Vat.OnMargin(80m, 80m));
    }

    [Theory]
    [InlineData(121, 21)]
    [InlineData(5.99, 1.04)]
    [InlineData(64.99, 11.28)]
    public void Vat_from_inclusive_price(decimal gross, decimal expected) =>
        Assert.Equal(expected, Vat.FromInclusive(gross));

    [Fact]
    public void Vat_on_exclusive_amount() => Assert.Equal(21m, Vat.OnExclusive(100m));
}
