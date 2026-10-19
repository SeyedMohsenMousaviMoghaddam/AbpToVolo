using System.Text;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// Moves code into the contract projects created by <see cref="ContractsScaffoldStep"/>, following the ABP.io layering:
///   Domain.Shared        ← enums and constant classes of the domain (Core) project
///   Application.Contracts ← DTOs, app service interfaces, application enums, permission definition providers
/// A semantic model of the whole solution decides what may move: a type moves only if every solution type it uses
/// is (or moves) in an allowed layer (Shared may use Shared; Contracts may use Shared and Contracts). The unit is a single
/// top-level type, so an enum declared next to an entity can still move. Two common blockers are solved automatically:
///   [AutoMap(typeof(Entity))] on a moved DTO     → CreateMap in a generated AutoMapper Profile in the Application project
///   Entity.MaxNameLength used by a moved DTO     → EntityConsts.MaxNameLength in Domain.Shared (the entity keeps its const)
/// Everything that cannot move stays where it was and is listed in the report.
/// </summary>
public sealed class ContractsStep : IMigrationStep
{
    private enum Target { None, Shared, Contracts }

    private sealed class Unit
    {
        public required string File { get; init; }
        public required MemberDeclarationSyntax Decl { get; init; }
        public required ProjectInfo Project { get; init; }
        public Target Candidate { get; set; }
        public Target Fixed { get; set; }
        public HashSet<Unit> Refs { get; } = [];
        public List<ConstRef> Consts { get; } = [];
        public List<MapRef> Maps { get; } = [];
        public string? BlockReason { get; set; }

        /// <summary>Namespaces of the resolved symbols the type uses (decides which usings to keep).</summary>
        public HashSet<string> UsedNamespaces { get; } = new(StringComparer.Ordinal);

        /// <summary>The type uses names that are not resolved in the model (NuGet types such as Volo.Abp.*).</summary>
        public bool HasUnresolved { get; set; }

        public HashSet<string> UnresolvedNames { get; } = new(StringComparer.Ordinal);
        public bool Moving => Candidate != Target.None && BlockReason is null;
        public Target Final => Fixed != Target.None ? Fixed : Moving ? Candidate : Target.None;

        public string Name => Decl switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier.Text,
            DelegateDeclarationSyntax d => d.Identifier.Text,
            _ => "?"
        };

