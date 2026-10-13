using System.Text;
using AbpToVolo.CodeMigration;
using AbpToVolo.Core;
using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.Steps;

/// <summary>
/// Replaces Module Zero with the ABP.io modules, using a semantic model of the whole solution:
///  1. User/Role/Tenant (AbpUser&lt;T&gt;...) → IdentityUser/IdentityRole/Tenant everywhere; IRepository&lt;User, long&gt; → IRepository&lt;IdentityUser, Guid&gt;.
///  2. Custom properties of those classes → ABP module entity extensions (same DB column), and every use
///     user.X → user.GetProperty&lt;T&gt;("X") / user.SetProperty("X", value).
///  3. Constants of those classes → UserConsts/RoleConsts/TenantConsts; inherited Zero constants → their values.
///  4. Zero managers/stores (UserManager : AbpUserManager...) → IdentityUserManager, ITenantManager, ...
///  5. Zero infrastructure and the Boilerplate template services that an ABP.io module replaces (UserAppService,
///     TokenAuthController, Seed...) → moved to _BoilerplateLegacy (excluded from compilation), together with
///     the code that depends only on them. Every moved file is listed with its reason.
///  6. Database: the Zero tables are renamed to Zero_* by a generated migration (snapshot updated) so that ABP.io can
///     create its own AbpUsers/AbpRoles/... tables, and a SQL script copies users, roles and tenants over.
/// </summary>
public sealed class ModuleZeroStep : IMigrationStep
{
    private sealed class ZeroClass
    {
        public required INamedTypeSymbol Symbol { get; init; }
        public required ZeroKind Kind { get; init; }
        public required string File { get; init; }
        public ZeroReplacement? Replacement => ZeroMappings.Replacements[Kind];
        public List<ExtensionProperty> Properties { get; } = [];
        public List<(string Name, string Type, string Value)> Constants { get; } = [];
        public string ConstsClass => Symbol.Name + "Consts";
    }

    private sealed record ExtensionProperty(string Name, string Type, string? MaxLength, bool Required);

    public string Name => "Module Zero → ABP.io modules";

    public bool IsEnabled(MigrationOptions options) => options.ConvertModuleZero;

    public void Execute(MigrationContext context)
    {
        var files = context.Projects.Where(p => p.Layer != ProjectLayer.Tests)
            .SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs"))
            .Where(f => !context.IsExcluded(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var model = SolutionModel.Build(files);

        var zeroClasses = FindZeroClasses(model);
        if (zeroClasses.Count == 0 && !files.Any(f => ZeroMappings.TemplateServices.ContainsKey(Path.GetFileNameWithoutExtension(f))))
        {
            context.Report.Info(Name, "No Module Zero classes found.");
            return;
        }

        context.UsesIdentityModules = true;
        context.ProjectsUsingZero.UnionWith(context.Projects.Where(p => p.Layer is ProjectLayer.Domain or ProjectLayer.EntityFrameworkCore).Select(p => p.Name));
        var byType = new Dictionary<INamedTypeSymbol, ZeroClass>(SymbolEqualityComparer.Default);
        foreach (var z in zeroClasses) byType[z.Symbol] = z;
        foreach (var zero in zeroClasses.Where(z => ZeroMappings.IsEntity(z.Kind)))
        {
            CollectMembers(zero, model);
            if (zero.Replacement is { } replacement && zero.Properties.Count > 0)
            {
                context.ExtensionProperties[replacement.Name] = zero.Properties.ToDictionary(p => p.Name, p => p.Type, StringComparer.Ordinal);
            }
        }

        // 5. Decide which files go to the legacy folder (before rewriting, on the original code).
        var legacy = FindLegacyFiles(context, model, zeroClasses);

        // 1-4. Rewrite every other file.
        var rewritten = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            context.Progress(i + 1, files.Count, context.Relative(file));
            if (legacy.ContainsKey(file)) continue;

            var tree = model.TreeOf[file];
            var rewriter = new ZeroRewriter(model.Model(tree), byType);
            var root = rewriter.Visit(tree.GetRoot())!;
            if (!rewriter.Changed) continue;

            var code = ModuleMethodEditor.AddUsings(root.ToFullString(), rewriter.Usings);
            FileSystem.WriteText(file, code, FileSystem.ReadText(file).Encoding);
            rewritten++;
            foreach (var change in rewriter.Changes.Distinct()) context.Report.Change(Name, change, context.Relative(file));
            foreach (var todo in rewriter.Todos.Distinct()) context.Report.Todo(Name, todo, context.Relative(file));
        }

        MoveToLegacy(context, legacy);
        RepairProtectedFiles(context, model, legacy, zeroClasses);
        RemoveNotificationUsages(context, model, legacy);
        ResolveIdentityAmbiguity(context);
        WriteConstants(context, zeroClasses);
        var cleaned = UsingCleaner.RemoveVanishedNamespaces(context, model.Namespaces);
        if (cleaned > 0) context.Report.Change(Name, $"Removed usings of namespaces that only contained legacy code ({cleaned} files)");
        WriteEntityExtensions(context, zeroClasses);
        ZeroDatabase.Prepare(context, zeroClasses.Where(z => ZeroMappings.IsEntity(z.Kind)).Select(z => z.Symbol.ToDisplayString()).ToList(),
            zeroClasses.Where(z => ZeroMappings.IsEntity(z.Kind)).SelectMany(z => z.Properties.Select(p => (z.Kind, p.Name))).ToList());

        var lines = context.Report.Section("Module Zero");
        lines.Add($"Replaced classes: {string.Join(", ", zeroClasses.Select(z => $"`{z.Symbol.Name}` → {(z.Replacement is null ? "(none)" : $"`{z.Replacement.Name}`")}"))}.");
        foreach (var zero in zeroClasses.Where(z => z.Properties.Count > 0))
        {
            lines.Add($"`{zero.Symbol.Name}` custom properties → module entity extensions of `{zero.Replacement!.Name}`: " +
                      string.Join(", ", zero.Properties.Select(p => $"`{p.Name}` ({p.Type})")) + ".");
        }

        lines.Add($"{rewritten} files rewritten; {legacy.Count} files moved to `{FileSystem.LegacyFolder}` (excluded from compilation):");
        foreach (var group in legacy.GroupBy(l => l.Value).OrderByDescending(g => g.Count()))
        {
            lines.Add($"- {group.Count()} × {group.Key}: `{string.Join("`, `", group.Take(6).Select(g => Path.GetFileNameWithoutExtension(g.Key)))}`{(group.Count() > 6 ? " …" : "")}");
        }

        lines.Add("User, role and tenant ids are Guid in ABP.io: variables, DTO properties and foreign keys that held a Zero user/tenant id (long/int) must become Guid.");
        context.Log.WriteLine($"{zeroClasses.Count} Zero classes replaced, {rewritten} files rewritten, {legacy.Count} files moved to {FileSystem.LegacyFolder}");
    }

    // ---------- discovery ----------

    private static List<ZeroClass> FindZeroClasses(SolutionModel model)
    {
        var result = new List<ZeroClass>();
        foreach (var (tree, file) in model.FileOf)
        {
            var semantic = model.Model(tree);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var kind = cls.BaseList?.Types.Select(t => SyntaxHelpers.SimpleName(t.Type))
                    .Select(n => ZeroMappings.BaseTypes.TryGetValue(n, out var k) ? k : (ZeroKind?)null)
                    .FirstOrDefault(k => k is not null);
                if (kind is null || semantic.GetDeclaredSymbol(cls) is not INamedTypeSymbol symbol) continue;
                if (result.Any(r => SymbolEqualityComparer.Default.Equals(r.Symbol, symbol))) continue;
                result.Add(new ZeroClass { Symbol = symbol, Kind = kind.Value, File = file });
            }
        }

        return result;
    }

