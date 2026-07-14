namespace Shared.Models.DTOs;

public record MoneyDto(int Amount, string Currency = "EUR")
{
    /// <summary>
    /// Amount in the major currency unit (e.g. euros) formatted to 2 decimal places.
    /// Calculations stay in cents (Amount) for precision.
    /// </summary>
    public string DisplayAmount => (Amount / 100m).ToString("F2");
}
