namespace Ksef.Fa3;

/// <summary>Pełna kontrola pliku faktury przed wysyłką: wymagania KSeF + schemat FA(3).</summary>
public static class Fa3Validator
{
    /// <summary>Waliduje dokładnie te bajty, które pójdą do KSeF.</summary>
    public static IReadOnlyList<Fa3ValidationError> Validate(byte[] xml)
    {
        List<Fa3ValidationError> errors = [.. KsefDocumentRules.Check(xml)];
        errors.AddRange(Fa3Schema.Validate(new MemoryStream(xml)));
        return errors;
    }
}
