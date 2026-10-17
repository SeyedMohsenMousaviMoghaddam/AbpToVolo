using System.Text.RegularExpressions;
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
/// Compiler-guided fixes. Before the build, the converter only knows the solution's own types (NuGet types are not
/// resolved), so code like "var user = await repo.GetAsync(id); user.EmailAddress" cannot be fixed by type. Here the
/// real compiler tells exactly which member of which type is wrong, at which position:
///   - IdentityUser/IdentityRole/Tenant members: Boilerplate names → ABP.io names, former custom properties → GetProperty/SetProperty
///   - removed/renamed repository methods: GetAll → GetQueryableAsync, InsertRangeAsync → InsertManyAsync, sync → async ...
///   - protected setters: entity.Id = x → EntityHelper.TrySetId, user.PhoneNumber = x → user.SetPhoneNumber(x, false) ...
///   - static Clock.Now outside services → DateTime.Now; LocalizedString used as string → .Value
/// The solution is built again until no more errors can be fixed (each build reaches further projects).
/// </summary>
public sealed partial class BuildFixStep : IMigrationStep
{
    public const int MaxIterations = 10;

    /// <summary>With --stub-unresolved every stubbing round lets the build reach further projects, so more rounds are needed.</summary>
    public const int MaxIterationsWithStubs = 40;

    private delegate SyntaxNode Fix(SyntaxNode node);

    /// <summary>A fix that returns its node with this annotation removes the node (e.g. a using directive).</summary>
    private static readonly SyntaxAnnotation Remove = new("AbpToVolo.Remove");

    private sealed record PlannedFix(SyntaxNode Target, Fix Apply, string Kind, string[] Usings);

    public string Name => "Compiler-guided fixes";

    public bool IsEnabled(MigrationOptions options) => options.CompilerFixes;

    public void Execute(MigrationContext context)
    {
        var target = ProjectWriter.FindSolution(context.OutputRoot);
        if (target is null)
        {
            context.Report.Warning(Name, "No solution file found; compiler-guided fixes skipped.");
            return;
        }

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastErrors = -1;
        var stubbed = new List<string>();
        var maxIterations = context.Options.StubUnresolved ? MaxIterationsWithStubs : MaxIterations;
        var retries = 0;
        var queriesFixed = false;
        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            context.Progress(iteration - 1, maxIterations, $"build {iteration}");
            var result = DotnetBuild.Run(target, context.OutputRoot, context.CancellationToken);
            lastErrors = result.Errors.Count + result.OtherErrors.Count;

            // Only non-compiler errors (a file locked by an IDE or a running process, MSB3021/MSB3027...): they say nothing
            // about the code. Log them and build again a few times before giving up.
            if (result.Errors.Count == 0 && result.OtherErrors.Count > 0)
            {
                foreach (var other in result.OtherErrors.Take(3)) context.Log.WriteLine($"build {iteration}: {other.Trim()}");
                if (FixPackageDowngrades(context, result.OtherErrors, totals) > 0) continue;
                if (++retries <= 3)
                {
                    context.Log.WriteLine($"build {iteration}: {result.OtherErrors.Count} build errors that are not compiler errors — retrying in 10 s (close the programs that use the output, e.g. Visual Studio)");
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    iteration--;
                    continue;
                }

                context.Report.Warning(Name, "The build stopped with errors that are not compiler errors: " + string.Join(" | ", result.OtherErrors.Take(3).Select(e => e.Trim())));
                break;
            }
            if (lastErrors == 0)
            {
                context.Log.WriteLine($"build {iteration}: no errors");
                // Once everything compiles: extension properties read inside EF Core queries (compile, but fail at run time).
                if (!queriesFixed)
                {
                    queriesFixed = true;
                    var rewritten = FixExtensionPropertiesInQueries(context, totals);
                    if (rewritten > 0)
                    {
                        context.Log.WriteLine($"build {iteration}: {rewritten} extension properties inside EF Core queries → EF.Property; building again");
                        continue;
                    }
                }

                break;
            }

            var applied = FixPackageDowngrades(context, result.OtherErrors, totals) + ApplyFixes(context, result.Errors, totals);
            if (applied == 0) applied = ApplySemanticFixes(context, result.Errors, totals);
            var projects = string.Join(", ", result.Errors.GroupBy(e => e.Project).Select(g => $"{g.Key}: {g.Count()}"));
            context.Log.WriteLine($"build {iteration}: {lastErrors} errors ({projects}) → {applied} fixed");
            if (applied == 0 && context.Options.StubUnresolved && result.Errors.Count > 0)
            {
                applied = StubUnresolved(context, result.Errors, stubbed);
                context.Log.WriteLine($"build {iteration}: {applied} members that still do not compile kept under #if ABPTOVOLO_ORIGINAL");
            }

            if (applied == 0) break;
        }

        if (stubbed.Count > 0)
        {
            var section = context.Report.Section("Members kept under #if ABPTOVOLO_ORIGINAL");
            section.Add($"{stubbed.Count} members did not compile after the automatic fixes. Their original (converted) body is kept under " +
                        "'#if ABPTOVOLO_ORIGINAL' and they throw NotImplementedException until they are fixed by hand:");
            section.AddRange(stubbed.Select(s => $"- {s}"));
        }

        var lines = context.Report.Section("Compiler-guided fixes");
        lines.Add($"The converted solution was built with the real compiler and {totals.Values.Sum()} errors were fixed automatically:");
        foreach (var (kind, count) in totals.OrderByDescending(t => t.Value))
        {
            lines.Add($"- {count} × {kind}");
        }

