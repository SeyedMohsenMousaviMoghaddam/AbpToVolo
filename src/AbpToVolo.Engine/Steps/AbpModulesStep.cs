using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// After Module Zero is replaced: wires the ABP.io application modules into every layer, as in the ABP.io template
/// (Identity, TenantManagement, PermissionManagement, SettingManagement, FeatureManagement, Account, OpenIddict) —
/// packages + [DependsOn] — and integrates the Identity/TenantManagement entities into the application DbContext
/// ([ReplaceDbContext], module DbSets, builder.ConfigureOpenIddict()).
/// </summary>
public sealed class AbpModulesStep : IMigrationStep
{
    public string Name => "ABP.io modules (Identity, Account, OpenIddict...)";

    public bool IsEnabled(MigrationOptions options) => options.ConvertModuleZero;

    internal static readonly Dictionary<ProjectLayer, Func<string, bool>> ModuleNameFilters = new()
    {
        [ProjectLayer.DomainShared] = n => n.EndsWith("DomainSharedModule", StringComparison.Ordinal),
        [ProjectLayer.Domain] = n => n.EndsWith("DomainModule", StringComparison.Ordinal) || n.EndsWith("CoreModule", StringComparison.Ordinal),
        [ProjectLayer.ApplicationContracts] = n => n.EndsWith("ApplicationContractsModule", StringComparison.Ordinal),
        [ProjectLayer.Application] = n => n.EndsWith("ApplicationModule", StringComparison.Ordinal) && !n.EndsWith("ContractsModule", StringComparison.Ordinal),
        [ProjectLayer.EntityFrameworkCore] = n => n.EndsWith("EntityFrameworkCoreModule", StringComparison.Ordinal) || n.EndsWith("EntityFrameworkModule", StringComparison.Ordinal),
        [ProjectLayer.HttpApi] = n => n.EndsWith("HttpApiModule", StringComparison.Ordinal) || n.EndsWith("WebCoreModule", StringComparison.Ordinal),
        [ProjectLayer.Host] = n => n.EndsWith("HostModule", StringComparison.Ordinal),
        [ProjectLayer.DbMigrator] = n => n.EndsWith("MigratorModule", StringComparison.Ordinal),
    };

    public void Execute(MigrationContext context)
    {
        if (!context.UsesIdentityModules)
        {
            context.Report.Info(Name, "Module Zero was not used; no identity modules added.");
            return;
        }

        foreach (var (layer, modules) in AbpModuleCatalog.IdentityModules)
        {
            var project = context.FindProject(layer);
            if (project is null) continue;
            AddModules(context, project, layer, modules);
        }

        IntegrateDbContext(context);
        context.Log.WriteLine("Identity, TenantManagement, PermissionManagement, SettingManagement, FeatureManagement, Account and OpenIddict modules wired");
    }

    internal static void AddModules(MigrationContext context, ProjectInfo project, ProjectLayer layer, IEnumerable<AbpPackageModule> modules)
    {
        const string step = "ABP.io modules (Identity, Account, OpenIddict...)";
        var moduleFile = FindModuleFile(project, ModuleNameFilters[layer]);
        foreach (var module in modules)
        {
            if (ProjectWriter.AddPackageReference(project.CsprojPath, module.Package, context.Options.AbpVersion))
            {
                context.Report.Change(step, $"Added package {module.Package}", context.Relative(project.CsprojPath));
            }

            if (moduleFile is null) continue;
            var text = File.ReadAllText(moduleFile);
            var updated = ModuleDependencyEditor.AddDependsOn(text, module.Module, module.Namespace, ModuleNameFilters[layer]);
            if (updated is not null && updated != text)
            {
                FileSystem.WriteText(moduleFile, updated);
                context.Report.Change(step, $"[DependsOn] added {module.Module}", context.Relative(moduleFile));
            }
        }

        if (moduleFile is null)
        {
            context.Report.Todo(step, $"No module class found in {project.Name}: add {string.Join(", ", modules.Select(m => m.Module))} to its [DependsOn].");
        }
    }

    internal static string? FindModuleFile(ProjectInfo project, Func<string, bool> isModule) =>
        FileSystem.EnumerateFiles(project.Directory, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("AbpModule", StringComparison.Ordinal))
            .FirstOrDefault(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Any(c => SyntaxHelpers.HasBaseType(c, "AbpModule") && isModule(c.Identifier.Text)));

    private static readonly (string Type, string Name)[] ModuleDbSets =
    [
        ("IdentityUser", "Users"), ("IdentityRole", "Roles"), ("IdentityClaimType", "ClaimTypes"), ("OrganizationUnit", "OrganizationUnits"),
        ("IdentitySecurityLog", "SecurityLogs"), ("IdentityLinkUser", "LinkUsers"), ("IdentityUserDelegation", "UserDelegations"),
        ("IdentitySession", "Sessions"), ("Tenant", "Tenants"), ("TenantConnectionString", "TenantConnectionStrings")
    ];

    /// <summary>Identity entities that are not aggregate roots (Module Zero code used repositories of their Boilerplate equivalents).</summary>
    private static readonly (string Type, string Name)[] SecondaryDbSets =
    [
        ("IdentityUserRole", "UserRoles"), ("IdentityUserLogin", "UserLogins"), ("IdentityUserToken", "UserTokens"),
        ("IdentityUserClaim", "UserClaims"), ("IdentityRoleClaim", "RoleClaims"), ("IdentityUserOrganizationUnit", "UserOrganizationUnits"),
        ("OrganizationUnitRole", "OrganizationUnitRoles")
    ];