    private static void CollectMembers(ZeroClass zero, SolutionModel model)
    {
        foreach (var member in zero.Symbol.GetMembers())
        {
            if (member is IFieldSymbol { IsConst: true } constant &&
                constant.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var value })
            {
                zero.Constants.Add((constant.Name, constant.Type.ToDisplayString(), value.ToString()));
                continue;
            }

            if (member is not IPropertySymbol { IsStatic: false, IsOverride: false, DeclaredAccessibility: Accessibility.Public, GetMethod: not null, SetMethod: not null } property)
            {
                continue;
            }

            if (!IsExtensionPropertyType(property.Type)) continue;

            var syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
            var attributes = syntax?.AttributeLists.SelectMany(a => a.Attributes).ToList() ?? [];
            var maxLength = attributes.FirstOrDefault(a => SyntaxHelpers.SimpleName(a.Name) is "MaxLength" or "StringLength" or "MaxLengthAttribute" or "StringLengthAttribute")
                ?.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString();
            if (maxLength is not null && zero.Constants.Any(c => c.Name == maxLength))
            {
                maxLength = $"{zero.ConstsClass}.{maxLength}";
            }

            var required = attributes.Any(a => SyntaxHelpers.SimpleName(a.Name) is "Required" or "RequiredAttribute");
            zero.Properties.Add(new ExtensionProperty(property.Name, TypeName(property.Type), maxLength, required));
        }

