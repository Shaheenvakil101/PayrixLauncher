using System.Text.Json.Serialization;

namespace PayrixLauncher.Models;

public class TransactionItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("modified")]
    public string? Modified { get; set; }

    [JsonPropertyName("creator")]
    public string? Creator { get; set; }

    [JsonPropertyName("modifier")]
    public string? Modifier { get; set; }

    [JsonPropertyName("txn")]
    public string? Txn { get; set; }

    [JsonPropertyName("item")]
    public string? Item { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("custom")]
    public string? Custom { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("inactive")]
    public int? Inactive { get; set; }

    [JsonPropertyName("frozen")]
    public int? Frozen { get; set; }

    [JsonPropertyName("um")]
    public string? Um { get; set; }

    [JsonPropertyName("commodityCode")]
    public string? CommodityCode { get; set; }

    [JsonPropertyName("total")]
    public decimal? Total { get; set; }

    [JsonPropertyName("discount")]
    public decimal? Discount { get; set; }

    [JsonPropertyName("productCode")]
    public string? ProductCode { get; set; }

    [JsonPropertyName("discountTreatment")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? DiscountTreatment { get; set; }

    // ── Level 2 / Level 3 enhanced data fields ────────────────────────────────

    /// <summary>Sequence number of this line item within the transaction (1-based).</summary>
    [JsonPropertyName("itemSequenceNumber")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? ItemSequenceNumber { get; set; }

    /// <summary>Tax amount for this line item, in cents.</summary>
    [JsonPropertyName("taxAmount")]
    public decimal? TaxAmount { get; set; }

    /// <summary>Tax rate applied to this line item (e.g. 8.25 for 8.25%).</summary>
    [JsonPropertyName("taxRate")]
    public decimal? TaxRate { get; set; }

    /// <summary>1 = tax is already included in the line total; 0 = tax is additional.</summary>
    [JsonPropertyName("taxIncludedInTotal")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? TaxIncludedInTotal { get; set; }

    /// <summary>
    /// Worldpay CNP lineItemDetailIndicator — describes the fulfilment record type for this line item.
    /// <list type="bullet">
    ///   <item>0 — Normal line item detail record</item>
    ///   <item>1 — Normal last line item detail record</item>
    ///   <item>2 — Credit line item detail record</item>
    ///   <item>3 — Credit last line item detail record</item>
    ///   <item>4 — Payment line item detail record</item>
    ///   <item>5 — Payment last line item detail record</item>
    /// </list>
    /// The last-item variants (1, 3, 5) should be used on the final line item in each group.
    /// </summary>
    [JsonPropertyName("lineItemDetailIndicator")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? LineItemDetailIndicator { get; set; }

    /// <summary>
    /// String bridge for the DataGrid ComboBox editor — avoids int?/string type-mismatch
    /// when using SelectedItem binding in the CellEditingTemplate.
    /// </summary>
    [JsonIgnore]
    public string LineItemDetailIndicatorStr
    {
        get => (LineItemDetailIndicator ?? 0).ToString();
        set
        {
            if (int.TryParse(value?.Split(' ')[0].Trim(), out var v))
                LineItemDetailIndicator = v;
        }
    }

    /// <summary>Human-readable label for <see cref="LineItemDetailIndicator"/>.</summary>
    [JsonIgnore]
    public string LineItemDetailIndicatorLabel => LineItemDetailIndicator switch
    {
        0    => "0 — Normal",
        1    => "1 — Normal (last)",
        2    => "2 — Credit",
        3    => "3 — Credit (last)",
        4    => "4 — Payment",
        5    => "5 — Payment (last)",
        null => "0 — Normal",   // API returns null for items posted without an explicit indicator → treat as Normal
        var n => $"{n}"
    };

    public decimal PriceDollars => (Price ?? 0) / 100;
    public decimal TotalDollars => (Total ?? 0) / 100;

    private static readonly System.Globalization.CultureInfo _usd =
        System.Globalization.CultureInfo.GetCultureInfo("en-US");
    public string PriceFormatted => PriceDollars.ToString("C2", _usd);
    public string TotalFormatted => TotalDollars.ToString("C2", _usd);
}