        public string Namespace => Decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } ns ? SolutionModel.FullNamespace(ns) : "";
    }

    private sealed record ConstRef(IFieldSymbol Field, Unit Owner, TextSpan AccessSpan);

    private sealed record MapRef(string Source, string Destination, bool Reverse, TextSpan AttributeSpan);

    private static readonly string[] AllowedExternalPrefixes =
        ["System", "Volo.Abp", "Microsoft.Extensions.Localization", "Microsoft.AspNetCore.Authorization", "JetBrains.Annotations", "AutoMapper"];

    private static readonly HashSet<string> AutoMapperOnlyAttributes = new(StringComparer.Ordinal)
    {
        "Ignore", "IgnoreMap", "SourceMember", "ValueConverter", "ValueResolver", "NullSubstitute", "MapAtRuntime", "UseExistingValue", "AutoMapFrom", "AutoMapTo"
    };

    private static readonly HashSet<string> DtoBaseTypes = new(StringComparer.Ordinal)
    {
        "EntityDto", "CreationAuditedEntityDto", "AuditedEntityDto", "FullAuditedEntityDto", "ExtensibleEntityDto", "ExtensibleObject",
        "ExtensibleAuditedEntityDto", "ExtensibleFullAuditedEntityDto", "PagedResultRequestDto", "PagedAndSortedResultRequestDto",
        "LimitedResultRequestDto", "ListResultDto", "PagedResultDto", "IEntityDto", "IValidatableObject", "IPagedResultRequest",
        "ISortedResultRequest", "ILimitedResultRequest", "IHasCreationTime"
    };

    private static readonly string[] DtoNameSuffixes = ["Dto", "Input", "Output", "Request", "Response", "Inputs", "Outputs"];

    public string Name => "Contracts (move code)";

    public bool IsEnabled(MigrationOptions options) => options.CreateContracts;

    public void Execute(MigrationContext context)
    {
        var domain = context.FindProject(ProjectLayer.Domain);
        var application = context.FindProject(ProjectLayer.Application);
        var shared = context.FindProject(ProjectLayer.DomainShared);
        var contracts = context.FindProject(ProjectLayer.ApplicationContracts);
        if (domain is null || application is null || shared is null || contracts is null)
        {
            return;
        }

        var sourceFiles = context.Projects
            .Where(p => p.Layer != ProjectLayer.Tests)
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var model = SolutionModel.Build(sourceFiles);
        var units = CreateUnits(context, model, domain, application, shared, contracts);
        var byDecl = units.ToDictionary(u => (SyntaxNode)u.Decl);

        foreach (var unit in units.Where(u => u.Candidate != Target.None))
        {
            Analyze(unit, model, byDecl);
        }

        BlockReverseReferences(context, units);
        ResolveFixpoint(units);
        var duplicates = MergeDuplicates(units, application);
        AddAbpTypePackages(context, units, shared, contracts);

        var moved = units.Where(u => u.Moving).ToList();
        var constsClasses = PlanConstsClasses(model, moved);
        Apply(context, model, units, shared, contracts, constsClasses);
        RemoveDuplicates(context, duplicates);
        WriteConstsClasses(context, shared, constsClasses);
        WriteProfile(context, application, moved);
        WriteModules(context, domain, application, shared, contracts);
        Summarize(context, units);
    }

    private static List<Unit> CreateUnits(MigrationContext context, SolutionModel model, ProjectInfo domain, ProjectInfo application, ProjectInfo shared, ProjectInfo contracts)
    {
        var units = new List<Unit>();
        foreach (var (tree, file) in model.FileOf)
        {
            var project = context.FindProjectForFile(file);
            if (project is null) continue;

            var declarations = tree.GetRoot()
                .DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<MemberDeclarationSyntax>()
                .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

            foreach (var decl in declarations)
            {
                var unit = new Unit { File = file, Decl = decl, Project = project };
                if (project == shared) unit.Fixed = Target.Shared;
                else if (project == contracts) unit.Fixed = Target.Contracts;
                else if (project == domain)
                {
                    // Plain DTOs of the domain project (used by domain services, so not movable to Contracts) can be shared.
                    unit.Candidate = IsSharedKind(decl) || IsContractKind(decl, context.Relative(file)) && decl is not InterfaceDeclarationSyntax
                        ? Target.Shared
                        : IsPermissionProvider(decl) ? Target.Contracts : Target.None;
                }
                else if (project == application) unit.Candidate = IsContractKind(decl, context.Relative(file)) ? Target.Contracts : Target.None;
                units.Add(unit);
            }
        }

        return units;
    }

    // ---------- classification ----------

    internal static bool IsSharedKind(MemberDeclarationSyntax decl) => decl switch
    {
        EnumDeclarationSyntax => true,
        ClassDeclarationSyntax c when c.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().StartsWith("LocalizationResourceName", StringComparison.Ordinal)) => true,
        ClassDeclarationSyntax or StructDeclarationSyntax when decl is TypeDeclarationSyntax t => t.BaseList is null && t.Members.Count > 0 && t.Members.All(IsConstantMember),
        _ => false
    };

    private static bool IsConstantMember(MemberDeclarationSyntax member) => member switch
    {
        FieldDeclarationSyntax f => f.Modifiers.Any(SyntaxKind.ConstKeyword) ||
                                    (f.Modifiers.Any(SyntaxKind.StaticKeyword) && f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) &&
                                     f.Declaration.Variables.All(v => v.Initializer?.Value is LiteralExpressionSyntax)),
        BaseTypeDeclarationSyntax nested => IsSharedKind(nested),
        _ => false
    };

    private static bool IsPermissionProvider(MemberDeclarationSyntax decl) =>
        decl is ClassDeclarationSyntax c && SyntaxHelpers.HasBaseType(c, "PermissionDefinitionProvider");

    internal static bool IsContractKind(MemberDeclarationSyntax decl, string relativePath)
    {
        switch (decl)
        {
            case EnumDeclarationSyntax:
                return true;
            case InterfaceDeclarationSyntax i:
                return i.Identifier.Text.EndsWith("AppService", StringComparison.Ordinal) ||
                       i.BaseList?.Types.Any(t => SyntaxHelpers.SimpleName(t.Type) is var n &&
                                                  (n.EndsWith("AppService", StringComparison.Ordinal) || n is "IApplicationService" or "IRemoteService")) == true;
            case TypeDeclarationSyntax t when t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax:
                var name = t.Identifier.Text;
                var baseNames = t.BaseList?.Types.Select(b => SyntaxHelpers.SimpleName(b.Type)).ToList() ?? [];
                if (baseNames.Any(b => b is "Profile" || b.EndsWith("Service", StringComparison.Ordinal) || b.EndsWith("Controller", StringComparison.Ordinal) ||
                                       b.EndsWith("Manager", StringComparison.Ordinal) || b.EndsWith("ServiceBase", StringComparison.Ordinal)))
                {
                    return false;
                }

                var looksLikeDto = DtoNameSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)) ||
                                   relativePath.Contains("/Dto/", StringComparison.OrdinalIgnoreCase) ||
                                   relativePath.Contains("/Dtos/", StringComparison.OrdinalIgnoreCase);
                var baseIsDto = baseNames.Count == 0 || baseNames.All(b => DtoBaseTypes.Contains(b) || b.EndsWith("Dto", StringComparison.Ordinal) ||
                                                                          DtoNameSuffixes.Any(s => b.EndsWith(s, StringComparison.Ordinal)));
                return looksLikeDto && baseIsDto;
            default:
                return false;
        }
    }

    // ---------- analysis ----------

    private static void Analyze(Unit unit, SolutionModel model, Dictionary<SyntaxNode, Unit> byDecl)
    {
        var root = unit.Decl.SyntaxTree.GetCompilationUnitRoot();
        var semantic = model.Model(unit.Decl.SyntaxTree);
        var ignored = new List<TextSpan>();
        foreach (var attribute in unit.Decl.DescendantNodes().OfType<AttributeSyntax>())
        {
            var attributeName = AttributeName(attribute);
            if (attributeName == "AutoMap")
            {
                if (ReadMap(attribute, semantic, unit) is { } map)
                {
                    unit.Maps.Add(map);
                    ignored.Add(attribute.Span);
                    continue;
                }

                unit.BlockReason = "uses an [AutoMap] attribute that cannot be converted to a Profile";
                return;
            }

            if (AutoMapperOnlyAttributes.Contains(attributeName))
            {
                unit.BlockReason = $"uses the AutoMapper attribute [{attributeName}]";
                return;
            }
        }

        foreach (var name in unit.Decl.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (ignored.Any(s => s.Contains(name.Span)))
            {
                continue;
            }

            var info = semantic.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is null)
            {
                // A member of an unresolved type is not interesting; the unresolved type name itself is.
                if (!(name.Parent is MemberAccessExpressionSyntax ma && ma.Name == name) && name.Identifier.Text is not ("var" or "nameof" or "dynamic"))
                {
                    unit.HasUnresolved = true;
                    unit.UnresolvedNames.Add(name.Identifier.Text);
                }

                continue;
            }

            // "Person" in Person.MaxNameLength: not a dependency when the const itself is extracted to Domain.Shared.
            if (symbol is INamedTypeSymbol && name.Parent is MemberAccessExpressionSyntax owner && owner.Expression == name &&
                semantic.GetSymbolInfo(owner.Name).Symbol is IFieldSymbol { IsConst: true } ownerConst && IsLiteralConst(ownerConst) &&
                unit.Candidate == Target.Contracts)
            {
                continue;
            }

            if (symbol.ContainingNamespace is { IsGlobalNamespace: false } containing && symbol is not INamespaceSymbol)
            {
                unit.UsedNamespaces.Add(containing.ToDisplayString());
            }

            if (symbol is INamespaceSymbol or ILocalSymbol or IParameterSymbol or ITypeParameterSymbol or IRangeVariableSymbol or ILabelSymbol or IDiscardSymbol)
            {
                continue;
            }

            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            if (type is null)
            {
                continue;
            }

            foreach (var reference in type.OriginalDefinition.DeclaringSyntaxReferences)
            {
                if (TopLevel(reference.GetSyntax()) is not { } decl || !byDecl.TryGetValue(decl, out var target) || target == unit)
                {
                    continue;
                }

                if (symbol is IFieldSymbol { IsConst: true } field && target.Candidate == Target.None && target.Fixed == Target.None)
                {
                    // Entity.MaxLength used in a DTO attribute: extract it to EntityConsts in Domain.Shared.
                    if (unit.Candidate == Target.Contracts && name.Parent is MemberAccessExpressionSyntax access && access.Name == name && IsLiteralConst(field))
                    {
                        unit.Consts.Add(new ConstRef(field, target, access.Span));
                        continue;
                    }
                }

                unit.Refs.Add(target);
            }
        }

        // A using of a namespace the contract projects do not have (EF Core, ASP.NET MVC, a third-party library...) only
        // matters when the type uses a name that could come from it, i.e. an unresolved name that is not a known ABP type.
        // (Files often mix a DTO/interface with its implementation, whose usings are irrelevant for the moved type.)
        // Domain.Shared has no ABP application packages: a shared candidate must not use unresolved (NuGet) types,
        // except the few that Domain.Shared does have.
        if (unit.Candidate == Target.Shared && unit.Decl is not EnumDeclarationSyntax)
        {
            var notShared = unit.UnresolvedNames.Where(n => !KnownSharedTypes.Contains(n) && !TypeMappings.AbpTypePackages.ContainsKey(n)).ToList();
            if (notShared.Count > 0)
            {
                unit.BlockReason = $"uses {string.Join(", ", notShared.Take(3))} (not available in Domain.Shared)";
                return;
            }
        }

        var unknownNames = unit.UnresolvedNames.Where(n => !KnownAbpContractTypes.Contains(n) && !TypeMappings.AbpTypePackages.ContainsKey(n)).ToList();
        if (unknownNames.Count > 0)
        {
            var disallowed = UsingNames(root, unit.Decl).FirstOrDefault(n =>
                !model.Namespaces.Contains(n) && !AllowedExternalPrefixes.Any(p => n == p || n.StartsWith(p + ".", StringComparison.Ordinal)));
            if (disallowed is not null)
            {
                unit.BlockReason = $"uses namespace {disallowed} ({string.Join(", ", unknownNames.Take(3))})";
            }
        }
    }

    /// <summary>ABP types available in Domain.Shared (Volo.Abp.Ddd.Domain.Shared, Localization, Validation).</summary>
    private static readonly HashSet<string> KnownSharedTypes = new(StringComparer.Ordinal)
    {
        "LocalizationResourceName", "LocalizationResourceNameAttribute", "ILocalizableString", "LocalizableString", "MultiTenancySides",
        "CanBeNull", "NotNull", "Check", "DynamicStringLength", "DynamicMaxLength"
    };

    /// <summary>ABP types that are available in Domain.Shared / Application.Contracts.</summary>
    private static readonly HashSet<string> KnownAbpContractTypes = new(StringComparer.Ordinal)
    {
        "EntityDto", "IEntityDto", "CreationAuditedEntityDto", "AuditedEntityDto", "FullAuditedEntityDto", "ExtensibleEntityDto",
        "ExtensibleCreationAuditedEntityDto", "ExtensibleAuditedEntityDto", "ExtensibleFullAuditedEntityDto", "ExtensibleObject",
        "ListResultDto", "PagedResultDto", "PagedResultRequestDto", "PagedAndSortedResultRequestDto", "LimitedResultRequestDto",
        "IPagedResultRequest", "ISortedResultRequest", "ILimitedResultRequest", "IListResult", "IPagedResult",
        "IApplicationService", "IRemoteService", "ICrudAppService", "IReadOnlyAppService", "ICreateAppService", "IUpdateAppService",
        "IDeleteAppService", "ICreateUpdateAppService", "RemoteService", "RemoteServiceAttribute",
        "PermissionDefinitionProvider", "IPermissionDefinitionContext", "PermissionDefinition", "PermissionGroupDefinition",
        "MultiTenancySides", "ILocalizableString", "LocalizableString", "LocalizedString", "IStringLocalizer",
        "LocalizationResourceName", "LocalizationResourceNameAttribute", "IHasCreationTime", "IHasModificationTime", "IMayHaveCreator",
        "ISoftDelete", "IMultiTenant", "IHasExtraProperties", "ExtraPropertyDictionary", "DisableAuditing", "DisableAuditingAttribute",
        "DisableValidation", "Authorize", "AllowAnonymous", "CanBeNull", "NotNull", "Check", "IRemoteStreamContent", "RemoteStreamContent",
        "DynamicStringLength", "DynamicMaxLength", "Task"
    };

    private static MapRef? ReadMap(AttributeSyntax attribute, SemanticModel semantic, Unit unit)
    {
        var args = attribute.ArgumentList?.Arguments;
        if (args is not { Count: >= 1 } || args.Value[0].Expression is not TypeOfExpressionSyntax typeOf)
        {
            return null;
        }

        var source = semantic.GetTypeInfo(typeOf.Type).Type;
        var destination = semantic.GetDeclaredSymbol(unit.Decl) as INamedTypeSymbol;
        if (source is null or IErrorTypeSymbol || destination is null || destination.IsGenericType)
        {
            return null;
        }

        var reverse = args.Value.Skip(1).Any(a => a.NameEquals?.Name.Identifier.Text == "ReverseMap" && a.Expression.ToString() == "true");
        return new MapRef(source.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), destination.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            reverse, attribute.Span);
    }

    private static bool IsLiteralConst(IFieldSymbol field) =>
        field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var value } &&
        !value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any();

    /// <summary>Permission providers are only moved when no domain/infrastructure code uses them.</summary>
    private static void BlockReverseReferences(MigrationContext context, List<Unit> units)
    {
        foreach (var unit in units.Where(u => u.Candidate == Target.Contracts && u.Project.Layer == ProjectLayer.Domain))
        {
            var pattern = new System.Text.RegularExpressions.Regex($@"\b{System.Text.RegularExpressions.Regex.Escape(unit.Name)}\b");
            var user = units.Select(u => u.File).Distinct()
                .Where(f => f != unit.File && context.FindProjectForFile(f) is { Layer: not (ProjectLayer.Application or ProjectLayer.ApplicationContracts or ProjectLayer.HttpApi or ProjectLayer.Host) })
                .FirstOrDefault(f => pattern.IsMatch(File.ReadAllText(f)));
            if (user is not null)
            {
                unit.BlockReason = $"is used by {Path.GetFileName(user)}";
            }
        }
    }

    private static void ResolveFixpoint(List<Unit> units)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var unit in units.Where(u => u.Moving))
            {
                var blocker = unit.Refs.FirstOrDefault(r => !IsAllowed(unit, r));
                if (blocker is not null)
                {
                    unit.BlockReason = $"depends on {blocker.Name} ({blocker.Project.Name})";
                    changed = true;
                }
            }
        }
        while (changed);
    }

    /// <summary>
    /// The same type (same full name, same code) in the application and the domain: in the source the application's copy won
    /// in its own project; once moved, the Contracts and Domain.Shared copies would be ambiguous (CS0433). The application's
    /// copy is dropped and the Domain.Shared one serves everyone.
    /// </summary>
    private static List<Unit> MergeDuplicates(List<Unit> units, ProjectInfo application)
    {
        var shared = units.Where(u => u.Project != application && u.Final == Target.Shared && u.Decl is BaseTypeDeclarationSyntax)
            .GroupBy(u => $"{u.Namespace}.{u.Name}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var duplicates = new List<Unit>();
        foreach (var unit in units.Where(u => u.Project == application && u.Decl is BaseTypeDeclarationSyntax))
        {
            if (!shared.TryGetValue($"{unit.Namespace}.{unit.Name}", out var other)) continue;
            if (SyntaxFactory.AreEquivalent(unit.Decl, other.Decl))
            {
                unit.Candidate = Target.None;
                unit.Fixed = Target.Shared;
                duplicates.Add(unit);
                continue;
            }

            // A different type of the same name: it stays in the application, which sees its own copy first (as in the
            // source); the types that use it stay with it.
            if (unit.Moving) unit.BlockReason = $"another {unit.Name} of the same namespace is in {other.Project.Name}";
        }

        if (units.Any(u => u.BlockReason?.StartsWith("another ", StringComparison.Ordinal) == true)) ResolveFixpoint(units);
        return duplicates;
    }

    private void RemoveDuplicates(MigrationContext context, List<Unit> duplicates)
    {
        foreach (var file in duplicates.GroupBy(u => u.File, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(file.Key)) continue;
            var (text, encoding) = FileSystem.ReadText(file.Key);
            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var names = file.Select(u => u.Name).ToHashSet(StringComparer.Ordinal);
            var remove = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => names.Contains(t.Identifier.Text) && t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax).ToList();
            var rest = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepNoTrivia)!;
            if (!rest.DescendantNodes().OfType<MemberDeclarationSyntax>().Any(m => m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)) File.Delete(file.Key);
            else FileSystem.WriteText(file.Key, rest.ToFullString(), encoding);
            context.Report.Change(Name, $"Removed {string.Join(", ", names)} (the same type is in Domain.Shared; both copies would be ambiguous)", context.Relative(file.Key));
        }
    }

    private static bool IsAllowed(Unit unit, Unit dependency) =>
        dependency.Final == Target.Shared || (unit.Candidate == Target.Contracts && dependency.Final == Target.Contracts);

    // ---------- applying the moves ----------

    private static Dictionary<Unit, (string ClassName, List<IFieldSymbol> Fields)> PlanConstsClasses(SolutionModel model, List<Unit> moved)
    {
        var result = new Dictionary<Unit, (string, List<IFieldSymbol>)>();
        foreach (var group in moved.SelectMany(u => u.Consts).GroupBy(c => c.Owner))
        {
            var owner = group.Key;
            var className = owner.Name + "Consts";
            var ns = owner.Namespace;
            if (model.Compilation.GetTypeByMetadataName(string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}") is not null)
            {
                className = owner.Name + "SharedConsts";
            }

            result[owner] = (className, group.Select(c => c.Field).Distinct<IFieldSymbol>(SymbolEqualityComparer.Default).ToList());
        }

        return result;
    }

    private void Apply(MigrationContext context, SolutionModel model, List<Unit> units, ProjectInfo shared, ProjectInfo contracts,
        Dictionary<Unit, (string ClassName, List<IFieldSymbol> Fields)> constsClasses)
    {
        var sharedNamespaces = units.Where(u => u.Final == Target.Shared).Select(u => u.Namespace)
            .Concat(constsClasses.Keys.Select(o => o.Namespace)).ToHashSet(StringComparer.Ordinal);
        var contractNamespaces = units.Where(u => u.Final == Target.Contracts).Select(u => u.Namespace).Concat(sharedNamespaces).ToHashSet(StringComparer.Ordinal);

        // Files that need edits: files with moving units, and entity files whose consts were extracted.
        var files = units.Where(u => u.Moving).Select(u => u.File)
            .Concat(constsClasses.Keys.Select(o => o.File))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var tree = model.TreeOf[file];
            var root = tree.GetCompilationUnitRoot();
            var fileUnits = units.Where(u => u.File == file).ToList();
            var moving = fileUnits.Where(u => u.Moving).ToList();
            var source = context.FindProjectForFile(file)!;
            var relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(source.Directory, file)) ?? "";

            // Entity consts that moved to Domain.Shared: the entity keeps the const, pointing to the shared value.
            var initializerEdits = constsClasses.Where(c => c.Key.File == file)
                .SelectMany(c => c.Value.Fields.Select(f => (Field: f, c.Value.ClassName)))
                .Select(x => (Node: (x.Field.DeclaringSyntaxReferences[0].GetSyntax() as VariableDeclaratorSyntax)!.Initializer!.Value, x.ClassName, x.Field.Name))
                .ToList();

            var wholeFileTarget = moving.Count == fileUnits.Count && moving.Select(u => u.Candidate).Distinct().Count() == 1 ? moving[0].Candidate : Target.None;
            if (wholeFileTarget != Target.None && initializerEdits.Count == 0)
            {
                var available = wholeFileTarget == Target.Shared ? sharedNamespaces : contractNamespaces;
                var rewritten = RewriteMoved(root, moving, constsClasses, UsingFilter.For(moving, wholeFileTarget, available, model));
                var targetProject = wholeFileTarget == Target.Shared ? shared : contracts;
                var targetPath = UniquePath(Path.Combine(targetProject.Directory, relativeFolder, Path.GetFileName(file)));
                FileSystem.WriteText(targetPath, rewritten.ToFullString());
                File.Delete(file);
                context.Report.Change(Name, $"Moved to {targetProject.Name}: {string.Join(", ", moving.Select(u => u.Name))}", context.Relative(targetPath));
                continue;
            }

            // Extract the moving types into their own files and edit the original file.
            foreach (var unit in moving)
            {
                var available = unit.Candidate == Target.Shared ? sharedNamespaces : contractNamespaces;
                var targetProject = unit.Candidate == Target.Shared ? shared : contracts;
                var targetPath = UniquePath(Path.Combine(targetProject.Directory, relativeFolder, unit.Name + ".cs"));
                FileSystem.WriteText(targetPath, ExtractedFile(root, unit, constsClasses, UsingFilter.For([unit], unit.Candidate, available, model)));
                context.Report.Change(Name, $"Extracted {unit.Name} from {Path.GetFileName(file)} to {targetProject.Name}", context.Relative(targetPath));
            }

            var tracked = root.TrackNodes(moving.Select(u => (SyntaxNode)u.Decl).Concat(initializerEdits.Select(e => (SyntaxNode)e.Node)));
            foreach (var edit in initializerEdits)
            {
                var current = tracked.GetCurrentNode(edit.Node)!;
                tracked = tracked.ReplaceNode(current, ParseExpression($"{edit.ClassName}.{edit.Name}").WithTriviaFrom(current));
            }

            if (moving.Count > 0)
            {
                tracked = tracked.RemoveNodes(moving.Select(u => tracked.GetCurrentNode(u.Decl)!), SyntaxRemoveOptions.KeepNoTrivia)!;
            }

            if (!tracked.DescendantNodes().OfType<MemberDeclarationSyntax>().Any(m => m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                File.Delete(file);
                context.Report.Change(Name, "Deleted (all types moved)", context.Relative(file));
            }
            else
            {
                FileSystem.WriteText(file, tracked.ToFullString());
            }
        }
    }

    /// <summary>A moved file: [AutoMap] attributes removed, entity consts redirected, usings that no longer resolve removed.</summary>
    private static CompilationUnitSyntax RewriteMoved(CompilationUnitSyntax root, List<Unit> units,
        Dictionary<Unit, (string ClassName, List<IFieldSymbol> Fields)> constsClasses, UsingFilter filter)
    {
        var attributeSpans = units.SelectMany(u => u.Maps).Select(m => m.AttributeSpan).ToHashSet();
        var constEdits = units.SelectMany(u => u.Consts.Select(c => (Unit: u, Const: c))).ToDictionary(x => x.Const.AccessSpan, x => ConstReplacement(x.Unit, x.Const, constsClasses));

        var rewritten = (CompilationUnitSyntax)new MoveRewriter(attributeSpans, constEdits).Visit(root);
        return FilterUsings(rewritten, filter);
    }

    private static string ExtractedFile(CompilationUnitSyntax root, Unit unit,
        Dictionary<Unit, (string ClassName, List<IFieldSymbol> Fields)> constsClasses, UsingFilter filter)
    {
        var attributeSpans = unit.Maps.Select(m => m.AttributeSpan).ToHashSet();
        var constEdits = unit.Consts.ToDictionary(c => c.AccessSpan, c => ConstReplacement(unit, c, constsClasses));
        var decl = (MemberDeclarationSyntax)new MoveRewriter(attributeSpans, constEdits).Visit(unit.Decl)!;

        var usings = UsingDirectives(root, unit.Decl)
            .Select(u => u.WithoutTrivia().ToString())
            .Distinct()
            .Where(u => KeepUsing(u, filter));

        var sb = new StringBuilder();
        foreach (var u in usings) sb.AppendLine(u);
        if (sb.Length > 0) sb.AppendLine();
        if (unit.Namespace.Length > 0)
        {
            sb.AppendLine($"namespace {unit.Namespace};");
            sb.AppendLine();
        }

        // Drop only the blank lines in front of the type; doc comments (/// lines + their line breaks) must stay intact.
        var leading = decl.GetLeadingTrivia().SkipWhile(t => t.IsKind(SyntaxKind.EndOfLineTrivia) || t.IsKind(SyntaxKind.WhitespaceTrivia)).ToList();
        var indent = decl.GetLeadingTrivia().Reverse().TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).Select(t => t.ToString()).FirstOrDefault() ?? "";
        sb.Append(Dedent(indent + decl.WithLeadingTrivia(leading).ToFullString()).TrimEnd());
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>PersonConsts.X when the DTO's namespace sees the consts' namespace (same or nested), else fully qualified.</summary>
    private static string ConstReplacement(Unit unit, ConstRef c, Dictionary<Unit, (string ClassName, List<IFieldSymbol> Fields)> constsClasses)
    {
        var ns = c.Owner.Namespace;
        var className = constsClasses[c.Owner].ClassName;
        var visible = ns.Length == 0 || unit.Namespace == ns || unit.Namespace.StartsWith(ns + ".", StringComparison.Ordinal);
        return visible ? $"{className}.{c.Field.Name}" : $"global::{ns}.{className}.{c.Field.Name}";
    }

    private sealed class MoveRewriter(HashSet<TextSpan> removedAttributes, Dictionary<TextSpan, string> constEdits) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => KeepTrivia(node, base.VisitClassDeclaration(node));

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => KeepTrivia(node, base.VisitRecordDeclaration(node));

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => KeepTrivia(node, base.VisitStructDeclaration(node));

        /// <summary>When every attribute list of a type is removed, its leading trivia (blank lines, doc comments) must stay.</summary>
        private static SyntaxNode? KeepTrivia(MemberDeclarationSyntax original, SyntaxNode? visited) =>
            visited is MemberDeclarationSyntax member && original.AttributeLists.Count > 0 && member.AttributeLists.Count == 0
                ? member.WithLeadingTrivia(original.AttributeLists[0].GetLeadingTrivia())
                : visited;

        public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
        {
            var kept = node.Attributes.Where(a => !removedAttributes.Contains(a.Span)).ToList();
            if (kept.Count == node.Attributes.Count) return base.VisitAttributeList(node);
            if (kept.Count == 0) return null;
            return node.WithAttributes(SeparatedList(kept));
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node) =>
            constEdits.TryGetValue(node.Span, out var replacement)
                ? ParseExpression(replacement).WithTriviaFrom(node)
                : base.VisitMemberAccessExpression(node);

        /// <summary>
        /// "{ get; internal set; }": the type moves to another assembly (contracts), so the code that set the property
        /// (the application layer) could no longer — the accessor becomes public.
        /// </summary>
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
            return visited.Modifiers.Count == 1 && visited.Modifiers[0].IsKind(SyntaxKind.InternalKeyword)
                ? visited.WithModifiers(default).WithLeadingTrivia(visited.Modifiers[0].LeadingTrivia)
                : visited;
        }
    }

    private static CompilationUnitSyntax FilterUsings(CompilationUnitSyntax root, UsingFilter filter)
    {
        var remove = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => !KeepUsing(u.WithoutTrivia().ToString(), filter))
            .ToList();
        return remove.Count == 0 ? root : root.RemoveNodes(remove, SyntaxRemoveOptions.KeepNoTrivia)!;
    }

    /// <summary>Volo/Microsoft namespaces available through the packages of each contract project.</summary>
    private static readonly HashSet<string> SharedExternalNamespaces = new(StringComparer.Ordinal)
    {
        "Volo.Abp", "Volo.Abp.Domain", "Volo.Abp.Localization", "Volo.Abp.Validation", "Volo.Abp.MultiTenancy", "Volo.Abp.Modularity",
        "Volo.Abp.VirtualFileSystem", "Volo.Abp.DependencyInjection", "Volo.Abp.EventBus", "Volo.Abp.Reflection", "Volo.Abp.Settings",
        "Volo.Abp.Threading", "Microsoft.Extensions.Localization", "JetBrains.Annotations"
    };

    private static readonly HashSet<string> ContractsExternalNamespaces = new(SharedExternalNamespaces, StringComparer.Ordinal)
    {
        "Volo.Abp.Application.Dtos", "Volo.Abp.Application.Services", "Volo.Abp.Authorization", "Volo.Abp.Authorization.Permissions",
        "Volo.Abp.Auditing", "Volo.Abp.ObjectExtending", "Volo.Abp.Data", "Volo.Abp.Content", "Microsoft.AspNetCore.Authorization"
    };

    private sealed record UsingFilter(Target Target, HashSet<string> Available, SolutionModel Model, HashSet<string> Used, bool HasUnresolved, HashSet<string> ExtraNamespaces)
    {
        public static UsingFilter For(IEnumerable<Unit> units, Target target, HashSet<string> available, SolutionModel model)
        {
            var list = units.ToList();
            var extra = list.SelectMany(u => u.UnresolvedNames)
                .Where(TypeMappings.AbpTypePackages.ContainsKey)
                .Select(n => TypeMappings.AbpTypePackages[n].Namespace)
                .ToHashSet(StringComparer.Ordinal);
            return new UsingFilter(target, available, model,
                list.SelectMany(u => u.UsedNamespaces).ToHashSet(StringComparer.Ordinal), list.Any(u => u.HasUnresolved), extra);
        }
    }

    /// <summary>Known ABP types used by moved code (BackgroundJobPriority...) need their package in the target project.</summary>
    private void AddAbpTypePackages(MigrationContext context, List<Unit> units, ProjectInfo shared, ProjectInfo contracts)
    {
        foreach (var (target, project) in new[] { (Target.Shared, shared), (Target.Contracts, contracts) })
        {
            var packages = units.Where(u => u.Moving && u.Candidate == target)
                .SelectMany(u => u.UnresolvedNames)
                .Where(TypeMappings.AbpTypePackages.ContainsKey)
                .Select(n => TypeMappings.AbpTypePackages[n].Package)
                .Where(p => p != "Volo.Abp.Core")
                .Distinct();
            foreach (var package in packages)
            {
                if (ProjectWriter.AddPackageReference(project.CsprojPath, package, context.Options.AbpVersion))
                {
                    context.Report.Change(Name, $"Added package {package} (used by moved types)", context.Relative(project.CsprojPath));
                }
            }
        }
    }

    private static bool KeepUsing(string directive, UsingFilter filter)
    {
        var name = directive.Replace("global ", "").Replace("using ", "").Replace("static ", "").TrimEnd(';').Trim();
        if (name.Contains('=')) name = name[(name.IndexOf('=') + 1)..].Trim();
        if (name == "AutoMapper" || name.StartsWith("AutoMapper.", StringComparison.Ordinal)) return false;

        // Solution namespaces: only if something of that namespace exists in (or moves to) the reachable layers.
        if (filter.Model.Namespaces.Contains(name)) return filter.Available.Contains(name);

        // .NET namespaces resolve in the model, so we know exactly whether they are used.
        if (name == "System" || name.StartsWith("System.", StringComparison.Ordinal)) return filter.Used.Contains(name);

        // NuGet namespaces cannot be resolved: keep them only when the type has unresolved names and the target has the package.
        var external = filter.Target == Target.Shared ? SharedExternalNamespaces : ContractsExternalNamespaces;
        return filter.HasUnresolved && (external.Contains(name) || filter.ExtraNamespaces.Contains(name));
    }

    private static IEnumerable<string> UsingNames(CompilationUnitSyntax root, SyntaxNode decl) =>
        UsingDirectives(root, decl).Where(u => u.Alias is null && u.Name is not null).Select(u => u.Name!.ToString());

    private static IEnumerable<UsingDirectiveSyntax> UsingDirectives(CompilationUnitSyntax root, SyntaxNode decl) =>
        root.Usings.Concat(decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings));

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = SyntaxHelpers.SimpleName(attribute.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static MemberDeclarationSyntax? TopLevel(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .LastOrDefault(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

    private static string UniquePath(string path)
    {
        var candidate = path;
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}{i}{Path.GetExtension(path)}");
        }

        return candidate;
    }

    /// <summary>Removes the common indentation (types extracted from a block namespace are indented one level).</summary>
    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join(Environment.NewLine, lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    // ---------- generated code ----------

    private void WriteConstsClasses(MigrationContext context, ProjectInfo shared, Dictionary<Unit, (string ClassName, List<IFieldSymbol> Fields)> constsClasses)
    {
        foreach (var (owner, (className, fields)) in constsClasses)
        {
            var source = context.FindProjectForFile(owner.File)!;
            var folder = Path.GetDirectoryName(Path.GetRelativePath(source.Directory, owner.File)) ?? "";
            var path = UniquePath(Path.Combine(shared.Directory, folder, className + ".cs"));

            var sb = new StringBuilder();
            if (owner.Namespace.Length > 0)
            {
                sb.AppendLine($"namespace {owner.Namespace};");
                sb.AppendLine();
            }

            sb.AppendLine($"/// <summary>Constants of {owner.Name}, shared with DTOs (generated by AbpToVolo).</summary>");
            sb.AppendLine($"public static class {className}");
            sb.AppendLine("{");
            foreach (var field in fields)
            {
                var value = ((VariableDeclaratorSyntax)field.DeclaringSyntaxReferences[0].GetSyntax()).Initializer!.Value.ToString();
                sb.AppendLine($"    public const {field.Type.ToDisplayString()} {field.Name} = {value};");
            }

            sb.AppendLine("}");
            FileSystem.WriteText(path, sb.ToString());
            context.Report.Change(Name, $"Generated {className} ({string.Join(", ", fields.Select(f => f.Name))}) for {owner.Name}", context.Relative(path));
        }
    }

    private void WriteProfile(MigrationContext context, ProjectInfo application, List<Unit> moved)
    {
        var maps = moved.SelectMany(u => u.Maps).DistinctBy(m => (m.Source, m.Destination, m.Reverse)).ToList();
        if (maps.Count == 0)
        {
            return;
        }

        var className = $"{Identifier(context.ModulePrefix)}ApplicationAutoMapperProfile";
        var path = UniquePath(Path.Combine(application.Directory, className + ".cs"));
        className = Path.GetFileNameWithoutExtension(path);

        var sb = new StringBuilder();
        sb.AppendLine("using AutoMapper;");
        sb.AppendLine();
        sb.AppendLine($"namespace {application.RootNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Generated by AbpToVolo: the [AutoMap] attributes of the DTOs moved to Application.Contracts");
        sb.AppendLine("/// (the contracts project does not reference the domain, so the mappings live here).");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public class {className} : Profile");
        sb.AppendLine("{");
        sb.AppendLine($"    public {className}()");
        sb.AppendLine("    {");
        foreach (var map in maps)
        {
            sb.AppendLine($"        CreateMap<{map.Source}, {map.Destination}>(){(map.Reverse ? ".ReverseMap()" : "")};");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        FileSystem.WriteText(path, sb.ToString());
        context.Report.Change(Name, $"Generated AutoMapper profile with {maps.Count} mappings from moved DTOs", context.Relative(path));
    }

    private void WriteModules(MigrationContext context, ProjectInfo domain, ProjectInfo application, ProjectInfo shared, ProjectInfo contracts)
    {
        var prefix = Identifier(context.ModulePrefix);
        var rootNs = domain.RootNamespace;
        var sharedModule = $"{prefix}DomainSharedModule";
        var contractsModule = $"{prefix}ApplicationContractsModule";

        var sb = new StringBuilder();
        sb.AppendLine("using Volo.Abp.Domain;");
        sb.AppendLine("using Volo.Abp.Localization;");
        sb.AppendLine("using Volo.Abp.Modularity;");
        sb.AppendLine("using Volo.Abp.Validation;");
        sb.AppendLine("using Volo.Abp.VirtualFileSystem;");
        var localization = context.LocalizationResources.Where(l => l.ProjectName == shared.Name).ToList();
        foreach (var ns in localization.Select(l => l.Namespace).Distinct()) sb.AppendLine($"using {ns};");
        sb.AppendLine();
        sb.AppendLine($"namespace {rootNs};");
        sb.AppendLine();
        sb.AppendLine("[DependsOn(");
        sb.AppendLine("    typeof(AbpDddDomainSharedModule),");
        sb.AppendLine("    typeof(AbpValidationModule))]");
        sb.AppendLine($"public class {sharedModule} : AbpModule");
        sb.AppendLine("{");
        sb.AppendLine("    public override void ConfigureServices(ServiceConfigurationContext context)");
        sb.AppendLine("    {");
        sb.AppendLine("        Configure<AbpVirtualFileSystemOptions>(options =>");
        sb.AppendLine("        {");
        sb.AppendLine($"            options.FileSets.AddEmbedded<{sharedModule}>();");
        sb.AppendLine("        });");
        foreach (var loc in localization)
        {
            sb.AppendLine();
            sb.AppendLine("        Configure<AbpLocalizationOptions>(options =>");
            sb.AppendLine("        {");
            foreach (var statement in LocalizationCode.OptionsBody(loc).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                sb.AppendLine($"            {statement};");
            }

            sb.AppendLine("        });");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        FileSystem.WriteText(Path.Combine(shared.Directory, sharedModule + ".cs"), sb.ToString());

        FileSystem.WriteText(Path.Combine(contracts.Directory, contractsModule + ".cs"), $$"""
            using Volo.Abp.Application;
            using Volo.Abp.Authorization;
            using Volo.Abp.Modularity;

            namespace {{rootNs}};

            [DependsOn(
                typeof({{sharedModule}}),
                typeof(AbpDddApplicationContractsModule),
                typeof(AbpAuthorizationAbstractionsModule))]
            public class {{contractsModule}} : AbpModule
            {
            }

            """);
        context.Report.Change(Name, $"Generated {sharedModule} and {contractsModule}");

        AddModuleDependency(context, domain, sharedModule, rootNs, n => n.EndsWith("CoreModule", StringComparison.Ordinal) || n.EndsWith("DomainModule", StringComparison.Ordinal));
        AddModuleDependency(context, application, contractsModule, rootNs, n => n.EndsWith("ApplicationModule", StringComparison.Ordinal));

        if (!FileSystem.EnumerateFiles(application.Directory, "*.cs").Any(f => File.ReadAllText(f).Contains("AbpAutoMapperOptions", StringComparison.Ordinal)))
        {
            context.Report.Todo(Name, "The application module does not configure AbpAutoMapperOptions; add " +
                                      "Configure<AbpAutoMapperOptions>(options => options.AddMaps<YourApplicationModule>()) so the generated profile is used.");
        }
    }

    private void AddModuleDependency(MigrationContext context, ProjectInfo project, string dependency, string dependencyNamespace, Func<string, bool> isTarget)
    {
        foreach (var file in FileSystem.EnumerateFiles(project.Directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("AbpModule", StringComparison.Ordinal)) continue;

            var updated = ModuleDependencyEditor.AddDependsOn(text, dependency, dependencyNamespace, isTarget);
            if (updated is not null)
            {
                FileSystem.WriteText(file, updated);
                context.Report.Change(Name, $"[DependsOn] added {dependency}", context.Relative(file));
                return;
            }
        }

        context.Report.Todo(Name, $"Add typeof({dependency}) to the [DependsOn] of the {project.Name} module.");
    }

    private void Summarize(MigrationContext context, List<Unit> units)
    {
        var lines = context.Report.Section("Contract projects");
        var shared = units.Count(u => u.Moving && u.Candidate == Target.Shared);
        var contracts = units.Count(u => u.Moving && u.Candidate == Target.Contracts);
        var blocked = units.Where(u => u.Candidate != Target.None && !u.Moving).ToList();

        lines.Add($"Moved to Domain.Shared: **{shared}** types (enums, constants, localization).");
        lines.Add($"Moved to Application.Contracts: **{contracts}** types (DTOs, app service interfaces, permissions).");
        lines.Add($"Stayed in their project: **{blocked.Count}** candidate types, because they depend on code that must stay (most common reasons below).");
        foreach (var reason in blocked.GroupBy(b => b.BlockReason!.StartsWith("depends on", StringComparison.Ordinal) ? b.BlockReason : b.BlockReason)
                     .OrderByDescending(g => g.Count()).Take(25))
        {
            lines.Add($"- {reason.Count()} × {reason.Key} — e.g. `{string.Join("`, `", reason.Take(3).Select(b => b.Name))}`");
        }

        context.Log.WriteLine($"    moved {shared} types to Domain.Shared, {contracts} to Application.Contracts ({blocked.Count} stayed)");
    }

    private static string Identifier(string prefix) => new(prefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
}
