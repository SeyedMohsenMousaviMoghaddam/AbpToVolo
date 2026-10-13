using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AbpToVolo.Tests;

public abstract class RewriterTestBase
{
    protected static readonly LocalizationResourceInfo PhoneBookResource =
        new("PhoneBook", "PhoneBookResource", "Acme.PhoneBook.Localization", "/Localization/PhoneBook", "en", "Acme.Test", ["en", "fa"]);

    protected static (string Code, FileMigrationContext Context) Migrate(
        string code, ProjectLayer layer = ProjectLayer.Unknown, LocalizationResourceInfo? localization = null, IdTypeMode idType = IdTypeMode.Source)
    {
        var project = new ProjectInfo { Name = "Acme.Test", CsprojPath = @"C:\x\Acme.Test.csproj", Layer = layer, RootNamespace = "Acme" };
        var context = new FileMigrationContext("Test.cs", "\n", project, localization) { IdType = idType };
        var result = CSharpFileMigrator.Migrate(code.ReplaceLineEndings("\n"), context);

        var errors = CSharpSyntaxTree.ParseText(result.Code).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Converted code has syntax errors:\n" + string.Join("\n", errors) + "\n\n" + result.Code);
        return (result.Code, context);
    }

    /// <summary>Compares ignoring whitespace differences.</summary>
    protected static void AssertContains(string expected, string actual) =>
        Assert.True(Normalize(actual).Contains(Normalize(expected), StringComparison.Ordinal), $"Expected to find:\n{expected}\n\nin:\n{actual}");

    protected static void AssertNotContains(string unexpected, string actual) =>
        Assert.False(Normalize(actual).Contains(Normalize(unexpected), StringComparison.Ordinal), $"Did not expect:\n{unexpected}\n\nin:\n{actual}");

    private static string Normalize(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