        // Constants declared after properties that reference them are resolved above only if already collected.
        for (var i = 0; i < zero.Properties.Count; i++)
        {
            var p = zero.Properties[i];
            if (p.MaxLength is not null && !p.MaxLength.Contains('.') && zero.Constants.Any(c => c.Name == p.MaxLength))
            {
                zero.Properties[i] = p with { MaxLength = $"{zero.ConstsClass}.{p.MaxLength}" };
            }
        }
    }

    private static bool IsExtensionPropertyType(ITypeSymbol type)
    {
        var underlying = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;
        return underlying.SpecialType is SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Int16 or SpecialType.System_Int32
                   or SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single
                   or SpecialType.System_DateTime or SpecialType.System_Byte
               || underlying.TypeKind == TypeKind.Enum
               || underlying.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.TimeSpan";
    }

    internal static string TypeName(ITypeSymbol type) =>
        type.SpecialType != SpecialType.None || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n && n.TypeArguments[0].SpecialType != SpecialType.None
            ? type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // ---------- legacy (quarantine) ----------

    private static Dictionary<string, string> FindLegacyFiles(MigrationContext context, SolutionModel model, List<ZeroClass> zeroClasses)
    {
        var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var zero in zeroClasses)
        {
            legacy.TryAdd(zero.File, zero.Replacement is null
                ? $"Module Zero infrastructure ({zero.Kind}) without ABP.io counterpart"
                : $"Module Zero class replaced by {zero.Replacement.Name}");
        }

        foreach (var (tree, file) in model.FileOf)
        {
            if (legacy.ContainsKey(file)) continue;
            var root = tree.GetRoot();

            if (IsProtected(root)) continue;

            var template = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Where(t => ZeroMappings.IsTemplateClass(t.Identifier.Text,
                    t.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } ns ? SolutionModel.FullNamespace(ns) : null))
                .Select(t => t.Identifier.Text)
                .FirstOrDefault();
            if (template is not null)
            {
                legacy[file] = $"Boilerplate template code replaced by {ZeroMappings.TemplateServices[template]}";
                continue;
            }

            var semantic = model.Model(tree);
            var trigger = root.DescendantNodes().OfType<SimpleNameSyntax>()
                .Where(n => ZeroMappings.LegacyTriggers.Contains(n.Identifier.Text))
                .FirstOrDefault(n => semantic.GetSymbolInfo(n).Symbol is null);
            if (trigger is not null)
            {
                legacy[file] = $"uses Module Zero infrastructure ({trigger.Identifier.Text})";
            }
        }

        // Code that depends on moved types that have no replacement goes too (until nothing changes).
        var replaced = zeroClasses.Where(z => z.Replacement is not null).Select(z => z.Symbol).ToHashSet<ISymbol>(SymbolEqualityComparer.Default);
        bool changed;
        do
        {
            changed = false;
            var legacyTypes = legacy.Keys
                .SelectMany(f => model.TreeOf[f].GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Select(t => model.Model(model.TreeOf[f]).GetDeclaredSymbol(t)))
                .OfType<INamedTypeSymbol>()
                .Where(t => !replaced.Contains(t))
                .ToHashSet<ISymbol>(SymbolEqualityComparer.Default);
            var legacyNames = legacyTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var (tree, file) in model.FileOf)
            {
                if (legacy.ContainsKey(file) || IsProtected(tree.GetRoot())) continue;
                var semantic = model.Model(tree);
                var dependency = tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
                    .Where(n => legacyNames.Contains(n.Identifier.Text))
                    .Select(n => semantic.GetSymbolInfo(n).Symbol)
                    .Select(s => s as INamedTypeSymbol ?? s?.ContainingType)
                    .FirstOrDefault(t => t is not null && legacyTypes.Contains(t.OriginalDefinition));
                if (dependency is not null)
                {
                    legacy[file] = $"depends on legacy code ({dependency.Name})";
                    changed = true;
                }
            }
        }
        while (changed);

        return legacy;
    }

    /// <summary>
    /// Module classes and DbContexts never go to the legacy folder (the solution cannot work without them): the code in
    /// them that uses legacy types is removed instead (see <see cref="RepairProtectedFiles"/>).
    /// </summary>
    private static bool IsProtected(SyntaxNode root) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(c => SyntaxHelpers.HasBaseType(c, "AbpModule", "AbpDbContext", "AbpZeroDbContext"));

    /// <summary>Removes [DependsOn] entries, statements and members of module/DbContext files that use legacy types.</summary>
    private void RepairProtectedFiles(MigrationContext context, SolutionModel model, Dictionary<string, string> legacy, List<ZeroClass> zeroClasses)
    {
        var replaced = zeroClasses.Where(z => z.Replacement is not null).Select(z => z.Symbol.Name).ToHashSet(StringComparer.Ordinal);
        var legacyNames = legacy.Keys
            .SelectMany(f => model.TreeOf[f].GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text))
            .Concat(ZeroMappings.LegacyTriggers)
            .Where(n => !replaced.Contains(n))
            .ToHashSet(StringComparer.Ordinal);
        if (legacyNames.Count == 0) return;

        foreach (var (tree, file) in model.FileOf)
        {
            if (legacy.ContainsKey(file) || !File.Exists(file)) continue;
            var original = File.ReadAllText(file);
            var root = CSharpSyntaxTree.ParseText(original).GetRoot();
            if (!IsProtected(root)) continue;

            var remover = new LegacyReferenceRemover(LegacyReferenceRemover.WithMemberNames(root, legacyNames), original.Contains("\r\n") ? "\r\n" : "\n");
            var updated = remover.Visit(root)!;
            if (remover.Removed.Count == 0) continue;

            FileSystem.WriteText(file, updated.ToFullString());
            foreach (var removed in remover.Removed.Distinct())
            {
                context.Report.Todo(Name, $"Removed code that used legacy Boilerplate types ({removed}); re-create it with ABP.io APIs if still needed.", context.Relative(file));
            }
        }
    }

    /// <summary>Boilerplate notification system: no ABP.io counterpart. Only the code using it is commented out.</summary>
    private static readonly HashSet<string> SurgicalTriggers = new(StringComparer.Ordinal)
    {
        "INotificationPublisher", "IRealTimeNotifier", "IUserNotificationManager", "INotificationSubscriptionManager", "INotificationStore",
        "IOnlineClientManager", "NotificationData", "LocalizableMessageNotificationData", "MessageNotificationData", "UserNotification",
        "TenantNotification", "UserNotificationInfo", "NotificationDefinition", "INotificationDefinitionManager"
    };

    /// <summary>Comments out the code that uses the Boilerplate notification system (files stay in the build).</summary>
    private void RemoveNotificationUsages(MigrationContext context, SolutionModel model, Dictionary<string, string> legacy)
    {
        foreach (var (tree, file) in model.FileOf)
        {
            if (legacy.ContainsKey(file) || !File.Exists(file)) continue;
            var original = File.ReadAllText(file);
            if (!SurgicalTriggers.Any(t => original.Contains(t, StringComparison.Ordinal))) continue;

            var root = CSharpSyntaxTree.ParseText(original).GetRoot();
            var triggers = root.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.Text).Where(SurgicalTriggers.Contains).ToHashSet(StringComparer.Ordinal);
            if (triggers.Count == 0) continue;

            var remover = new LegacyReferenceRemover(LegacyReferenceRemover.WithMemberNames(root, triggers), original.Contains("\r\n") ? "\r\n" : "\n");
            var updated = remover.Visit(root)!;
            if (remover.Removed.Count == 0) continue;
            FileSystem.WriteText(file, updated.ToFullString());
            context.Report.Todo(Name, $"Boilerplate notifications ({string.Join(", ", triggers)}) have no ABP.io counterpart: the code using them was commented out " +
                                      "(use distributed events + SignalR, or the ABP.io Notification system of the commercial edition).", context.Relative(file));
        }
    }

    /// <summary>
    /// "using IdentityUser = Volo.Abp.Identity.IdentityUser;" where a file imports both Volo.Abp.Identity and
    /// Microsoft.AspNetCore.Identity (both declare IdentityUser/IdentityRole).
    /// </summary>
    private void ResolveIdentityAmbiguity(MigrationContext context)
    {
        foreach (var file in context.Projects.SelectMany(p => FileSystem.EnumerateFiles(p.Directory, "*.cs")).Where(f => !context.IsExcluded(f)))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("using Microsoft.AspNetCore.Identity;", StringComparison.Ordinal) || !text.Contains("using Volo.Abp.Identity;", StringComparison.Ordinal)) continue;

            var aliases = new[] { "IdentityUser", "IdentityRole", "IdentityUserClaim", "IdentityRoleClaim", "IdentityUserLogin", "IdentityUserRole", "IdentityUserToken" }
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{n}\b(?!\s*=)") && !text.Contains($"using {n} =", StringComparison.Ordinal))
                .ToList();
            if (aliases.Count == 0) continue;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var directives = aliases.Select(a => UsingDirective(NameEquals(a), ParseName($"Volo.Abp.Identity.{a}")).NormalizeWhitespace().WithTrailingTrivia(EndOfLine(nl)));
            FileSystem.WriteText(file, root.WithUsings(root.Usings.AddRange(directives)).ToFullString());
            context.Report.Change(Name, $"Alias using for {string.Join(", ", aliases)} (Volo.Abp.Identity vs Microsoft.AspNetCore.Identity)", context.Relative(file));
        }
    }

    private sealed class LegacyReferenceRemover(HashSet<string> legacyNames, string newLine) : CSharpSyntaxRewriter
    {
        public List<string> Removed { get; } = [];

        /// <summary>Adds the names of fields, properties and parameters whose type is one of <paramref name="typeNames"/>.</summary>
        public static HashSet<string> WithMemberNames(SyntaxNode root, HashSet<string> typeNames)
        {
            bool Uses(TypeSyntax? type) => type is not null && type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(n => typeNames.Contains(n.Identifier.Text));
            var names = new HashSet<string>(typeNames, StringComparer.Ordinal);
            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => Uses(f.Declaration.Type)))
                foreach (var v in field.Declaration.Variables) names.Add(v.Identifier.Text);
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(p => Uses(p.Type))) names.Add(property.Identifier.Text);
            foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>().Where(p => Uses(p.Type))) names.Add(parameter.Identifier.Text);
            foreach (var local in root.DescendantNodes().OfType<VariableDeclarationSyntax>().Where(v => Uses(v.Type)))
                foreach (var v in local.Variables) names.Add(v.Identifier.Text);
            return names;
        }

        public override SyntaxNode? VisitParameterList(ParameterListSyntax node)
        {
            var kept = node.Parameters.Where(p => !(p.Type is not null && LegacyName(p.Type) is { } name && Note(name))).ToList();
            if (kept.Count == node.Parameters.Count) return base.VisitParameterList(node);
            var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, kept.Count - 1));
            return node.WithParameters(SeparatedList(kept.Select((p, i) => i == 0 ? p.WithoutLeadingTrivia() : p.WithLeadingTrivia(Space)), separators));
        }

        private string? LegacyName(SyntaxNode node) =>
            node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Select(n => n.Identifier.Text).FirstOrDefault(legacyNames.Contains);

        public override SyntaxNode? VisitAttributeArgumentList(AttributeArgumentListSyntax node)
        {
            var kept = node.Arguments.Where(a => !(a.Expression is TypeOfExpressionSyntax t && LegacyName(t.Type) is { } name && Note(name))).ToList();
            if (kept.Count == node.Arguments.Count) return base.VisitAttributeArgumentList(node);

            var multiLine = node.ToFullString().Contains('\n');
            var separators = Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(multiLine ? EndOfLine(newLine) : Space), Math.Max(0, kept.Count - 1));
            return node.WithArguments(SeparatedList(kept, separators));
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var statements = new List<StatementSyntax>();
            var pending = new List<SyntaxTrivia>();
            foreach (var statement in node.Statements)
            {
                if (statement is not BlockSyntax && LegacyName(statement) is { } name)
                {
                    Note(name);
                    pending.AddRange(SyntaxHelpers.CommentLines(SyntaxHelpers.Indentation(statement), newLine,
                        SyntaxHelpers.CommentOutLines(statement, $"uses legacy Boilerplate code ({name}) moved to {FileSystem.LegacyFolder}.")));
                    continue;
                }

                var visited = (StatementSyntax)Visit(statement)!;
                if (pending.Count > 0)
                {
                    visited = visited.WithLeadingTrivia(pending.Concat(visited.GetLeadingTrivia()));
                    pending.Clear();
                }

                statements.Add(visited);
            }

            var close = pending.Count > 0 ? node.CloseBraceToken.WithLeadingTrivia(pending.Concat(node.CloseBraceToken.LeadingTrivia)) : node.CloseBraceToken;
            return node.WithStatements(List(statements)).WithCloseBraceToken(close);
        }

        // Fields/properties of a legacy type are removed (returning null removes the member).
        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) =>
            LegacyName(node.Declaration.Type) is { } name && Note(name) ? null : base.VisitFieldDeclaration(node);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node) =>
            LegacyName(node.Type) is { } name && Note(name) ? null : base.VisitPropertyDeclaration(node);

        private bool Note(string name)
        {
            Removed.Add(name);
            return true;
        }
    }

    private void MoveToLegacy(MigrationContext context, Dictionary<string, string> legacy)
    {
        foreach (var group in legacy.GroupBy(l => context.FindProjectForFile(l.Key)))
        {
            var project = group.Key;
            if (project is null) continue;

            foreach (var (file, reason) in group)
            {
                var target = Path.Combine(project.Directory, FileSystem.LegacyFolder, Path.GetRelativePath(project.Directory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
                context.Report.Change(Name, $"Moved to {FileSystem.LegacyFolder}: {reason}", context.Relative(target));
            }

            ExcludeLegacyFolder(project.CsprojPath);
        }

        if (legacy.Count > 0)
        {
            context.Report.Todo(Name, $"{legacy.Count} files were moved to {FileSystem.LegacyFolder} folders (not compiled). Port the business logic you still " +
                                      "need from them to the ABP.io modules (see the 'Module Zero' section), then delete the folders.");
        }
    }

    internal static void ExcludeLegacyFolder(string csproj)
    {
        var doc = System.Xml.Linq.XDocument.Load(csproj);
        if (doc.Descendants("Compile").Any(c => c.Attribute("Remove")?.Value.StartsWith(FileSystem.LegacyFolder, StringComparison.Ordinal) == true)) return;
        doc.Root!.Add(new System.Xml.Linq.XElement("ItemGroup",
            new System.Xml.Linq.XComment(" Boilerplate code without ABP.io counterpart (kept for reference, not compiled) "),
            new System.Xml.Linq.XElement("Compile", new System.Xml.Linq.XAttribute("Remove", $"{FileSystem.LegacyFolder}\\**")),
            new System.Xml.Linq.XElement("None", new System.Xml.Linq.XAttribute("Include", $"{FileSystem.LegacyFolder}\\**"))));
        ProjectWriter.Save(doc, csproj);
    }

    // ---------- generated code ----------

    private void WriteConstants(MigrationContext context, List<ZeroClass> zeroClasses)
    {
        foreach (var zero in zeroClasses.Where(z => z.Constants.Count > 0))
        {
            var project = context.FindProjectForFile(zero.File)!;
            var folder = Path.GetDirectoryName(Path.GetRelativePath(project.Directory, zero.File)) ?? "";
            var ns = zero.Symbol.ContainingNamespace.IsGlobalNamespace ? null : zero.Symbol.ContainingNamespace.ToDisplayString();
            var sb = new StringBuilder();
            if (ns is not null)
            {
                sb.AppendLine($"namespace {ns};");
                sb.AppendLine();
            }

            sb.AppendLine($"/// <summary>Constants of the former Module Zero class {zero.Symbol.Name} (generated by AbpToVolo).</summary>");
            sb.AppendLine($"public static class {zero.ConstsClass}");
            sb.AppendLine("{");
            foreach (var (name, type, value) in zero.Constants)
            {
                var resolved = zero.Constants.Any(c => c.Name == value) ? value : value;
                sb.AppendLine($"    public const {type} {name} = {resolved};");
            }

            sb.AppendLine("}");
            var path = Path.Combine(project.Directory, folder, zero.ConstsClass + ".cs");
            FileSystem.WriteText(path, sb.ToString());
            context.Report.Change(Name, $"Generated {zero.ConstsClass} with the constants of {zero.Symbol.Name}", context.Relative(path));
        }
    }

    /// <summary>ABP.io template pattern: ModuleExtensionConfigurator (Domain) + EfCoreEntityExtensionMappings (EF Core).</summary>
    private void WriteEntityExtensions(MigrationContext context, List<ZeroClass> zeroClasses)
    {
        var extended = zeroClasses.Where(z => z.Properties.Count > 0 && z.Replacement is not null).ToList();
        if (extended.Count == 0) return;

        var domain = context.FindProject(ProjectLayer.Domain);
        var efCore = context.FindProject(ProjectLayer.EntityFrameworkCore);
        if (domain is null || efCore is null) return;

        var prefix = new string(context.ModulePrefix.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        var configurator = $"{prefix}ModuleExtensionConfigurator";
        var mappings = $"{prefix}EfCoreEntityExtensionMappings";

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using Volo.Abp.Identity;");
        sb.AppendLine("using Volo.Abp.ObjectExtending;");
        sb.AppendLine("using Volo.Abp.Threading;");
        sb.AppendLine();
        sb.AppendLine($"namespace {domain.RootNamespace};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Generated by AbpToVolo: the custom properties of the former Module Zero User/Role/Tenant classes,");
        sb.AppendLine("/// defined as ABP.io module entity extensions (they appear in the module DTOs, APIs and UI).");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public static class {configurator}");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly OneTimeRunner OneTimeRunner = new OneTimeRunner();");
        sb.AppendLine();
        sb.AppendLine("    public static void Configure()");
        sb.AppendLine("    {");
        sb.AppendLine("        OneTimeRunner.Run(() =>");
        sb.AppendLine("        {");
        foreach (var group in extended.GroupBy(z => z.Kind == ZeroKind.Tenant ? "TenantManagement" : "Identity"))
        {
            sb.AppendLine("            ObjectExtensionManager.Instance.Modules()");
            sb.AppendLine($"                .Configure{group.Key}({group.Key.ToLowerInvariant()} =>");
            sb.AppendLine("                {");
            foreach (var zero in group)
            {
                var (method, variable) = zero.Kind switch
                {
                    ZeroKind.User => ("ConfigureUser", "user"),
                    ZeroKind.Role => ("ConfigureRole", "role"),
                    _ => ("ConfigureTenant", "tenant")
                };
                sb.AppendLine($"                    {group.Key.ToLowerInvariant()}.{method}({variable} =>");
                sb.AppendLine("                    {");
                foreach (var property in zero.Properties)
                {
                    sb.AppendLine($"                        {variable}.AddOrUpdateProperty<{property.Type}>(\"{property.Name}\");");
                }

                sb.AppendLine("                    });");
            }

            sb.AppendLine("                });");
        }

        sb.AppendLine("        });");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        var configuratorPath = Path.Combine(domain.Directory, configurator + ".cs");
        FileSystem.WriteText(configuratorPath, sb.ToString());

        sb.Clear();
        sb.AppendLine("using System;");
        sb.AppendLine("using Volo.Abp.Identity;");
        sb.AppendLine("using Volo.Abp.ObjectExtending;");
        sb.AppendLine("using Volo.Abp.TenantManagement;");
        sb.AppendLine("using Volo.Abp.Threading;");
        sb.AppendLine();
        sb.AppendLine($"namespace {efCore.RootNamespace}.EntityFrameworkCore;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Generated by AbpToVolo: maps the entity extensions to the existing database columns.</summary>");
        sb.AppendLine($"public static class {mappings}");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly OneTimeRunner OneTimeRunner = new OneTimeRunner();");
        sb.AppendLine();
        sb.AppendLine("    public static void Configure()");
        sb.AppendLine("    {");
        sb.AppendLine($"        global::{domain.RootNamespace}.{configurator}.Configure();");
        sb.AppendLine();
        sb.AppendLine("        OneTimeRunner.Run(() =>");
        sb.AppendLine("        {");
        foreach (var zero in extended)
        {
            foreach (var property in zero.Properties)
            {
                var build = new List<string>();
                if (property.MaxLength is not null) build.Add($"propertyBuilder.HasMaxLength({QualifyConst(property.MaxLength, zero)});");
                if (property.Required) build.Add("propertyBuilder.IsRequired();");
                var lambda = build.Count == 0 ? "" : $", (entityBuilder, propertyBuilder) => {{ {string.Join(" ", build)} }}";
                sb.AppendLine($"            ObjectExtensionManager.Instance.MapEfCoreProperty<{zero.Replacement!.Name}, {property.Type}>(\"{property.Name}\"{lambda});");
            }
        }

        sb.AppendLine("        });");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        var mappingsPath = Path.Combine(efCore.Directory, "EntityFrameworkCore", mappings + ".cs");
        FileSystem.WriteText(mappingsPath, sb.ToString());

        Wire(context, domain, n => n.EndsWith("DomainModule", StringComparison.Ordinal) || n.EndsWith("CoreModule", StringComparison.Ordinal),
            $"{configurator}.Configure();", domain.RootNamespace);
        Wire(context, efCore, n => n.EndsWith("Module", StringComparison.Ordinal), $"{mappings}.Configure();", $"{efCore.RootNamespace}.EntityFrameworkCore");

        context.Report.Change(Name, $"Generated {configurator} and {mappings} (module entity extensions)", context.Relative(configuratorPath));
    }

    private static string QualifyConst(string expression, ZeroClass zero) =>
        expression.StartsWith(zero.ConstsClass + ".", StringComparison.Ordinal) && zero.Symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"global::{ns.ToDisplayString()}.{expression}"
            : expression;

    private void Wire(MigrationContext context, ProjectInfo project, Func<string, bool> isModule, string statement, string ns)
    {
        foreach (var file in FileSystem.EnumerateFiles(project.Directory, "*.cs"))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("AbpModule", StringComparison.Ordinal)) continue;
            var updated = ModuleMethodEditor.AddStatement(text, isModule, "PreConfigureServices", "ServiceConfigurationContext context", statement);
            if (updated is null) continue;
            FileSystem.WriteText(file, ModuleMethodEditor.AddUsings(updated, [ns]));
            context.Report.Change(Name, $"PreConfigureServices calls {statement}", context.Relative(file));
            return;
        }

        context.Report.Todo(Name, $"Call {statement} in the PreConfigureServices of the {project.Name} module.");
    }

    // ---------- rewriter ----------

    private sealed class ZeroRewriter(SemanticModel model, Dictionary<INamedTypeSymbol, ZeroClass> zero) : CSharpSyntaxRewriter
    {
        public bool Changed { get; private set; }
        public HashSet<string> Usings { get; } = new(StringComparer.Ordinal);
        public List<string> Changes { get; } = [];
        public List<string> Todos { get; } = [];

        private ZeroClass? ZeroOf(ISymbol? symbol) =>
            symbol is INamedTypeSymbol type && zero.TryGetValue(type.OriginalDefinition, out var z) ? z : null;

        private ZeroClass? ZeroTypeOf(ExpressionSyntax expression)
        {
            var type = model.GetTypeInfo(expression).Type;
            return type is INamedTypeSymbol t && zero.TryGetValue(t.OriginalDefinition, out var z) ? z : null;
        }

        private SyntaxNode Replace(SyntaxNode original, ZeroReplacement replacement, string change)
        {
            Changed = true;
            Usings.Add(replacement.Namespace);
            Changes.Add(change);
            return IdentifierName(replacement.Name).WithTriviaFrom(original);
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            var z = ZeroOf(model.GetSymbolInfo(node).Symbol);
            return z?.Replacement is { } r ? Replace(node, r, $"{z.Symbol.Name} → {r.Name}") : base.VisitQualifiedName(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (ZeroOf(symbol) is { Replacement: { } replacement } z && !IsMemberName(node))
            {
                return Replace(node, replacement, $"{z.Symbol.Name} → {replacement.Name}");
            }

            if (symbol is null && !IsMemberName(node) && ZeroMappings.FrameworkTypes.TryGetValue(node.Identifier.Text, out var framework) && IsTypePosition(node))
            {
                return Replace(node, framework, $"{node.Identifier.Text} → {framework.Name}");
            }

            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
            var args = node.TypeArgumentList.Arguments;

            // IRepository<UserRole, long> → IRepository<IdentityUserRole>: these ABP.io Identity entities have composite keys.
            if (args.Count == 2 && args[0] is IdentifierNameSyntax framework && model.GetSymbolInfo(framework).Symbol is null &&
                framework.Identifier.Text is "UserRole" or "UserLogin" or "UserToken" or "UserOrganizationUnit" or "OrganizationUnitRole")
            {
                Changed = true;
                Changes.Add($"{node.Identifier.Text}<{framework.Identifier.Text}, {args[1]}> → keyless (composite key in ABP.io)");
                Todos.Add($"{ZeroMappings.FrameworkTypes[framework.Identifier.Text].Name} has a composite key in ABP.io; prefer IIdentityUserRepository / IdentityUserManager APIs.");
                return visited.WithTypeArgumentList(visited.TypeArgumentList.WithArguments(SingletonSeparatedList(visited.TypeArgumentList.Arguments[0])));
            }

            // IRepository<UserClaim, long> → IRepository<IdentityUserClaim, Guid> (ABP.io Identity entities have Guid keys).
            if (args.Count == 2 && args[0] is IdentifierNameSyntax guidEntity && model.GetSymbolInfo(guidEntity).Symbol is null &&
                guidEntity.Identifier.Text is "UserClaim" or "RoleClaim" or "OrganizationUnit" or "RolePermissionSetting" or "UserPermissionSetting" or "PermissionSetting" &&
                args[1] is PredefinedTypeSyntax { Keyword.Text: "long" or "int" })
            {
                Changed = true;
                Usings.Add("System");
                return visited.WithTypeArgumentList(visited.TypeArgumentList.WithArguments(
                    visited.TypeArgumentList.Arguments.Replace(visited.TypeArgumentList.Arguments[1], IdentifierName("Guid"))));
            }

            // IRepository<User, long> → IRepository<IdentityUser, Guid>: ABP.io user/role/tenant keys are Guid.
            if (args.Count == 2 && ZeroOf(model.GetSymbolInfo(args[0]).Symbol) is { } entity && ZeroMappings.IsEntity(entity.Kind) &&
                args[1] is PredefinedTypeSyntax { Keyword.Text: "long" or "int" })
            {
                Changed = true;
                Usings.Add("System");
                Changes.Add($"{node.Identifier.Text}<{entity.Symbol.Name}, {args[1]}> key → Guid");
                var newArgs = visited.TypeArgumentList.Arguments.Replace(visited.TypeArgumentList.Arguments[1], IdentifierName("Guid"));
                return visited.WithTypeArgumentList(visited.TypeArgumentList.WithArguments(newArgs));
            }

            return visited;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            // user.NationalCode = value;  →  user.SetProperty("NationalCode", value);
            if (node.Expression is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: MemberAccessExpressionSyntax access } assignment &&
                ZeroTypeOf(access.Expression) is { } z && z.Properties.Any(p => p.Name == access.Name.Identifier.Text))
            {
                Changed = true;
                Usings.Add("Volo.Abp.Data");
                Changes.Add($"{z.Symbol.Name}.{access.Name.Identifier.Text} = … → SetProperty(\"{access.Name.Identifier.Text}\", …)");
                var receiver = (ExpressionSyntax)Visit(access.Expression)!;
                var value = (ExpressionSyntax)Visit(assignment.Right)!;
                return ExpressionStatement(InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver.WithoutTrivia(), IdentifierName("SetProperty")),
                        ArgumentList(SeparatedList([
                            Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(access.Name.Identifier.Text))),
                            Argument(value.WithoutTrivia())
                        ], [Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space)]))))
                    .WithTriviaFrom(node);
            }

            return base.VisitExpressionStatement(node);
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var member = node.Name.Identifier.Text;

            // Static access on the Zero class: User.DefaultPassword → UserConsts.DefaultPassword, User.MaxUserNameLength → 256
            if (ZeroOf(model.GetSymbolInfo(node.Expression).Symbol) is { } staticZero && ZeroMappings.IsEntity(staticZero.Kind))
            {
                if (staticZero.Constants.Any(c => c.Name == member))
                {
                    Changed = true;
                    Changes.Add($"{staticZero.Symbol.Name}.{member} → {staticZero.ConstsClass}.{member}");
                    return ParseExpression($"{staticZero.ConstsClass}.{member}").WithTriviaFrom(node);
                }

                if (ZeroMappings.ConstantsBase(staticZero.Kind) is { } baseName && TypeMappings.ZeroConstants[baseName].TryGetValue(member, out var literal))
                {
                    Changed = true;
                    Changes.Add($"{staticZero.Symbol.Name}.{member} → {literal}");
                    return ParseExpression(literal).WithTriviaFrom(node);
                }
            }

            // Instance access on a Zero entity: custom property → GetProperty, renamed base member → new name
            if (ZeroTypeOf(node.Expression) is { } z && ZeroMappings.IsEntity(z.Kind))
            {
                var property = z.Properties.FirstOrDefault(p => p.Name == member);
                var isAssignmentTarget = node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node;
                if (property is not null && isAssignmentTarget)
                {
                    Todos.Add($"'{member}' is now an extra property: replace this assignment with SetProperty(\"{member}\", value).");
                }
                else if (property is not null)
                {
                    Changed = true;
                    Usings.Add("Volo.Abp.Data");
                    Changes.Add($"{z.Symbol.Name}.{member} → GetProperty<{property.Type}>(\"{member}\")");
                    if (node.Ancestors().Any(a => a is LambdaExpressionSyntax))
                    {
                        Todos.Add($"'{member}' is now an extra property; inside an EF Core query use EF.Property<{property.Type}>(x, \"{member}\") instead of GetProperty.");
                    }

                    var receiver = (ExpressionSyntax)Visit(node.Expression)!;
                    return InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver,
                                GenericName(Identifier("GetProperty"), TypeArgumentList(SingletonSeparatedList(ParseTypeName(property.Type))))),
                            ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(member))))))
                        .WithTriviaFrom(node);
                }

                if (ZeroMappings.MemberRenames.TryGetValue(z.Kind, out var renames) && renames.TryGetValue(member, out var newName))
                {
                    Changed = true;
                    Changes.Add($"{z.Symbol.Name}.{member} → {newName}");
                    var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
                    return visited.WithName(IdentifierName(newName).WithTriviaFrom(visited.Name));
                }
            }

            return base.VisitMemberAccessExpression(node);
        }

        /// <summary>new { user.NationalCode } → new { NationalCode = user.GetProperty&lt;string&gt;("NationalCode") }</summary>
        public override SyntaxNode? VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node)
        {
            var visited = (AnonymousObjectMemberDeclaratorSyntax)base.VisitAnonymousObjectMemberDeclarator(node)!;
            return node.NameEquals is null && node.Expression is MemberAccessExpressionSyntax original && visited.Expression is not MemberAccessExpressionSyntax
                ? visited.WithNameEquals(NameEquals(original.Name.Identifier.Text))
                : visited;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            // identityResult.CheckErrors(LocalizationManager) → identityResult.CheckErrors() (Volo.Abp.Identity)
            if (visited.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "CheckErrors" } &&
                visited.ArgumentList.Arguments.Count == 1 && visited.ArgumentList.Arguments[0].ToString() == "LocalizationManager")
            {
                Changed = true;
                Usings.Add("Volo.Abp.Identity");
                Changes.Add("CheckErrors(LocalizationManager) → CheckErrors()");
                return visited.WithArgumentList(ArgumentList());
            }

            return visited;
        }

        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            if (ZeroOf(model.GetSymbolInfo(node.Type).Symbol) is { } z && ZeroMappings.IsEntity(z.Kind))
            {
                Todos.Add($"new {z.Symbol.Name}(...) → create {z.Replacement!.Name} with its constructor (Guid id, ...) and set extension properties with SetProperty.");
            }

            return base.VisitObjectCreationExpression(node);
        }

        private static bool IsMemberName(IdentifierNameSyntax node) =>
            node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node ||
            node.Parent is MemberBindingExpressionSyntax;

        private static bool IsTypePosition(IdentifierNameSyntax node) => node.Parent is
            TypeArgumentListSyntax or VariableDeclarationSyntax or ParameterSyntax or PropertyDeclarationSyntax or
            MethodDeclarationSyntax or ObjectCreationExpressionSyntax or TypeOfExpressionSyntax or SimpleBaseTypeSyntax or
            NullableTypeSyntax or ArrayTypeSyntax or CastExpressionSyntax;
    }
}
