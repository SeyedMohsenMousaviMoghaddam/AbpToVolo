using AbpToVolo.Mappings;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AbpToVolo.CodeMigration.Rewriters;

/// <summary>
/// Runs last: maps Abp.* usings to Volo.Abp.*, drops Castle usings, adds the namespaces the other
/// rewriters required, and writes the file-level TODO block on top of the file.
/// </summary>
public sealed class UsingsRewriter : MigrationRewriter
{
    public UsingsRewriter(FileMigrationContext context) : base(context)
    {
    }

    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
        var (usings, carried) = Process(visited.Usings);
        visited = visited.WithUsings(List(usings));
        return carried.Count > 0 && visited.Members.Count > 0
            ? visited.ReplaceNode(visited.Members[0], visited.Members[0].WithLeadingTrivia(carried.Concat(visited.Members[0].GetLeadingTrivia())))
            : visited;
    }

    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
        var (usings, _) = Process(visited.Usings);
        return visited.WithUsings(List(usings));
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        var hadUsings = visited.Usings.Count > 0;
        var (usings, carried) = Process(visited.Usings);

        var existing = new HashSet<string>(usings.Where(u => u.Name is not null).Select(u => u.Name!.ToString()), StringComparer.Ordinal);
        foreach (var nested in visited.Members.OfType<BaseNamespaceDeclarationSyntax>().SelectMany(n => n.Usings))
        {
            if (nested.Name is not null) existing.Add(nested.Name.ToString());
        }

        var added = Context.RequiredUsings.Where(ns => !existing.Contains(ns)).Select(NewUsing).ToList();
        if (added.Count > 0)
        {
            if (!hadUsings)
            {
                // Separate the new using block from the code with an empty line.
                added[^1] = added[^1].WithTrailingTrivia(EndOfLine(Context.NewLine), EndOfLine(Context.NewLine));
            }

            usings.AddRange(added);
        }

        if (carried.Count > 0)
        {
            if (usings.Count > 0)
            {
                usings[0] = usings[0].WithLeadingTrivia(carried.Concat(usings[0].GetLeadingTrivia()));
            }
            else if (visited.Members.Count > 0)
            {
                visited = visited.ReplaceNode(visited.Members[0], visited.Members[0].WithLeadingTrivia(carried.Concat(visited.Members[0].GetLeadingTrivia())));
            }
        }

        visited = visited.WithUsings(List(usings));

        if (Context.FileTodos.Count > 0)
        {
            var header = new List<SyntaxTrivia> { Comment("// ---- AbpToVolo: manual work required ----"), EndOfLine(Context.NewLine) };
            foreach (var todo in Context.FileTodos)
            {
                header.Add(Comment("// " + SyntaxHelpers.TodoPrefix + todo));
                header.Add(EndOfLine(Context.NewLine));
            }

            header.Add(Comment("// -------------------------------------------"));
            header.Add(EndOfLine(Context.NewLine));
            visited = visited.WithLeadingTrivia(header.Concat(visited.GetLeadingTrivia()));
        }

        return visited;
    }

    private UsingDirectiveSyntax NewUsing(string ns) =>
        UsingDirective(ParseName(ns)).NormalizeWhitespace().WithTrailingTrivia(EndOfLine(Context.NewLine));

    private (List<UsingDirectiveSyntax> Usings, List<SyntaxTrivia> Carried) Process(SyntaxList<UsingDirectiveSyntax> original)
    {
        var result = new List<UsingDirectiveSyntax>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var carried = new List<SyntaxTrivia>();

        void Keep(UsingDirectiveSyntax directive, SyntaxTriviaList leading)
        {
            var key = directive.ToString().Trim();
            if (!seen.Add(key))
            {
                CarryComments(leading);
                return;
            }

            if (carried.Count > 0)
            {
                leading = TriviaList(carried.Concat(leading));
                carried.Clear();
            }

            result.Add(directive.WithLeadingTrivia(leading));
        }

        void CarryComments(SyntaxTriviaList leading)
        {
            if (leading.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
            {
                carried.AddRange(leading);
            }
        }

        foreach (var directive in original)
        {
            var name = directive.Name?.ToString();
            var leading = directive.GetLeadingTrivia();

            if (name is null || directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
            {
                Keep(directive, leading);
                continue;
            }

            if (name == "Castle.Core.Logging")
            {
                Context.Change("using Castle.Core.Logging → Microsoft.Extensions.Logging");
                Keep(NewUsing("Microsoft.Extensions.Logging"), leading);
                Keep(NewUsing("Microsoft.Extensions.Logging.Abstractions"), TriviaList());
                continue;
            }

            if (NamespaceMappings.IsDroppedThirdParty(name))
            {
                Context.Change($"using {name} removed (Castle Windsor is not used by ABP.io)");
                CarryComments(leading);
                continue;
            }

            if (!NamespaceMappings.IsBoilerplateNamespace(name))
            {
                Keep(directive, leading);
                continue;
            }

            if (directive.Alias is not null || directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            {
                Context.FileTodo($"'{directive.ToString().Trim()}' removed: alias/static using of a Boilerplate namespace.");
                CarryComments(leading);
                continue;
            }

            if (!NamespaceMappings.Map.TryGetValue(name, out var targets))
            {
                Context.FileTodo($"Namespace '{name}' has no known ABP.io equivalent; its using was removed.");
                CarryComments(leading);
                continue;
            }

            if (targets.Length == 0)
            {
                Context.Change($"using {name} removed");
                CarryComments(leading);
                continue;
            }

            Context.Change($"using {name} → {string.Join(", ", targets)}");
            for (var i = 0; i < targets.Length; i++)
            {
                Keep(NewUsing(targets[i]), i == 0 ? leading : TriviaList());
            }
        }

        return (result, carried);
    }
}
