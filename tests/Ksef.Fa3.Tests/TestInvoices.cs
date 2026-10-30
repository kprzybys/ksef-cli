using System.Text.RegularExpressions;

namespace Ksef.Fa3.Tests;

/// <summary>Przykładowe faktury MF z submodułu biblioteki, z wypełnionymi znacznikami #...#.</summary>
internal static partial class TestInvoices
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Templates", name));

    public static string Fill(string name) =>
        Placeholder().Replace(Read(name), m => m.Groups[1].Value switch
        {
            var p when p.Contains("nip", StringComparison.OrdinalIgnoreCase) => "1111111111",
            var p when p.Contains("date") => "2026-10-30",
            "invoice_number" => "FV/TEST/1",
            var p => throw new InvalidOperationException($"Nieobsłużony znacznik #{p}# we wzorze {name}."),
        });

    [GeneratedRegex("#([A-Za-z0-9_]+)#")]
    private static partial Regex Placeholder();
}
