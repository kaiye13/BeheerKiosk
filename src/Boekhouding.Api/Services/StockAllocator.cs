using Boekhouding.Api.Data;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>
/// Wijst verkochte stuks toe aan voorraadpartijen:
/// eerst margegoederen (oudste eerst), daarna factuurgoederen (oudste eerst).
/// Per toewijzing wordt meteen de verschuldigde btw vastgelegd.
/// </summary>
public class StockAllocator(AppDbContext db)
{
    public async Task AllocateAsync(SalesOrderLine line, CancellationToken ct = default)
    {
        line.UnallocatedQuantity = line.Quantity - line.Allocations.Sum(a => a.Quantity);
        if (line.ProductId is null || line.UnallocatedQuantity <= 0) return;

        // De DB-filter ziet nog niet-bewaarde wijzigingen niet; daarom in geheugen opnieuw filteren.
        var lots = (await db.StockLots
                .Where(l => l.ProductId == line.ProductId && l.QuantityRemaining > 0)
                .ToListAsync(ct))
            .Where(l => l.QuantityRemaining > 0)
            .OrderBy(l => l.Regime == VatRegime.Margin ? 0 : 1)
            .ThenBy(l => l.PurchaseDate)
            .ThenBy(l => l.Id);

        foreach (var lot in lots)
        {
            if (line.UnallocatedQuantity == 0) break;
            var take = Math.Min(lot.QuantityRemaining, line.UnallocatedQuantity);
            lot.QuantityRemaining -= take;
            line.UnallocatedQuantity -= take;
            line.Allocations.Add(new SaleAllocation
            {
                StockLot = lot,
                Quantity = take,
                Regime = lot.Regime,
                UnitCost = lot.UnitCost,
                UnitPriceIncl = line.UnitPriceIncl,
                VatAmount = CalculateVat(lot.Regime, line.UnitPriceIncl, lot.UnitCost, take),
            });
        }
    }

    public static decimal CalculateVat(VatRegime regime, decimal unitPriceIncl, decimal unitCost, int quantity) =>
        regime == VatRegime.Margin
            ? Vat.OnMargin(unitPriceIncl * quantity, unitCost * quantity)
            : Vat.FromInclusive(unitPriceIncl * quantity);

    /// <summary>
    /// Na het invoeren van nieuwe voorraad: verkopen die eerder zonder voorraad bleven, alsnog toewijzen.
    /// Enkel orders vanaf de maand van aankoop, zodat al afgesloten maanden niet wijzigen.
    /// </summary>
    public async Task<int> AllocatePendingAsync(Product product, DateTime purchaseDate, CancellationToken ct = default)
    {
        var fromMonth = new DateTime(purchaseDate.Year, purchaseDate.Month, 1);
        // Ook orderlijnen met dezelfde SKU waarvoor het product toen nog niet bestond (verkocht vóór invoer).
        var pending = await db.SalesOrderLines
            .Include(l => l.SalesOrder)
            .Include(l => l.Allocations)
            .Where(l => (l.ProductId == product.Id || (l.ProductId == null && l.Sku == product.Sku))
                        && l.UnallocatedQuantity > 0
                        && !l.SalesOrder.IsHistorical && l.SalesOrder.OrderDate >= fromMonth)
            .OrderBy(l => l.SalesOrder.OrderDate)
            .ToListAsync(ct);

        foreach (var line in pending)
        {
            line.ProductId = product.Id;
            await AllocateAsync(line, ct);
        }
        return pending.Count;
    }
}