    private static HashSet<string> RepositoryEntitiesUsed(MigrationContext context)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"\bIRepository<\s*(?<type>\w+)\s*[,>]");
        return context.Projects
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f))
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => m.Groups["type"].Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// ABP.io template: the application DbContext replaces IIdentityDbContext/ITenantManagementDbContext, so the module
    /// entities live in the same DbContext (navigation properties to IdentityUser, single migration history).
    /// </summary>
    private void IntegrateDbContext(MigrationContext context)
    {
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        if (efCore is null) return;

        foreach (var file in FileSystem.EnumerateFiles(efCore.Directory, "*.cs").Where(f => !context.IsExcluded(f)))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("AbpDbContext<", StringComparison.Ordinal)) continue;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var dbContext = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => SyntaxHelpers.HasBaseType(c, "AbpDbContext"));
            if (dbContext is null || SyntaxHelpers.HasBaseType(dbContext, "IIdentityDbContext")) continue;

            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var memberIndent = dbContext.Members.Count > 0 ? SyntaxHelpers.Indentation(dbContext.Members[0]) : SyntaxHelpers.Indentation(dbContext) + "    ";
            var updated = dbContext;

            // interfaces
            var baseList = updated.BaseList!;
            foreach (var iface in new[] { "IIdentityDbContext", "ITenantManagementDbContext" })
            {
                baseList = baseList.AddTypes(SimpleBaseType(IdentifierName(iface)).WithLeadingTrivia(Space));
            }

            updated = updated.WithBaseList(baseList);

            // [ReplaceDbContext]
            var classIndent = SyntaxHelpers.Indentation(updated);
            var attributes = new[] { "IIdentityDbContext", "ITenantManagementDbContext" }.Select(i =>
                AttributeList(SingletonSeparatedList(Attribute(IdentifierName("ReplaceDbContext"), ParseAttributeArgumentList($"(typeof({i}))"))))
                    .WithLeadingTrivia(Whitespace(classIndent)).WithTrailingTrivia(EndOfLine(nl)));
            var leading = updated.GetLeadingTrivia();
            updated = updated.WithLeadingTrivia(Whitespace(classIndent));              // the old first token keeps only its indentation
            updated = updated.WithAttributeLists(updated.AttributeLists.InsertRange(0, attributes));
            updated = updated.WithLeadingTrivia(leading);                              // comments move in front of the new first attribute

            // module DbSets (+ the Identity entities that are not aggregate roots but have a repository in the converted code:
            // AddDefaultRepositories(includeAllEntities: true) registers IRepository<T> only for the DbSets of the DbContext)
            var existing = updated.Members.OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
            var used = RepositoryEntitiesUsed(context);
            var dbSets = ModuleDbSets.Concat(SecondaryDbSets.Where(s => used.Contains(s.Type))).Where(s => !existing.Contains(s.Name))
                .Select((s, i) => ParseMemberDeclaration($"public DbSet<{s.Type}> {s.Name} {{ get; set; }}")!
                    .WithLeadingTrivia(i == 0
                        ? TriviaList(EndOfLine(nl), Whitespace(memberIndent), Comment("// Entities of the ABP.io Identity and TenantManagement modules (ReplaceDbContext)"), EndOfLine(nl), Whitespace(memberIndent))
                        : TriviaList(Whitespace(memberIndent)))
                    .WithTrailingTrivia(EndOfLine(nl)))
                .ToList();
            updated = updated.WithMembers(updated.Members.InsertRange(0, dbSets));

            // builder.ConfigureOpenIddict()
            var onModelCreating = updated.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "OnModelCreating" && m.Body is not null);
            if (onModelCreating is not null && !onModelCreating.ToString().Contains("ConfigureOpenIddict", StringComparison.Ordinal))
            {
                var builder = onModelCreating.ParameterList.Parameters[0].Identifier.Text;
                var statements = onModelCreating.Body!.Statements;
                var anchor = statements.LastOrDefault(s => SyntaxHelpers.Text(s).StartsWith($"{builder}.Configure", StringComparison.Ordinal))
                             ?? statements.FirstOrDefault(s => SyntaxHelpers.Text(s).StartsWith("base.OnModelCreating", StringComparison.Ordinal));
                var indent = statements.Count > 0 ? SyntaxHelpers.Indentation(statements[0]) : memberIndent + "    ";
                var call = ParseStatement($"{builder}.ConfigureOpenIddict();").WithLeadingTrivia(Whitespace(indent)).WithTrailingTrivia(EndOfLine(nl));
                var index = anchor is null ? 0 : statements.IndexOf(anchor) + 1;
                updated = updated.ReplaceNode(onModelCreating, onModelCreating.WithBody(onModelCreating.Body.WithStatements(statements.Insert(index, call))));
            }

            var code = root.ReplaceNode(dbContext, updated).ToFullString();
            code = ModuleMethodEditor.AddUsings(code, [
                "Volo.Abp.DependencyInjection", "Volo.Abp.Identity", "Volo.Abp.Identity.EntityFrameworkCore", "Volo.Abp.TenantManagement",
                "Volo.Abp.TenantManagement.EntityFrameworkCore", "Volo.Abp.OpenIddict.EntityFrameworkCore", "Microsoft.EntityFrameworkCore"
            ]);
            FileSystem.WriteText(file, code);
            context.Report.Change(Name, $"{dbContext.Identifier.Text}: [ReplaceDbContext] IIdentityDbContext/ITenantManagementDbContext, module DbSets, ConfigureOpenIddict()", context.Relative(file));
            return;
        }
    }
}
