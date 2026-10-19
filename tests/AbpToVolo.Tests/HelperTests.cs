using AbpToVolo.Core;
using AbpToVolo.Steps;

namespace AbpToVolo.Tests;

public class HelperTests
{
    [Theory]
    [InlineData("8.0.11", "10.0.11", true)]
    [InlineData("10.0.0", "10.0.11", true)]
    [InlineData("10.0.11", "10.0.11", false)]
    [InlineData("10.0.12", "10.0.11", false)]
    [InlineData("9.0.4", "10.0.*", true)]
    [InlineData("10.0.20", "10.0.*", true)]
    [InlineData("11.0.0", "10.0.*", false)]
    public void Version_comparison(string current, string target, bool expected) =>
        Assert.Equal(expected, ProjectFileStep.IsLower(current, target));

    [Theory]
    [InlineData("Acme.PhoneBook.Core", ProjectLayer.Domain)]
    [InlineData("Acme.PhoneBook.Web.Core", ProjectLayer.HttpApi)]
    [InlineData("Acme.PhoneBook.Web.Host", ProjectLayer.Host)]
    [InlineData("Acme.PhoneBook.EntityFrameworkCore", ProjectLayer.EntityFrameworkCore)]
    [InlineData("Acme.PhoneBook.Application", ProjectLayer.Application)]
    [InlineData("Acme.PhoneBook.Migrator", ProjectLayer.DbMigrator)]
    [InlineData("Acme.PhoneBook.Tests", ProjectLayer.Tests)]
    [InlineData("Acme.PhoneBook.Web.Tests", ProjectLayer.Tests)]
    public void Layer_detection(string name, ProjectLayer layer) => Assert.Equal(layer, ProjectInfo.DetectLayer(name));

    [Fact]
    public void Project_rename_does_not_touch_longer_names()
    {
        const string sln = """
            Project("{X}") = "Acme.Core", "src\Acme.Core\Acme.Core.csproj", "{1}"
            Project("{X}") = "Acme.Web.Core", "src\Acme.Web.Core\Acme.Web.Core.csproj", "{2}"
            """;

        var result = RenameLayersStep.ReplaceProjectName(sln, "Acme.Core", "Acme.Domain");

        Assert.Contains("\"Acme.Domain\", \"src\\Acme.Domain\\Acme.Domain.csproj\"", result);
        Assert.Contains("\"Acme.Web.Core\", \"src\\Acme.Web.Core\\Acme.Web.Core.csproj\"", result);
    }

    [Fact]
    public void Rename_target_with_only_build_output_is_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "abptovolo-" + Guid.NewGuid().ToString("N"));
        var leftovers = Path.Combine(root, "Acme.Domain");
        var kept = Path.Combine(root, "Acme.Application");
        Directory.CreateDirectory(Path.Combine(leftovers, "obj", "Debug"));
        File.WriteAllText(Path.Combine(leftovers, "obj", "project.assets.json"), "{}");
        Directory.CreateDirectory(Path.Combine(kept, "obj"));
        File.WriteAllText(Path.Combine(kept, "Class1.cs"), "");
        try
        {
            RenameLayersStep.DeleteBuildLeftovers(leftovers);
            RenameLayersStep.DeleteBuildLeftovers(kept);

            Assert.False(Directory.Exists(leftovers));
            Assert.True(File.Exists(Path.Combine(kept, "Class1.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("PhoneBook-fa.xml", "fa", "PhoneBook")]
    [InlineData("PhoneBook.xml", "en", "PhoneBook")]
    [InlineData("PhoneBook-zh-Hans.xml", "zh-Hans", "PhoneBook")]
    public void Localization_source_name(string file, string culture, string expected) =>
        Assert.Equal(expected, LocalizationStep.SourceName(file, culture));

    [Fact]
    public void Ms_version_is_read_from_nuspec()
    {
        const string nuspec = """
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><dependencies><group targetFramework="net10.0">
              <dependency id="Microsoft.EntityFrameworkCore.Relational" version="10.0.11" />
              <dependency id="Microsoft.EntityFrameworkCore" version="[10.0.11, )" exclude="Build,Analyzers" />
            </group></dependencies></metadata></package>
            """;

        Assert.Equal("10.0.11", ResolveVersionsStep.FindDependency(nuspec, "Microsoft.EntityFrameworkCore"));
    }

    [Fact]
    public void Command_line_is_parsed()
    {
        var options = Program.ParseArguments(["in", "-o", "out", "--abp-version", "9.3.0", "--rename-layers", "--ms-version", "9.0.5"])!;

        Assert.EndsWith("in", options.InputPath);
        Assert.EndsWith("out", options.OutputPath);
        Assert.Equal("9.3.0", options.AbpVersion);
        Assert.Equal("9.0.5", options.MicrosoftPackagesVersion);
        Assert.True(options.RenameLayers);
        Assert.Null(Program.ParseArguments(["--help"]));
        Assert.Throws<ArgumentException>(() => Program.ParseArguments(["in"]));
        Assert.Throws<ArgumentException>(() => Program.ParseArguments(["in", "-o", "out", "--bogus"]));
    }
}
