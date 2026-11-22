namespace Boekhouding.Api.Integrations;

/// <summary>Vaste testcatalogus, gedeeld door de seeder en de dummy-koppelingen zodat SKU's overeenkomen.</summary>
public static class DummyCatalog
{
    public record Item(string Sku, string Title, string Vendor, string ProductType, decimal Price, int WeightGrams);

    public static readonly Item[] Items =
    [
        new("PKM-SV-BP",       "Pokémon Scarlet & Violet Booster Pack",        "Pokémon",  "Booster pack",  5.99m,   20),
        new("PKM-151-ETB",     "Pokémon 151 Elite Trainer Box",                 "Pokémon",  "Elite Trainer Box", 64.99m, 900),
        new("PKM-PE-BUNDLE",   "Pokémon Prismatic Evolutions Booster Bundle",   "Pokémon",  "Booster bundle", 39.99m, 300),
        new("PKM-CHAR-UPC",    "Pokémon Charizard Ultra Premium Collection",    "Pokémon",  "Collection box", 159.99m, 1800),
        new("PKM-SINGLE-MIX",  "Pokémon mystery single (holo of beter)",       "Pokémon",  "Single",         9.99m,   10),
        new("PKM-VINTAGE-LOT", "Vintage Pokémon kaartenlot (50 st.)",           "Pokémon",  "Lot",            24.99m,  150),
        new("OP-OP09-BOX",     "One Piece OP-09 Booster Box",                   "Bandai",   "Booster box",    129.99m, 700),
        new("OP-OP09-BP",      "One Piece OP-09 Booster Pack",                  "Bandai",   "Booster pack",   6.49m,   20),
        new("YGO-RA03-BOX",    "Yu-Gi-Oh! Rarity Collection 3 Booster Box",     "Konami",   "Booster box",    74.99m,  500),
        new("LORC-BP",         "Disney Lorcana Booster Pack",                   "Ravensburger", "Booster pack", 6.99m, 20),
        new("ACC-TOPLOAD-25",  "Toploaders 25 st.",                             "Ultra Pro", "Accessoire",    4.99m,   120),
        new("ACC-PENNY-100",   "Penny sleeves 100 st.",                         "Ultra Pro", "Accessoire",    2.49m,   60),
    ];

    public static readonly string[] CustomerNames =
    [
        "Jens Peeters", "Lotte Janssens", "Milan Maes", "Emma Jacobs", "Noah Willems", "Lina Claes",
        "Arthur Goossens", "Nora Wouters", "Louis De Smet", "Julie Mertens", "Finn Dubois", "Elise Lambert",
        "Vic Hermans", "Ella Vermeulen", "Seppe Desmet", "Fien Pauwels", "Mats Martens", "Hanne Aerts",
    ];
}
