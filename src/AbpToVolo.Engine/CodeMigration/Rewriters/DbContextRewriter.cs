using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// AbpZeroDbContext&lt;TTenant, TRole, TUser, TSelf&gt; / AbpDbContext → AbpDbContext&lt;TSelf&gt;.
/// For Zero contexts the ABP.io module tables are added to the model (builder.ConfigureIdentity() ...),
/// so that "dotnet ef migrations add" creates them in the application database.
/// </summary>
public sealed class DbContextRewriter : MigrationRewriter
{
    private static readonly (string Method, string Namespace)[] ModuleModelConfigurations =
    [
        ("ConfigurePermissionManagement", "Volo.Abp.PermissionManagement.EntityFrameworkCore"),
        ("ConfigureSettingManagement", "Volo.Abp.SettingManagement.EntityFrameworkCore"),
        ("ConfigureIdentity", "Volo.Abp.Identity.EntityFrameworkCore"),
        ("ConfigureFeatureManagement", "Volo.Abp.FeatureManagement.EntityFrameworkCore"),
        ("ConfigureTenantManagement", "Volo.Abp.TenantManagement.EntityFrameworkCore"),
    ];

    public DbContextRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        var baseType = SyntaxHelpers.FindBaseType(visited, "AbpZeroDbContext", "AbpDbContext");
        if (baseType is null)
        {
            return visited;
        }

        var isZero = SyntaxHelpers.SimpleName(baseType.Type) == "AbpZeroDbContext";
        var isNonGeneric = baseType.Type is IdentifierNameSyntax;
        if (!isZero && !isNonGeneric)
        {
            return visited; // already AbpDbContext<T>
        }

        var className = visited.Identifier.Text;
        Context.RequireUsing("Volo.Abp.EntityFrameworkCore");
        Context.RequireUsing("Volo.Abp.Data");
        Context.Change($"{className}: {SyntaxHelpers.Text(baseType.Type)} → AbpDbContext<{className}>");

        var newBase = SimpleBaseType(ParseTypeName($"AbpDbContext<{className}>")).WithTriviaFrom(baseType);
        visited = visited.ReplaceNode(baseType, newBase);

        if (!visited.AttributeLists.SelectMany(a => a.Attributes).Any(a => SyntaxHelpers.SimpleName(a.Name).StartsWith("ConnectionStringName", StringComparison.Ordinal)))
        {
            visited = AddConnectionStringAttribute(visited);
        }

        if (isZero)
        {
            Context.UsesZero = true;
            visited = AddModuleModelConfiguration(visited);
            Context.FileTodo("Module Zero DbSets (Users, Roles, Tenants, Permissions, Settings...) are now owned by the ABP.io Identity/TenantManagement/PermissionManagement modules. " +
                             "Remove the Zero entity classes and create a fresh migration: dotnet ef migrations add Initial.");
        }

        return visited;
    }

    private ClassDeclarationSyntax AddConnectionStringAttribute(ClassDeclarationSyntax cls)
    {
        var indent = SyntaxHelpers.Indentation(cls);
        var attribute = AttributeList(SingletonSeparatedList(
                Attribute(IdentifierName("ConnectionStringName"))
                    .WithArgumentList(ParseAttributeArgumentList("(\"Default\")"))))
            .WithLeadingTrivia(cls.GetLeadingTrivia())
            .WithTrailingTrivia(EndOfLine(Context.NewLine));

        return cls
            .WithLeadingTrivia(Whitespace(indent))
            .WithAttributeLists(cls.AttributeLists.Insert(0, attribute));
    }

    private ClassDeclarationSyntax AddModuleModelConfiguration(ClassDeclarationSyntax cls)
    {
        foreach (var (_, ns) in ModuleModelConfigurations)
        {
            Context.RequireUsing(ns);
        }

        var onModelCreating = cls.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating" && m.ParameterList.Parameters.Count == 1 && m.Body is not null);

        if (onModelCreating is not null)
        {
            var builder = onModelCreating.ParameterList.Parameters[0].Identifier.Text;
            var body = onModelCreating.Body!;
            var indent = body.Statements.Count > 0 ? SyntaxHelpers.Indentation(body.Statements[0]) : SyntaxHelpers.Indentation(onModelCreating) + "    ";
            var calls = ModuleModelConfigurations.Select(c => SyntaxHelpers.Stmt($"{builder}.{c.Method}();")
                .WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(EndOfLine(Context.NewLine))).ToList();

            var baseCallIndex = body.Statements.IndexOf(s => SyntaxHelpers.Text(s).StartsWith("base.OnModelCreating", StringComparison.Ordinal));
            var newBody = body.WithStatements(body.Statements.InsertRange(baseCallIndex + 1, calls));
            Context.Change("OnModelCreating: added builder.ConfigureIdentity()/ConfigureTenantManagement()/...");
            return cls.ReplaceNode(onModelCreating, onModelCreating.WithBody(newBody));
        }

        var memberIndent = cls.Members.Count > 0 ? SyntaxHelpers.Indentation(cls.Members[0]) : SyntaxHelpers.Indentation(cls) + "    ";
        var nl = Context.NewLine;
        var statementIndent = memberIndent + "    ";
        var code =
            $"{memberIndent}protected override void OnModelCreating(ModelBuilder builder){nl}" +
            $"{memberIndent}{{{nl}" +
            $"{statementIndent}base.OnModelCreating(builder);{nl}{nl}" +
            string.Concat(ModuleModelConfigurations.Select(c => $"{statementIndent}builder.{c.Method}();{nl}")) +
            $"{memberIndent}}}{nl}";

        var method = ParseMemberDeclaration(code)!.WithLeadingTrivia(EndOfLine(nl), Whitespace(memberIndent));
        Context.RequireUsing("Microsoft.EntityFrameworkCore");
        Context.Change("Added OnModelCreating with ABP.io module model configuration");
        return cls.AddMembers(method);
    }
}