        lines.Add(lastErrors == 0 ? "The solution builds without errors." : $"{lastErrors} errors remain (see 'Build errors' / the log) and need manual work.");
    }

    public const string OriginalCodeSymbol = "ABPTOVOLO_ORIGINAL";

    /// <summary>
    /// --stub-unresolved: a method/constructor/accessor/local function that still has errors keeps its converted body
    /// under "#if ABPTOVOLO_ORIGINAL" (never compiled, nothing is lost) and throws NotImplementedException instead,
    /// so the build reaches the next projects. Errors outside member bodies are left as they are.
    /// </summary>
    private int StubUnresolved(MigrationContext context, List<BuildDiagnostic> errors, List<string> stubbed)
    {
        var count = 0;
        foreach (var group in errors.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key) || context.IsExcluded(group.Key)) continue;
            var (code, encoding) = FileSystem.ReadText(group.Key);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            var text = tree.GetText();

            // An override of a member ABP.io does not have (CS0115, e.g. a Module Zero manager kept as a subclass): nothing can
            // call it any more, so the whole member goes under #if (stubbing its body would not remove the error).
            var overrides = group.Where(e => e.Code == "CS0115" && e.Line >= 1 && e.Line <= text.Lines.Count)
                .Select(e => (Error: e, Member: root.FindToken(text.Lines[e.Line - 1].Start + Math.Max(0, e.Column - 1)).Parent?.AncestorsAndSelf()
                    .FirstOrDefault(n => n is MethodDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax)))
                .Where(x => x.Member is not null)
                .DistinctBy(x => x.Member!.SpanStart)
                .ToList();
            if (overrides.Count > 0)
            {
                var nl = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var excluded = code;
                foreach (var (error, member) in overrides.OrderByDescending(x => x.Member!.SpanStart))
                {
                    var start = text.Lines.GetLineFromPosition(member!.SpanStart).Start;
                    var end = member.FullSpan.End;
                    var message = $"{error.Code}: {error.Message}".Replace(nl, " ");
                    excluded = excluded[..start] +
                               $"#if {OriginalCodeSymbol} // TODO [AbpToVolo]: overrides a member the ABP.io base class does not have ({message}). Port it or remove it.{nl}" +
                               excluded[start..end].TrimEnd('\r', '\n') + nl + $"#endif{nl}" + excluded[end..];
                    var location = $"{context.Relative(group.Key)}:{text.Lines.GetLinePosition(member.SpanStart).Line + 1}";
                    stubbed.Add($"override excluded ({location}) — {message}");
                    context.Report.Todo(Name, $"Override excluded from the build: the ABP.io base class has no such member ({message}).", location);
                }

                FileSystem.WriteText(group.Key, excluded, encoding);
                count += overrides.Count;
                continue;
            }

            var bodies = new Dictionary<SyntaxNode, BuildDiagnostic>();
            BuildDiagnostic? declarationError = null;
            var declarationErrors = new List<BuildDiagnostic>();
            foreach (var error in group)
            {
                if (error.Line < 1 || error.Line > text.Lines.Count) continue;
                var token = root.FindToken(text.Lines[error.Line - 1].Start + Math.Max(0, error.Column - 1));
                // The outermost member body containing the error (a lambda's body is part of its member).
                var member = token.Parent?.AncestorsAndSelf().LastOrDefault(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or
                    PropertyDeclarationSyntax { ExpressionBody: not null });
                SyntaxNode? body = member switch
                {
                    BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                    PropertyDeclarationSyntax p => p.ExpressionBody,
                    _ => null
                };
                // Errors about the body reported on the member's name (CS0161 "not all code paths return a value",
                // CS4032/CS1983/CS0127...) belong to the body too.
                var onName = member is MethodDeclarationSyntax { Identifier: var identifier } && identifier == token ||
                             member is ConstructorDeclarationSyntax { Identifier: var ctorIdentifier } && ctorIdentifier == token ||
                             member is AccessorDeclarationSyntax { Keyword: var keyword } && keyword == token;
                if (body is not null && (body.Span.Contains(token.Span) || onName)) bodies.TryAdd(body, error);
                else
                {
                    declarationError ??= error;
                    declarationErrors.Add(error);
                }
            }

            // An error in a declaration (field type, base type, signature): the whole file is kept under #if, unless it is
            // a module or the DbContext (the solution would not start without them). Endpoints (application services,
            // controllers) are repaired declaration by declaration instead: their routes must stay.
            if (declarationError is not null)
            {
                if (ZeroMappings.HasEndpointType(root) && RepairEndpointDeclarations(context, group.Key, code, encoding, root, text, declarationErrors, stubbed) is var repaired and > 0)
                {
                    count += repaired;
                    continue;
                }

                var types = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToList();
                if (types.Count == 0 || types.Any(t => t.EndsWith("Module", StringComparison.Ordinal) || t.EndsWith("DbContext", StringComparison.Ordinal))) continue;

                var nl = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var message = $"{declarationError.Code}: {declarationError.Message}".Replace("\r", " ").Replace("\n", " ");
                FileSystem.WriteText(group.Key,
                    $"#if {OriginalCodeSymbol} // TODO [AbpToVolo]: this file did not compile after the conversion ({message}). Fix it and remove the #if.{nl}{code}{nl}#endif{nl}",
                    encoding);
                var file = context.Relative(group.Key);
                stubbed.Add($"whole file `{file}` ({string.Join(", ", types)}) — {message}");
                context.Report.Todo(Name, $"{string.Join(", ", types)} excluded from the build (#if {OriginalCodeSymbol}): {message}", file);
                count++;
                continue;
            }

            if (bodies.Count == 0) continue;

            var newline = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var positions = group.Where(e => e.Line >= 1 && e.Line <= text.Lines.Count)
                .Select(e => text.Lines[e.Line - 1].Start + Math.Max(0, e.Column - 1)).ToList();
            var result = code;
            foreach (var (body, error) in bodies.OrderByDescending(b => b.Key.SpanStart))
            {
                var member = body.Parent!;
                var name = member switch
                {
                    MethodDeclarationSyntax m => m.Identifier.Text,
                    ConstructorDeclarationSyntax c => c.Identifier.Text + " (constructor)",
                    AccessorDeclarationSyntax a => $"{(a.Parent?.Parent as BasePropertyDeclarationSyntax) switch { PropertyDeclarationSyntax p => p.Identifier.Text, _ => "indexer" }}.{a.Keyword.Text}",
                    PropertyDeclarationSyntax p => p.Identifier.Text,
                    _ => member.Kind().ToString()
                };
                var type = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                var message = $"{error.Code}: {error.Message}".Replace("\"", "'").Replace("*/", "* /");
                var indent = new string(' ', Math.Max(0, text.Lines.GetLinePosition(member.SpanStart).Character));

                // Expression bodies: "=> expr;" (the semicolon belongs to the member)
                var span = body.Span;
                string original;
                if (body is ArrowExpressionClauseSyntax arrow)
                {
                    var semicolon = member switch
                    {
                        BaseMethodDeclarationSyntax m => m.SemicolonToken,
                        AccessorDeclarationSyntax a => a.SemicolonToken,
                        PropertyDeclarationSyntax p => p.SemicolonToken,
                        _ => default
                    };
                    if (!semicolon.IsKind(SyntaxKind.None)) span = TextSpan.FromBounds(span.Start, semicolon.Span.End);
                    var returns = member is not (ConstructorDeclarationSyntax or AccessorDeclarationSyntax { Keyword.Text: "set" or "init" }) &&
                                  !(member is MethodDeclarationSyntax { ReturnType: PredefinedTypeSyntax { Keyword.Text: "void" } });
                    original = $"{indent}    {(returns ? "return " : "")}{arrow.Expression};";
                }
                else
                {
                    var block = (BlockSyntax)body;
                    original = code[block.OpenBraceToken.Span.End..block.CloseBraceToken.SpanStart].Trim('\r', '\n');
                }

                // A constructor that throws would make the whole class unusable (a controller or application service loses
                // every endpoint): only its statements that do not compile go under #if, the others stay.
                if (member is ConstructorDeclarationSyntax && body is BlockSyntax ctorBody &&
                    ctorBody.Statements.Where(s => positions.Any(p => s.Span.Contains(p))).ToList() is { Count: > 0 } failing &&
                    failing.All(s => string.IsNullOrWhiteSpace(code[text.Lines.GetLineFromPosition(s.SpanStart).Start..s.SpanStart])))
                {
                    foreach (var statement in failing.OrderByDescending(s => s.SpanStart))
                    {
                        var lineStart = text.Lines.GetLineFromPosition(statement.SpanStart).Start;
                        var end = statement.FullSpan.End;
                        result = result[..lineStart] +
                                 $"#if {OriginalCodeSymbol} // TODO [AbpToVolo]: did not compile after the conversion ({message.Replace(newline, " ")}); skipped until fixed.{newline}" +
                                 result[lineStart..end].TrimEnd('\r', '\n') + newline + $"#endif{newline}" +
                                 result[end..];
                    }

                    var ctorLocation = $"{context.Relative(group.Key)}:{text.Lines.GetLinePosition(member.SpanStart).Line + 1}";
                    stubbed.Add($"`{type}.{name}` ({ctorLocation}) [{failing.Count} statements skipped] — {message}");
                    context.Report.Todo(Name, $"{type}.{name}: {failing.Count} statements did not compile ({message}) and are skipped (#if {OriginalCodeSymbol}); " +
                                              "the members that use what they set throw or fail until they are fixed.", ctorLocation);
                    count++;
                    continue;
                }

                // Modules and DbContexts run at startup: a throwing stub would stop the whole application, so their
                // members that return nothing become no-ops (reported as warnings) instead.
                var startup = IsStartupCode(member);
                var stubStatement = startup
                    ? $"{indent}    // AbpToVolo: skipped until fixed — the application starts without this code (see the report)."
                    : $"{indent}    throw new System.NotImplementedException(\"AbpToVolo: {type}.{name} did not compile after the conversion; see #if {OriginalCodeSymbol}.\");";
                var replacement =
                    $"{{{newline}" +
                    $"#if {OriginalCodeSymbol} // TODO [AbpToVolo]: did not compile after the conversion ({message.Replace(newline, " ")}). Fix the code and remove the #if.{newline}" +
                    $"{original}{newline}" +
                    $"#endif{newline}" +
                    $"{stubStatement}{newline}" +
                    $"{indent}}}";
                result = result[..span.Start] + replacement + result[span.End..];

                var location = $"{context.Relative(group.Key)}:{text.Lines.GetLinePosition(member.SpanStart).Line + 1}";
                if (group.Key.Contains(Path.DirectorySeparatorChar + CompatShimStep.ShimFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(group.Key) is "LegacyIdsModelBuilderExtensions.cs")
                {
                    // Code generated by AbpToVolo must compile: this is a converter bug, worth reporting.
                    context.Report.Warning(Name, $"Generated compatibility code did not compile ({message}); please report it to AbpToVolo.", location);
                }
                stubbed.Add($"`{type}.{name}` ({location}){(startup ? " [skipped at startup]" : "")} — {message}");
                if (startup)
                {
                    context.Report.Warning(Name, $"{type}.{name} is SKIPPED at startup (no-op): its converted body did not compile ({message}); " +
                                                 $"the application runs without it until it is fixed — see #if {OriginalCodeSymbol}.", location);
                }
                else
                {
                    context.Report.Todo(Name, $"{type}.{name} throws NotImplementedException: its converted body did not compile ({message}); see #if {OriginalCodeSymbol}.", location);
                }
                count++;
            }

            FileSystem.WriteText(group.Key, result, encoding);
        }

        return count;
    }

    /// <summary>
    /// Declaration errors of an endpoint file (application service, controller, their interfaces) without losing the
    /// endpoints: a constructor parameter / field / property / attribute / base interface that does not compile is
    /// removed (its uses then fail in member bodies, which are stubbed), and an unknown type in a method signature
    /// becomes object, so the method — and its route — stays. Each removal is a comment in the code and a report item.
    /// Returns the number of repaired declarations (0: nothing could be repaired, the whole file is excluded).
    /// </summary>
    private int RepairEndpointDeclarations(MigrationContext context, string file, string code, System.Text.Encoding encoding, SyntaxNode root,
        SourceText text, List<BuildDiagnostic> errors, List<string> stubbed)
    {
        var removals = new Dictionary<SyntaxNode, BuildDiagnostic>();
        var toObject = new Dictionary<SyntaxNode, BuildDiagnostic>();
        foreach (var error in errors)
        {
            if (error.Line < 1 || error.Line > text.Lines.Count) continue;
            var node = root.FindToken(text.Lines[error.Line - 1].Start + Math.Max(0, error.Column - 1)).Parent;
            if (node is null) continue;
            var path = node.AncestorsAndSelf().ToList();

            if (path.OfType<UsingDirectiveSyntax>().FirstOrDefault() is { } usingDirective)
            {
                removals.TryAdd(usingDirective, error);
            }
            else if (path.OfType<AttributeSyntax>().FirstOrDefault() is { } attribute)
            {
                removals.TryAdd(attribute.Parent is AttributeListSyntax { Attributes.Count: 1 } list ? list : attribute, error);
            }
            else if (path.OfType<ParameterSyntax>().FirstOrDefault() is { Parent: ParameterListSyntax { Parent: TypeDeclarationSyntax or ConstructorDeclarationSyntax } } parameter)
            {
                removals.TryAdd(parameter, error);
            }
            else if (path.OfType<BaseTypeSyntax>().FirstOrDefault() is { Parent: BaseListSyntax } baseType)
            {
                removals.TryAdd(baseType, error);
            }
            else if (path.FirstOrDefault(n => n is FieldDeclarationSyntax or EventFieldDeclarationSyntax or PropertyDeclarationSyntax or EventDeclarationSyntax) is { } member &&
                     path.OfType<AccessorListSyntax>().FirstOrDefault() is null && path.OfType<ArrowExpressionClauseSyntax>().FirstOrDefault() is null)
            {
                removals.TryAdd(member, error);
            }
            else if (path.FirstOrDefault(n => n is TypeSyntax && n.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax)) is TypeSyntax type &&
                     path.Any(n => n is MethodDeclarationSyntax or DelegateDeclarationSyntax or ParameterListSyntax) &&
                     !path.Any(n => n is BlockSyntax or ArrowExpressionClauseSyntax))
            {
                toObject.TryAdd(type, error);
            }
        }

        // A removal inside another removed node is part of it.
        foreach (var inner in removals.Keys.Where(n => removals.Keys.Any(o => o != n && o.Span.Contains(n.Span))).ToList()) removals.Remove(inner);
        foreach (var inner in toObject.Keys.Where(n => removals.Keys.Any(o => o.Span.Contains(n.Span)) || toObject.Keys.Any(o => o != n && o.Span.Contains(n.Span))).ToList())
        {
            toObject.Remove(inner);
        }

        if (removals.Count + toObject.Count == 0) return 0;

        var relative = context.Relative(file);
        string Message(BuildDiagnostic e) => $"{e.Code}: {e.Message}".Replace("\r", " ").Replace("\n", " ").Replace("*/", "* /");
        string Location(SyntaxNode n) => $"{relative}:{text.Lines.GetLinePosition(n.SpanStart).Line + 1}";

        // Each removal leaves a comment on the declaration that contains it.
        var notes = new Dictionary<SyntaxNode, List<string>>();
        foreach (var (node, error) in removals)
        {
            var owner = node is UsingDirectiveSyntax ? node.Parent! :
                node.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax and not (FieldDeclarationSyntax or PropertyDeclarationSyntax or EventFieldDeclarationSyntax)) ?? node.Parent!;
            if (node is MemberDeclarationSyntax && node.Parent is not null) owner = node.Parent;
            if (!notes.TryGetValue(owner, out var list)) notes[owner] = list = [];
            var removed = Regex.Replace(node.ToString(), @"\s+", " ");
            list.Add($"removed `{removed}` ({Message(error)})");
            stubbed.Add($"`{removed}` removed from an endpoint ({Location(node)}) — {Message(error)}");
            context.Report.Todo(Name, $"Endpoint kept: `{removed}` was removed because it did not compile ({Message(error)}); the members that used it throw NotImplementedException.", Location(node));
        }

        foreach (var (node, error) in toObject)
        {
            var owner = node.Ancestors().First(a => a is MemberDeclarationSyntax or DelegateDeclarationSyntax);
            if (!notes.TryGetValue(owner, out var list)) notes[owner] = list = [];
            list.Add($"type `{node}` → object ({Message(error)})");
            stubbed.Add($"`{node}` → object in a signature ({Location(node)}) — {Message(error)}");
            context.Report.Todo(Name, $"Endpoint kept: the type `{node}` of a signature did not compile ({Message(error)}) and became object; restore the type.", Location(node));
        }

        var tracked = root.TrackNodes(removals.Keys.Concat(toObject.Keys).Concat(notes.Keys));
        foreach (var node in toObject.Keys)
        {
            var current = tracked.GetCurrentNode(node)!;
            tracked = tracked.ReplaceNode(current, PredefinedType(Token(SyntaxKind.ObjectKeyword)).WithTriviaFrom(current));
        }

        var newline = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var (owner, list) in notes)
        {
            var current = tracked.GetCurrentNode(owner)!;
            if (current is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax) continue; // usings: the report is enough
            var indent = current.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToString();
            var comments = list.Select(l => $"// TODO [AbpToVolo]: {l}{newline}{indent}");
            tracked = tracked.ReplaceNode(current, current.WithLeadingTrivia(current.GetLeadingTrivia().AddRange(ParseLeadingTrivia(string.Concat(comments)))));
        }

        tracked = tracked.RemoveNodes(removals.Keys.Select(n => tracked.GetCurrentNode(n)!), SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)!;
        FileSystem.WriteText(file, tracked.ToFullString(), encoding);
        return removals.Count + toObject.Count;
    }

    /// <summary>
    /// A member of a module or DbContext that returns nothing (void method, constructor, setter, async method without
    /// result): code that runs while the application starts.
    /// </summary>
    private static bool IsStartupCode(SyntaxNode member)
    {
        var owner = member.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (owner is null || !(SyntaxHelpers.HasBaseType(owner, "AbpModule") || owner.Identifier.Text.EndsWith("DbContext", StringComparison.Ordinal)))
        {
            return false;
        }

        return member switch
        {
            ConstructorDeclarationSyntax => true,
            AccessorDeclarationSyntax a => a.Keyword.Text is "set" or "init",
            MethodDeclarationSyntax { ReturnType: PredefinedTypeSyntax { Keyword.Text: "void" } } => true,
            MethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword) && m.ReturnType is IdentifierNameSyntax { Identifier.Text: "Task" },
            _ => false
        };
    }

    /// <summary>NU1605 "Detected package downgrade: X from 13.0.4 to 13.0.3" → reference X 13.0.4 in that project.</summary>
    private int FixPackageDowngrades(MigrationContext context, List<string> errors, Dictionary<string, int> totals)
    {
        var fixes = 0;
        foreach (var match in errors.Select(e => PackageDowngrade().Match(e)).Where(m => m.Success).DistinctBy(m => (m.Groups["project"].Value, m.Groups["package"].Value)))
        {
            var csproj = match.Groups["project"].Value.Trim();
            if (!File.Exists(csproj)) continue;
            var doc = System.Xml.Linq.XDocument.Load(csproj);
            var reference = doc.Descendants("PackageReference")
                .FirstOrDefault(r => string.Equals(r.Attribute("Include")?.Value, match.Groups["package"].Value, StringComparison.OrdinalIgnoreCase));
            if (reference?.Attribute("Version") is not { } version) continue;

            version.Value = match.Groups["required"].Value;
            ProjectWriter.Save(doc, csproj);
            context.Report.Change(Name, $"{match.Groups["package"].Value} → {version.Value} (package downgrade NU1605)", context.Relative(csproj));
            totals["package downgrade (NU1605) → required version"] = totals.GetValueOrDefault("package downgrade (NU1605) → required version") + 1;
            fixes++;
        }

        return fixes;
    }

    [GeneratedRegex(@"^(?<project>[^\s].*?\.csproj) : error NU1605: .*?Detected package downgrade: (?<package>\S+) from (?<required>\S+) to (?<current>\S+)")]
    private static partial Regex PackageDowngrade();

    private int ApplyFixes(MigrationContext context, List<BuildDiagnostic> errors, Dictionary<string, int> totals)
    {
        var applied = 0;
        foreach (var group in errors.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key) || context.IsExcluded(group.Key)) continue;
            var (code, encoding) = FileSystem.ReadText(group.Key);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            var text = tree.GetText();

            var fixes = new Dictionary<SyntaxNode, PlannedFix>();
            foreach (var error in group)
            {
                if (error.Line < 1 || error.Line > text.Lines.Count) continue;
                var position = text.Lines[error.Line - 1].Start + Math.Max(0, error.Column - 1);
                if (Plan(context, root, position, error) is { } fix && !fixes.ContainsKey(fix.Target) &&
                    !fixes.Keys.Any(k => k.Span.Contains(fix.Target.Span) && k is StatementSyntax && fix.Target is StatementSyntax))
                {
                    fixes[fix.Target] = fix;
                }
            }

            if (fixes.Count == 0) continue;
            var updated = root.ReplaceNodes(fixes.Keys, (original, rewritten) => fixes[original].Apply(rewritten));
            var removed = updated.GetAnnotatedNodes(Remove).ToList();
            if (removed.Count > 0) updated = updated.RemoveNodes(removed, SyntaxRemoveOptions.KeepExteriorTrivia)!;
            var result = ModuleMethodEditor.AddUsings(updated.ToFullString(), fixes.Values.SelectMany(f => f.Usings).Distinct());
            FileSystem.WriteText(group.Key, result, encoding);

            foreach (var fix in fixes.Values)
            {
                totals[fix.Kind] = totals.GetValueOrDefault(fix.Kind) + 1;
            }

            context.Report.Change(Name, $"{fixes.Count} compiler-guided fixes ({string.Join(", ", fixes.Values.Select(f => f.Kind).Distinct())})", context.Relative(group.Key));
            applied += fixes.Count;
        }

        return applied;
    }

    private static PlannedFix? Plan(MigrationContext context, SyntaxNode root, int position, BuildDiagnostic error)
    {
        var token = root.FindToken(position);
        return error.Code switch
        {
            "CS1061" or "CS0117" when MissingMember().Match(error.Message) is { Success: true } m =>
                PlanMissingMember(context, token, Simple(m.Groups["type"].Value), m.Groups["member"].Value),
            "CS1929" when LocalizedStringMember().Match(error.Message) is { Success: true } m => PlanLocalizedString(token, m.Groups["member"].Value),
            "CS0120" when error.Message.Contains("'Clock.Now'", StringComparison.Ordinal) || error.Message.Contains(".Clock'", StringComparison.Ordinal) => PlanClockNow(token),
            "CS0120" when StaticReference().Match(error.Message) is { Success: true } m && AmbientMembers.ContainsKey(m.Groups["name"].Value) =>
                PlanAmbientMembers(token),
            "CS0103" when UnknownName().Match(error.Message) is { Success: true } m && AmbientMembers.ContainsKey(m.Groups["name"].Value) =>
                PlanAmbientMembers(token),
            "CS0103" when error.Message.Contains("'SimpleStringCipher'", StringComparison.Ordinal) => PlanStringCipher(token),
            "CS0201" => PlanUnusedId(token),
            "CS0234" or "CS0246" when token.Parent?.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault() is { Alias: null, StaticKeyword.RawKind: 0 } directive =>
                new PlannedFix(directive, n => n.WithAdditionalAnnotations(Remove), "using of a namespace that does not exist in ABP.io → removed", []),
            "CS1729" when error.Message.Contains("'UnitOfWorkAttribute'", StringComparison.Ordinal) => PlanUnitOfWorkAttribute(token),
            "CS1750" when error.Message.Contains("to type 'Guid'", StringComparison.Ordinal) => PlanGuidDefault(context, token),
            "CS0311" when error.Message.Contains("BackgroundJobInfo", StringComparison.Ordinal) => PlanBackgroundJobRecord(token),
            "CS0115" when CrudOverride().Match(error.Message) is { Success: true } m => PlanCrudOverride(token, m.Groups["method"].Value),
            "CS0029" or "CS1503" when GuidFromNumber().IsMatch(error.Message) => PlanGuidParse(token),
            "CS0019" when GuidComparedToNumber().IsMatch(error.Message) => PlanGuidParseInComparison(token),
            "CS1503" when ExceptionAsString().Match(error.Message) is { Success: true } m => PlanInnerException(token, int.Parse(m.Groups["index"].Value)),
            "CS0272" when InaccessibleSetter().Match(error.Message) is { Success: true } m =>
                PlanSetter(context, token, Simple(m.Groups["type"].Value), m.Groups["member"].Value),
            "CS0104" when AmbiguousReference().Match(error.Message) is { Success: true } m => PlanAmbiguousReference(token, m.Groups["a"].Value, m.Groups["b"].Value),
            _ => null
        };
    }

    /// <summary>
    /// CS0104 "'ITenantAppService' is an ambiguous reference between 'MyApp.Tenants.ITenantAppService' and
    /// 'Volo.Abp.TenantManagement.ITenantAppService'": the solution's own type was meant (the ABP.io one came with a
    /// using added for the module entities) → global::MyApp.Tenants.ITenantAppService.
    /// </summary>
    private static PlannedFix? PlanAmbiguousReference(SyntaxToken token, string a, string b)
    {
        static bool IsFramework(string name) => name.StartsWith("Volo.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                                                name.StartsWith("System.", StringComparison.Ordinal);
        var own = (IsFramework(a), IsFramework(b)) switch { (false, true) => a, (true, false) => b, _ => null };
        if (own is null || token.Parent is not SimpleNameSyntax name || name.Parent is QualifiedNameSyntax { Right: var right } && right == name ||
            name.Parent is MemberAccessExpressionSyntax { Name: var member } && member == name)
        {
            return null;
        }

        var generic = own.IndexOf('<');
        var ns = (generic < 0 ? own : own[..generic]);
        ns = ns[..ns.LastIndexOf('.')];
        var qualified = ParseTypeName($"global::{ns}.{name.WithoutTrivia()}").WithTriviaFrom(name);
        return new PlannedFix(name, _ => qualified, "ambiguous reference between the solution's type and an ABP.io type → the solution's type", []);
    }

    [GeneratedRegex(@"is an ambiguous reference between '(?<a>[^']+)' and '(?<b>[^']+)'")]
    private static partial Regex AmbiguousReference();

    // ---------- members Boilerplate base classes / property injection provided ----------

    /// <summary>
    /// Name → (type, namespace, declaration). Boilerplate classes used AbpSession, SettingManager, CurrentUnitOfWork...
    /// from base classes or property injection; in classes without an ABP.io base class the converted names
    /// (CurrentTenant, CurrentUser, DataFilter...) do not exist, so they are declared as property-injected members.
    /// </summary>
    private static readonly Dictionary<string, (string Namespace, string Declaration)> AmbientMembers = new(StringComparer.Ordinal)
    {
        ["CurrentTenant"] = ("Volo.Abp.MultiTenancy", "public ICurrentTenant CurrentTenant { get; set; }"),
        ["CurrentUser"] = ("Volo.Abp.Users", "public ICurrentUser CurrentUser { get; set; }"),
        ["DataFilter"] = ("Volo.Abp.Data", "public IDataFilter DataFilter { get; set; }"),
        ["AuthorizationService"] = ("Microsoft.AspNetCore.Authorization", "public IAuthorizationService AuthorizationService { get; set; }"),
        ["PermissionChecker"] = ("Volo.Abp.Authorization.Permissions", "public IPermissionChecker PermissionChecker { get; set; }"),
        ["SettingManager"] = ("Volo.Abp.SettingManagement", "public ISettingManager SettingManager { get; set; }"),
        ["SettingProvider"] = ("Volo.Abp.Settings", "public ISettingProvider SettingProvider { get; set; }"),
        ["UnitOfWorkManager"] = ("Volo.Abp.Uow", "public IUnitOfWorkManager UnitOfWorkManager { get; set; }"),
        ["CurrentUnitOfWork"] = ("Volo.Abp.Uow", "protected IUnitOfWork CurrentUnitOfWork => UnitOfWorkManager.Current;"),
        ["ObjectMapper"] = ("Volo.Abp.ObjectMapping", "public IObjectMapper ObjectMapper { get; set; }"),
        ["GuidGenerator"] = ("Volo.Abp.Guids", "public IGuidGenerator GuidGenerator { get; set; }"),
        ["LocalizationManager"] = ("Microsoft.Extensions.Localization", "public IStringLocalizerFactory LocalizationManager { get; set; }"),
        // Boilerplate repositories had MultiTenancySide; ABP.io repositories have CurrentTenant
        ["MultiTenancySide"] = ("Volo.Abp.MultiTenancy", "protected MultiTenancySides MultiTenancySide => CurrentTenant.IsAvailable ? MultiTenancySides.Tenant : MultiTenancySides.Host;"),
    };

    private static PlannedFix? PlanAmbientMembers(SyntaxToken token)
    {
        var cls = token.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (cls is null || cls.Modifiers.Any(SyntaxKind.StaticKeyword)) return null;

        var declared = cls.Members.SelectMany(m => m switch
        {
            PropertyDeclarationSyntax p => [p.Identifier.Text],
            FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.Text),
            MethodDeclarationSyntax me => [me.Identifier.Text],
            _ => []
        }).ToHashSet(StringComparer.Ordinal);

        // Every ambient name used as a simple identifier (not as a member name x.CurrentUser) and not declared in the class.
        var needed = cls.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(n => AmbientMembers.ContainsKey(n.Identifier.Text) &&
                        !(n.Parent is MemberAccessExpressionSyntax ma && ma.Name == n) && n.Parent is not QualifiedNameSyntax)
            .Select(n => n.Identifier.Text)
            .Where(n => !declared.Contains(n))
            .ToHashSet(StringComparer.Ordinal);
        if (needed.Contains("CurrentUnitOfWork") && !declared.Contains("UnitOfWorkManager")) needed.Add("UnitOfWorkManager");
        if (needed.Count == 0) return null;

        var names = needed.OrderBy(n => n == "CurrentUnitOfWork" ? 1 : 0).ThenBy(n => n, StringComparer.Ordinal).ToList();
        return new PlannedFix(cls, n =>
        {
            var c = (ClassDeclarationSyntax)n;
            var members = names.Select((name, i) =>
            {
                var member = ParseMemberDeclaration(AmbientMembers[name].Declaration + Environment.NewLine)!;
                return i == 0
                    ? member.WithLeadingTrivia(ParseLeadingTrivia("/* Property-injected by ABP.io (added by AbpToVolo: Boilerplate provided these members). */" + Environment.NewLine))
                    : member;
            });
            return c.WithMembers(List(members.Concat(c.Members)));
        }, $"declared property-injected {string.Join("/", names)} (former Boilerplate base-class members)",
            names.Select(name => AmbientMembers[name].Namespace).Distinct().ToArray());
    }

    /// <summary>
    /// User/tenant ids became Guid: long.Parse(userId) / Convert.ToInt64(userId) where a Guid is expected → Guid.Parse(userId).
    /// (Hard-coded numeric ids cannot be converted automatically and stay as errors.)
    /// </summary>
    private static PlannedFix? PlanGuidParse(SyntaxToken token)
    {
        var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(i =>
            i.SpanStart == token.SpanStart && i.ArgumentList.Arguments.Count == 1 &&
            SyntaxHelpers.Text(i.Expression) is "long.Parse" or "int.Parse" or "Convert.ToInt64" or "Convert.ToInt32" or "Int64.Parse" or "Int32.Parse");
        return invocation is null ? null : new PlannedFix(invocation, n =>
        {
            var i = (InvocationExpressionSyntax)n;
            return ParseExpression($"Guid.Parse({i.ArgumentList.Arguments[0].WithoutTrivia()}.ToString())").WithTriviaFrom(i);
        }, "numeric user/tenant id parsing → Guid.Parse (ids are Guid in ABP.io)", ["System"]);
    }

    /// <summary>
    /// [UnitOfWork(isolationLevel: System.Transactions.IsolationLevel.X)] → [UnitOfWork(true, System.Data.IsolationLevel.X)];
    /// [UnitOfWork(TransactionScopeOption.Suppress)] → [UnitOfWork(IsDisabled = true)].
    /// </summary>
    private static PlannedFix? PlanUnitOfWorkAttribute(SyntaxToken token)
    {
        var attribute = token.Parent?.AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute?.ArgumentList is not { Arguments.Count: 1 } list) return null;
        var value = list.Arguments[0].Expression.ToString();
        var member = value[(value.LastIndexOf('.') + 1)..];

        string? replacement = value switch
        {
            _ when value.Contains("IsolationLevel.", StringComparison.Ordinal) => $"UnitOfWork(true, System.Data.IsolationLevel.{member})",
            _ when value.EndsWith("TransactionScopeOption.Suppress", StringComparison.Ordinal) => "UnitOfWork(IsDisabled = true)",
            _ when value.Contains("TransactionScopeOption.", StringComparison.Ordinal) => "UnitOfWork(true)",
            _ => null
        };
        return replacement is null ? null : new PlannedFix(attribute, n => ParseAttribute(replacement).WithTriviaFrom(n),
            "[UnitOfWork(isolation/scope)] → ABP.io UnitOfWorkAttribute(isTransactional, System.Data.IsolationLevel)", []);
    }

    private static AttributeSyntax ParseAttribute(string text) =>
        ((AttributeListSyntax)ParseCompilationUnit($"[{text}] class X {{ }}").Members[0].AttributeLists[0]).Attributes[0];

    /// <summary>"Guid tenantId = 1" (a numeric tenant/user id default became Guid) → "Guid? tenantId = null"; the caller must pass the real id.</summary>
    private static PlannedFix? PlanGuidDefault(MigrationContext context, SyntaxToken token)
    {
        var parameter = token.Parent?.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
        if (parameter?.Type is null || parameter.Default is null) return null;
        context.Report.Todo("Compiler-guided fixes", $"Parameter '{parameter.Identifier.Text}' had the numeric default {parameter.Default.Value} (a Boilerplate tenant/user id); " +
                                                     "ids are Guid in ABP.io: pass the real id (see Zero_IdMap).", null);
        return new PlannedFix(parameter, n =>
        {
            var p = (ParameterSyntax)n;
            TypeSyntax type = p.Type is NullableTypeSyntax ? p.Type : NullableType(p.Type!.WithoutTrailingTrivia()).WithTrailingTrivia(Space);
            return p.WithType(type).WithDefault(p.Default!.WithValue(LiteralExpression(SyntaxKind.NullLiteralExpression).WithTriviaFrom(p.Default.Value)));
        }, "numeric default of a Guid id parameter → Guid? = null", []);
    }

    /// <summary>IRepository&lt;BackgroundJobInfo, long&gt; → IRepository&lt;BackgroundJobRecord, Guid&gt; (Volo.Abp.BackgroundJobs).</summary>
    private static PlannedFix? PlanBackgroundJobRecord(SyntaxToken token)
    {
        var generic = token.Parent?.AncestorsAndSelf().OfType<GenericNameSyntax>()
            .FirstOrDefault(g => g.TypeArgumentList.Arguments.Count == 2 && SyntaxHelpers.SimpleName(g.TypeArgumentList.Arguments[0]) == "BackgroundJobInfo");
        return generic is null ? null : new PlannedFix(generic, n =>
        {
            var g = (GenericNameSyntax)n;
            return g.WithTypeArgumentList(TypeArgumentList(SeparatedList<TypeSyntax>([IdentifierName("BackgroundJobRecord"), IdentifierName("Guid")])));
        }, "IRepository<BackgroundJobInfo, long> → IRepository<BackgroundJobRecord, Guid>", ["System", "Volo.Abp.BackgroundJobs"]);
    }

    /// <summary>
    /// Boilerplate AsyncCrudAppService overrides → ABP.io CrudAppService signatures:
    ///   UpdateAsync(TUpdateDto input)       → UpdateAsync(TKey id, TUpdateDto input)
    ///   DeleteAsync(EntityDto&lt;TKey&gt; input) → DeleteAsync(TKey id) (with "var input = new EntityDto&lt;TKey&gt;(id);")
    ///   GetAsync(EntityDto&lt;TKey&gt; input)    → GetAsync(TKey id)
    /// </summary>
    private static PlannedFix? PlanCrudOverride(SyntaxToken token, string method)
    {
        var declaration = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var cls = declaration?.Parent as ClassDeclarationSyntax;
        if (declaration?.Body is null || cls?.BaseList?.Types.FirstOrDefault()?.Type is not GenericNameSyntax { TypeArgumentList.Arguments.Count: >= 3 } crud ||
            !crud.Identifier.Text.EndsWith("CrudAppService", StringComparison.Ordinal) || declaration.ParameterList.Parameters.Count != 1)
        {
            return null;
        }

        var key = crud.TypeArgumentList.Arguments[2].ToString();
        var parameter = declaration.ParameterList.Parameters[0];
        var isEntityDto = parameter.Type is GenericNameSyntax { Identifier.Text: "EntityDto" };

        return method switch
        {
            "UpdateAsync" when !isEntityDto => new PlannedFix(declaration, n =>
            {
                var d = (MethodDeclarationSyntax)n;
                return d.WithParameterList(d.ParameterList.WithParameters(d.ParameterList.Parameters.Insert(0, Parameter(Identifier("id")).WithType(ParseTypeName(key + " ")))));
            }, "CRUD override UpdateAsync(input) → UpdateAsync(id, input)", []),
            "DeleteAsync" or "GetAsync" when isEntityDto => new PlannedFix(declaration, n =>
            {
                var d = (MethodDeclarationSyntax)n;
                var name = d.ParameterList.Parameters[0].Identifier.Text;
                var first = ParseStatement($"var {name} = new EntityDto<{key}>(id);{Environment.NewLine}");
                return d.WithParameterList(ParameterList(SingletonSeparatedList(Parameter(Identifier("id")).WithType(ParseTypeName(key + " ")))))
                    .WithBody(d.Body!.WithStatements(d.Body.Statements.Insert(0, first)));
            }, $"CRUD override {method}(EntityDto) → {method}(id)", ["Volo.Abp.Application.Dtos"]),
            _ => null
        };
    }

    /// <summary>SimpleStringCipher.DefaultPassPhrase = x (in a module) → Configure&lt;AbpStringEncryptionOptions&gt;(o => o.DefaultPassPhrase = x)</summary>
    private static PlannedFix? PlanStringCipher(SyntaxToken token)
    {
        var statement = token.Parent?.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault();
        if (statement?.Expression is not AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.Text: "DefaultPassPhrase" } } ||
            statement.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text.EndsWith("Module", StringComparison.Ordinal) != true)
        {
            return null;
        }

        return new PlannedFix(statement, n =>
        {
            var value = ((AssignmentExpressionSyntax)((ExpressionStatementSyntax)n).Expression).Right.WithoutTrivia();
            return ParseStatement($"Configure<AbpStringEncryptionOptions>(options => options.DefaultPassPhrase = {value});").WithTriviaFrom(n);
        }, "SimpleStringCipher.DefaultPassPhrase → AbpStringEncryptionOptions", ["Volo.Abp.Security.Encryption"]);
    }

    /// <summary>x.Id == long.Parse(userId) where Id is a Guid now → x.Id == Guid.Parse(userId)</summary>
    private static PlannedFix? PlanGuidParseInComparison(SyntaxToken token)
    {
        var binary = token.Parent?.AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault();
        var operand = new[] { binary?.Left, binary?.Right }.OfType<InvocationExpressionSyntax>().FirstOrDefault(i => IsNumberParse(i));
        return operand is null ? null : PlanGuidParse(operand.GetFirstToken());
    }

    private static bool IsNumberParse(InvocationExpressionSyntax i) =>
        i.ArgumentList.Arguments.Count == 1 &&
        SyntaxHelpers.Text(i.Expression) is "long.Parse" or "int.Parse" or "Convert.ToInt64" or "Convert.ToInt32" or "Int64.Parse" or "Int32.Parse";

    /// <summary>
    /// Boilerplate's UserFriendlyException(message, Exception inner) / (message, details, inner): ABP.io's constructors are
    /// (message, code, details, innerException, ...) → pass the exception as innerException: ex.
    /// </summary>
    private static PlannedFix? PlanInnerException(SyntaxToken token, int index)
    {
        var creation = token.Parent?.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>()
            .FirstOrDefault(c => SyntaxHelpers.SimpleName(c.Type) is "UserFriendlyException" or "BusinessException" or "AbpException" &&
                                 c.ArgumentList is { } list && list.Arguments.Count >= index);
        // (message, details: ex) written by ExceptionRewriter for an exception it could not recognize by name → innerException: ex
        if (creation?.ArgumentList is null || creation.ArgumentList.Arguments[index - 1].NameColon is { Name.Identifier.Text: not "details" }) return null;

        return new PlannedFix(creation, n =>
        {
            var c = (ObjectCreationExpressionSyntax)n;
            var args = c.ArgumentList!.Arguments;
            // Arguments after the exception (Boilerplate had none) keep their position before it only if named; name the exception.
            var updated = args.Replace(args[index - 1], args[index - 1].WithNameColon(SyntaxHelpers.NamedArgument("innerException")));
            if (index == 3 && args[1].NameColon is null)
            {
                updated = updated.Replace(updated[1], updated[1].WithNameColon(SyntaxHelpers.NamedArgument("details")));
            }

            return c.WithArgumentList(c.ArgumentList.WithArguments(updated));
        }, "UserFriendlyException(message, exception) → innerException: exception", []);
    }

    /// <summary>"(await repo.InsertAsync(x, autoSave: true)).Id;" is not a valid statement → "await repo.InsertAsync(x, autoSave: true);"</summary>
    private static PlannedFix? PlanUnusedId(SyntaxToken token)
    {
        var statement = token.Parent?.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault();
        if (statement?.Expression is not (MemberAccessExpressionSyntax { Name.Identifier.Text: "Id", Expression: ParenthesizedExpressionSyntax { Expression: AwaitExpressionSyntax } }
            or ParenthesizedExpressionSyntax { Expression: AwaitExpressionSyntax })) return null;

        return new PlannedFix(statement, n =>
        {
            var s = (ExpressionStatementSyntax)n;
            var parenthesized = s.Expression as ParenthesizedExpressionSyntax ?? (ParenthesizedExpressionSyntax)((MemberAccessExpressionSyntax)s.Expression).Expression;
            var awaited = parenthesized.Expression;
            return s.WithExpression(awaited.WithTriviaFrom(s.Expression));
        }, "unused (await …).Id statement → await …", []);
    }

    private static string Simple(string type)
    {
        var name = type.Contains('<') ? type[..type.IndexOf('<')] : type;
        name = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return type.Contains('<') ? name + type[type.IndexOf('<')..] : name;
    }

    // ---------- missing members ----------

    private static readonly Dictionary<string, ZeroKind> IdentityTypes = new(StringComparer.Ordinal)
    {
        ["IdentityUser"] = ZeroKind.User, ["IdentityRole"] = ZeroKind.Role, ["Tenant"] = ZeroKind.Tenant
    };

    private static readonly Dictionary<string, (string Name, bool AddAutoSave)> SyncRepositoryMethods = new(StringComparer.Ordinal)
    {
        ["Insert"] = ("InsertAsync", false), ["Update"] = ("UpdateAsync", false), ["Delete"] = ("DeleteAsync", false),
        ["Get"] = ("GetAsync", false), ["FirstOrDefault"] = ("FindAsync", false), ["Single"] = ("GetAsync", false),
        ["GetAllList"] = ("GetListAsync", false), ["Count"] = ("GetCountAsync", false), ["LongCount"] = ("GetCountAsync", false),
        ["InsertAndGetId"] = ("InsertAsync", true)
    };

    private static readonly Dictionary<string, string> RepositoryRenames = new(StringComparer.Ordinal)
    {
        ["InsertRangeAsync"] = "InsertManyAsync", ["UpdateRangeAsync"] = "UpdateManyAsync", ["DeleteRangeAsync"] = "DeleteManyAsync",
        ["GetAllListAsync"] = "GetListAsync", ["FirstOrDefaultAsync"] = "FindAsync", ["SingleAsync"] = "GetAsync",
        ["LongCountAsync"] = "GetCountAsync"
    };

    private static PlannedFix? PlanMissingMember(MigrationContext context, SyntaxToken token, string type, string member)
    {
        // x?.Member: the compiler points at the '.' of the member binding
        var name = token.Parent switch
        {
            SimpleNameSyntax simple => simple,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            _ => null
        };
        if (name is null) return null;

        // Object initializer: new IdentityUser { EmailAddress = ... }
        if (name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } && IdentityTypes.TryGetValue(type, out var initKind) &&
            ZeroMappings.MemberRenames.TryGetValue(initKind, out var initRenames) && initRenames.TryGetValue(member, out var initNew))
        {
            return new PlannedFix(name, n => IdentifierName(initNew).WithTriviaFrom(n), $"{type}.{member} → {initNew}", []);
        }

        // user?.FullName → (user?.Name + " " + user?.Surname)
        if (type == "IdentityUser" && member == "FullName" &&
            name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == binding)
        {
            return FullNameFix(conditional, n =>
            {
                var receiver = ((ConditionalAccessExpressionSyntax)n).Expression.WithoutTrivia();
                return $"({receiver}?.Name + \" \" + {receiver}?.Surname)";
            });
        }

        if (name.Parent is not MemberAccessExpressionSyntax access || access.Name != name) return null;

        if (IdentityTypes.TryGetValue(type, out var kind))
        {
            if (ZeroMappings.MemberRenames.TryGetValue(kind, out var renames) && renames.TryGetValue(member, out var newName))
            {
                // new { u.EmailAddress } → new { EmailAddress = u.Email }: the anonymous type keeps the property the code reads.
                if (access.Parent is AnonymousObjectMemberDeclaratorSyntax { NameEquals: null } renamedDeclarator)
                {
                    return new PlannedFix(renamedDeclarator, n =>
                    {
                        var d = (AnonymousObjectMemberDeclaratorSyntax)n;
                        var a = (MemberAccessExpressionSyntax)d.Expression;
                        return SyntaxHelpers.NamedMember(d, member, a.WithName(IdentifierName(newName).WithTriviaFrom(a.Name)));
                    }, $"{type}.{member} → {newName}", []);
                }

                return new PlannedFix(name, n => IdentifierName(newName).WithTriviaFrom(n), $"{type}.{member} → {newName}", []);
            }

            if (context.ExtensionProperties.TryGetValue(type, out var properties) && properties.TryGetValue(member, out var propertyType))
            {
                if (access.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Parent: ExpressionStatementSyntax statement } assignment &&
                    assignment.Left == access)
                {
                    return new PlannedFix(statement, n =>
                    {
                        var s = (ExpressionStatementSyntax)n;
                        var a = (AssignmentExpressionSyntax)s.Expression;
                        var receiver = ((MemberAccessExpressionSyntax)a.Left).Expression.WithoutTrivia();
                        return ParseStatement($"{receiver}.SetProperty(\"{member}\", {a.Right.WithoutTrivia()});").WithTriviaFrom(s);
                    }, $"{type}.{member} = … → SetProperty (extra property)", ["Volo.Abp.Data"]);
                }

                if (access.Parent is AssignmentExpressionSyntax other && other.Left == access) return null;

                if (access.Parent is AnonymousObjectMemberDeclaratorSyntax { NameEquals: null } declarator)
                {
                    return new PlannedFix(declarator, n =>
                    {
                        var d = (AnonymousObjectMemberDeclaratorSyntax)n;
                        var a = (MemberAccessExpressionSyntax)d.Expression;
                        return SyntaxHelpers.NamedMember(d, member,
                            ParseExpression($"{a.Expression.WithoutTrivia()}.GetProperty<{propertyType}>(\"{member}\")"));
                    }, $"{type}.{member} → GetProperty (extra property)", ["Volo.Abp.Data"]);
                }

                return new PlannedFix(access, n =>
                {
                    var a = (MemberAccessExpressionSyntax)n;
                    return ParseExpression($"{a.Expression.WithoutTrivia()}.GetProperty<{propertyType}>(\"{member}\")").WithTriviaFrom(a);
                }, $"{type}.{member} → GetProperty (extra property)", ["Volo.Abp.Data"]);
            }

            if (kind == ZeroKind.User && member == "FullName")
            {
                return FullNameFix(access, n =>
                {
                    var receiver = ((MemberAccessExpressionSyntax)n).Expression.WithoutTrivia();
                    return $"({receiver}.Name + \" \" + {receiver}.Surname)";
                });
            }

            return null;
        }

        // uow.SetTenantId(id) → CurrentTenant.Change(id) (declared by PlanAmbientMembers if the class has no CurrentTenant)
        if (type == "IUnitOfWork" && member == "SetTenantId" && access.Parent is InvocationExpressionSyntax setTenant)
        {
            return new PlannedFix(setTenant, n => ((InvocationExpressionSyntax)n).WithExpression(ParseExpression("CurrentTenant.Change")),
                "unitOfWork.SetTenantId(id) → CurrentTenant.Change(id)", []);
        }

        // uow.SaveChanges() → await uow.SaveChangesAsync()
        if (type == "IUnitOfWork" && member == "SaveChanges" && access.Parent is InvocationExpressionSyntax save)
        {
            var isAsync = SyntaxHelpers.IsInsideAsyncFunction(save);
            return new PlannedFix(save, n =>
            {
                var receiver = ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)n).Expression).Expression.WithoutTrivia();
                return ParseExpression(isAsync ? $"await {receiver}.SaveChangesAsync()" : $"{receiver}.SaveChangesAsync().GetAwaiter().GetResult()").WithTriviaFrom(n);
            }, "unitOfWork.SaveChanges() → SaveChangesAsync()", []);
        }

        if (type.Contains("Repository", StringComparison.Ordinal) && access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access)
        {
            return PlanRepositoryCall(invocation, access, member);
        }

        // Members of LocalizedString used as string: L["x"].Replace(...) → L["x"].Value.Replace(...)
        if (type == "LocalizedString" && member != "Value")
        {
            return PlanLocalizedString(access.Name.Identifier);
        }

        return null;
    }

    /// <summary>IdentityUser.FullName → Name + Surname; in an anonymous type "new { u.FullName }" the member keeps its name.</summary>
    private static PlannedFix FullNameFix(ExpressionSyntax target, Func<ExpressionSyntax, string> replacement)
    {
        const string kind = "IdentityUser.FullName → Name + Surname";
        if (target.Parent is AnonymousObjectMemberDeclaratorSyntax { NameEquals: null } declarator)
        {
            return new PlannedFix(declarator, n =>
            {
                var d = (AnonymousObjectMemberDeclaratorSyntax)n;
                return SyntaxHelpers.NamedMember(d, "FullName", ParseExpression(replacement(d.Expression)));
            }, kind, []);
        }

        return new PlannedFix(target, n => ParseExpression(replacement((ExpressionSyntax)n)).WithTriviaFrom(n), kind, []);
    }

    private static PlannedFix? PlanRepositoryCall(InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax access, string member)
    {
        var isAsync = SyntaxHelpers.IsInsideAsyncFunction(invocation);

        if (RepositoryRenames.TryGetValue(member, out var renamed))
        {
            return new PlannedFix(access.Name, n => IdentifierName(renamed).WithTriviaFrom(n), $"repository {member} → {renamed}", []);
        }

        if (member is "GetAll" or "GetAllIncluding")
        {
            var newName = member == "GetAll" ? "GetQueryableAsync" : "WithDetailsAsync";
            return new PlannedFix(invocation, n => Awaited((InvocationExpressionSyntax)n, newName, isAsync, autoSave: false, takeId: false),
                $"repository {member}() → {newName}()", []);
        }

        if (SyncRepositoryMethods.TryGetValue(member, out var sync))
        {
            // As a statement the result is not used: no ".Id" (it would not be a valid statement).
            var takeId = sync.AddAutoSave && invocation.Parent is not ExpressionStatementSyntax;
            return new PlannedFix(invocation, n => Awaited((InvocationExpressionSyntax)n, sync.Name, isAsync, sync.AddAutoSave, takeId),
                $"repository {member} (sync) → {sync.Name}", []);
        }

        if (member == "CountAsync" && invocation.ArgumentList.Arguments.Count == 0)
        {
            return new PlannedFix(access.Name, n => IdentifierName("GetCountAsync").WithTriviaFrom(n), "repository CountAsync() → GetCountAsync()", []);
        }

        return null;
    }

    /// <summary>repo.X(args) → (await repo.XAsync(args)) in async code, repo.XAsync(args).GetAwaiter().GetResult() otherwise.</summary>
    private static ExpressionSyntax Awaited(InvocationExpressionSyntax invocation, string method, bool isAsync, bool autoSave, bool takeId)
    {
        var access = (MemberAccessExpressionSyntax)invocation.Expression;
        var args = invocation.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
        if (autoSave) args.Add("autoSave: true");
        var call = $"{access.Expression.WithoutTrivia()}.{method}({string.Join(", ", args)})";
        var expression = isAsync ? (invocation.Parent is ExpressionStatementSyntax && !takeId ? $"await {call}" : $"(await {call})") : $"{call}.GetAwaiter().GetResult()";
        if (takeId) expression = isAsync ? $"{expression}.Id" : $"({expression}).Id";
        return ParseExpression(expression).WithTriviaFrom(invocation);
    }

    // ---------- other fixes ----------

    private static PlannedFix? PlanLocalizedString(SyntaxToken token, string? member = null)
    {
        // CS1061 points at the member name, CS1929 at the start of the receiver: (L["x"]).Replace(...)
        var access = token.Parent?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(m =>
            m.Name.Span.Contains(token.Span) ||
            member is not null && m.Name.Identifier.Text == member && m.Expression.Span.Contains(token.Span));
        if (access is null) return null;
        return new PlannedFix(access, n =>
        {
            var a = (MemberAccessExpressionSyntax)n;
            return a.WithExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, a.Expression.WithoutTrailingTrivia(), IdentifierName("Value")));
        }, "LocalizedString used as string → .Value", []);
    }

    private static PlannedFix? PlanClockNow(SyntaxToken token)
    {
        var access = token.Parent?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault(m => m.Expression is IdentifierNameSyntax { Identifier.Text: "Clock" } && m.Name.Identifier.Text == "Now");
        return access is null ? null : new PlannedFix(access, n => ParseExpression("DateTime.Now").WithTriviaFrom(n), "static Clock.Now → DateTime.Now", ["System"]);
    }

    private static readonly Dictionary<string, string> IdentityUserSetters = new(StringComparer.Ordinal)
    {
        ["PhoneNumber"] = "{0}.SetPhoneNumber({1}, false);",
        ["IsActive"] = "{0}.SetIsActive({1});",
        ["EmailConfirmed"] = "{0}.SetEmailConfirmed({1});",
        ["PhoneNumberConfirmed"] = "{0}.SetPhoneNumberConfirmed({1});",
        ["ShouldChangePasswordOnNextLogin"] = "{0}.SetShouldChangePasswordOnNextLogin({1});",
    };

    private static PlannedFix? PlanSetter(MigrationContext context, SyntaxToken token, string type, string member)
    {
        // Object initializer: new Person { Id = id, Name = n } → new Person { Name = n }.SetProtected(e => e.Id, id)
        if (token.Parent is IdentifierNameSyntax { Parent: AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax { Parent: ObjectCreationExpressionSyntax creation } } init } name &&
            init.Left == name && name.Identifier.Text == member &&
            (member == "Id" || member is "CreatorId" or "LastModifierId" or "CreationTime" or "LastModificationTime"))
        {
            if (CompatShimStep.EnsureProtectedSetterShim(context) is not { } ns) return null;
            return new PlannedFix(creation, n =>
            {
                var c = (ObjectCreationExpressionSyntax)n;
                var assignments = c.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
                    .Where(a => a.Left is IdentifierNameSyntax { Identifier.Text: var t } && t is "Id" or "CreatorId" or "LastModifierId" or "CreationTime" or "LastModificationTime" &&
                                (t == member || t == "Id" && type.StartsWith("Entity<", StringComparison.Ordinal)))
                    .ToList();
                var remaining = c.Initializer.Expressions.Where(e => !assignments.Contains(e)).Select(e => e.WithoutTrivia());
                ExpressionSyntax result = c.WithInitializer(c.Initializer.WithExpressions(SeparatedList(remaining))).WithoutTrailingTrivia();
                foreach (var a in assignments)
                {
                    result = ParseExpression($"{result}.SetProtected(e => e.{((IdentifierNameSyntax)a.Left).Identifier.Text}, {a.Right.WithoutTrivia()})");
                }

                return result.WithTriviaFrom(c);
            }, $"{{ {member} = … }} in an object initializer → .SetProtected(e => e.{member}, …)", [ns]);
        }

        var statement = token.Parent?.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault();
        if (statement?.Expression is not AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression, Left: MemberAccessExpressionSyntax left } ||
            left.Name.Identifier.Text != member)
        {
            return null;
        }

        if (member == "Id" && type.StartsWith("Entity<", StringComparison.Ordinal))
        {
            return new PlannedFix(statement, n =>
            {
                var a = (AssignmentExpressionSyntax)((ExpressionStatementSyntax)n).Expression;
                var receiver = ((MemberAccessExpressionSyntax)a.Left).Expression.WithoutTrivia();
                var value = a.Right.WithoutTrivia();

                // "x.Id = (await repo.InsertAsync(x, autoSave: true)).Id": InsertAsync already sets the id.
                if (value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i =>
                        i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "InsertAsync" } &&
                        i.ArgumentList.Arguments.FirstOrDefault()?.ToString() == receiver.ToString()))
                {
                    var insert = value.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().First().WithoutTrivia();
                    return ParseStatement($"{insert};").WithTriviaFrom(n);
                }

                // An await cannot be inside the lambda: compute the value first.
                return value.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any()
                    ? ParseStatement($"{{ var newId = {value}; EntityHelper.TrySetId({receiver}, () => newId, true); }}").WithTriviaFrom(n)
                    : ParseStatement($"EntityHelper.TrySetId({receiver}, () => {value}, true);").WithTriviaFrom(n);
            }, "entity.Id = … → EntityHelper.TrySetId", ["Volo.Abp.Domain.Entities"]);
        }

        if (type == "IdentityUser" && IdentityUserSetters.TryGetValue(member, out var format))
        {
            return new PlannedFix(statement, n =>
            {
                var a = (AssignmentExpressionSyntax)((ExpressionStatementSyntax)n).Expression;
                var receiver = ((MemberAccessExpressionSyntax)a.Left).Expression.WithoutTrivia();
                return ParseStatement(string.Format(format, receiver, a.Right.WithoutTrivia())).WithTriviaFrom(n);
            }, $"IdentityUser.{member} = … → setter method", []);
        }

        if (member is "CreatorId" or "LastModifierId" or "DeleterId" or "CreationTime" or "LastModificationTime" or "DeletionTime")
        {
            return new PlannedFix(statement, n => ParseStatement("{ }")
                .WithLeadingTrivia(n.GetLeadingTrivia().Add(Comment($"/* AbpToVolo: {member} is set by ABP.io auditing — removed: {SyntaxHelpers.Text(n)} */")).Add(Space))
                .WithTrailingTrivia(n.GetTrailingTrivia()), $"{member} is set by ABP.io auditing (assignment removed)", []);
        }

        return null;
    }

    [GeneratedRegex(@"'(?<type>[^']+)' does not contain a definition for '(?<member>\w+)'")]
    private static partial Regex MissingMember();

    [GeneratedRegex(@"'(Microsoft\.Extensions\.Localization\.)?LocalizedString' does not contain a definition for '(?<member>\w+)'")]
    private static partial Regex LocalizedStringMember();

    [GeneratedRegex(@"An object reference is required for the non-static field, method, or property '(?<name>\w+)[.']")]
    private static partial Regex StaticReference();

    [GeneratedRegex(@"(convert|Cannot implicitly convert) (from )?type '(long|int)' to 'System\.Guid\??'|cannot convert from '(long|int)' to 'System\.Guid\??'|cannot convert from '(long|int)' to 'System\.Linq\.Expressions\.Expression<System\.Func<Volo\.Abp\.(Identity\.Identity(User|Role)|TenantManagement\.Tenant), bool>>'")]
    private static partial Regex GuidFromNumber();

    [GeneratedRegex(@"'[\w.]+\.(?<method>UpdateAsync|DeleteAsync|GetAsync)\([^)]*\)': no suitable method found to override")]
    private static partial Regex CrudOverride();

    [GeneratedRegex(@"Operator '(==|!=)' cannot be applied to operands of type '(Guid\??|(long|int)\??)' and '(Guid\??|(long|int)\??)'")]
    private static partial Regex GuidComparedToNumber();

    [GeneratedRegex(@"Argument (?<index>[23]): cannot convert from '[\w.]*Exception' to 'string\??'")]
    private static partial Regex ExceptionAsString();

    [GeneratedRegex(@"The name '(?<name>\w+)' does not exist in the current context")]
    private static partial Regex UnknownName();

    [GeneratedRegex(@"The property or indexer '(?<type>.+)\.(?<member>\w+)' cannot be used in this context because the set accessor is inaccessible")]
    private static partial Regex InaccessibleSetter();
}
